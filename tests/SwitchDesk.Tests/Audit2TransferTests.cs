using System.IO;
using System.Text;
using System.Text.Json;
using SwitchDesk.Core;

namespace SwitchDesk.Tests;

internal static partial class Program
{
    private static async Task Audit2TransferTestsAsync()
    {
        await Test("failed folder download leaves the destination available for a full retry", async () =>
        {
            var directory = Directory.CreateDirectory(Path.Combine(root, "tree-retry")).FullName;
            var target = Path.Combine(directory, "folder");
            await using var peer = new Peer { FailRead = true }; await using var client = await peer.ConnectAsync();
            peer.SdFiles["/incoming/empty.bin"] = [];
            peer.SdFiles["/incoming/data.bin"] = [1, 2];
            await Throws<DeviceException>(() => Transfers.DownloadTreeAsync(client, "/incoming", target, null, default));
            Assert(!Directory.EnumerateFileSystemEntries(directory).Any());
            peer.FailRead = false;
            await Transfers.DownloadTreeAsync(client, "/incoming", target, null, default);
            Assert((await File.ReadAllBytesAsync(Path.Combine(target, "data.bin"))).SequenceEqual(new byte[] { 1, 2 }));
            Assert(File.Exists(Path.Combine(target, "empty.bin")));
        });
        await Test("all Homebrew install paths reject malformed NRO data before any device request", async () =>
        {
            var stream = new Audit2Duplex([]); await using var client = new DeviceClient(stream);
            var app = new HomebrewApp("Invalid fixture", "", "", "", "https://fixture.invalid/tool.nro", "tool.nro");
            var truncated = new byte[0x7f]; "NRO0"u8.CopyTo(truncated.AsSpan(0x10));
            foreach (var payload in new byte[][] { [], [1, 2, 3], Encoding.UTF8.GetBytes("<!doctype html><html>not an application</html>"), new byte[0x80], truncated })
                await Throws<InvalidDataException>(() => HomebrewStore.InstallAsync(client, app, payload, null, null));
            Assert((await stream.RequestMethodsAsync()).Length == 0 && !client.IsBroken,
                "invalid NRO content reached the protocol instead of being rejected locally");
        });
        await Test("cheat import preserves master blocks and removes input-only formatting before writing", async () =>
        {
            const string input = "\uFEFF# imported notes\r\n{Master}\r\n\r\n04000000 00000000 00000001 ; explanation\r\n\r\n[Regular]\r\n\r\n80000001\r\n04000000\t00000000 00000002\r\n\r\n20000000\r\n";
            var parsed = CheatManager.Parse(input);
            Assert(parsed.Length == 2 && parsed[0].IsMaster && !parsed[1].IsMaster && parsed[1].CodeLines.Length == 3);
            Expect<InvalidDataException>(() => CheatManager.Parse("{First}\n20000000\n{Second}\n20000000\n"));
            Expect<InvalidDataException>(() => CheatManager.Parse("[A]extra]\n20000000\n"));
            await using var peer = new Peer(); await using var client = await peer.ConnectAsync();
            await CheatManager.InstallAsync(client, Peer.Save.TitleId, "0123456789abcdef", Encoding.UTF8.GetBytes(input), null);
            const string expected = "{Master}\n04000000 00000000 00000001\n[Regular]\n80000001\n04000000 00000000 00000002\n20000000\n";
            Assert(Encoding.UTF8.GetString(peer.SdFiles[CheatManager.CheatPath(Peer.Save.TitleId, "0123456789abcdef")]) == expected,
                "the uploaded file still contains BOM/comments or lost master semantics");
            Assert(peer.ActiveHandleCount == 0);
        });
        await Test("invalid remote download sizes always release the acquired handle", async () =>
        {
            object[] invalidResults = [new { handle = "opened" }, new { handle = "opened", size = "bad" }, new { handle = "opened", size = -1L }];
            foreach (var result in invalidResults)
            {
                var stream = new Audit2Duplex(await Audit2Responses(result, new { }));
                await using var client = new DeviceClient(stream);
                var target = Path.Combine(root, "invalid-size-" + Guid.NewGuid().ToString("N") + ".bin");
                await Throws<InvalidDataException>(() => Transfers.DownloadAsync(client, "/fixture.bin", target, null, default));
                Assert(!File.Exists(target) && !Directory.GetFiles(root, Path.GetFileName(target) + ".*.partial").Any());
                Assert((await stream.RequestMethodsAsync()).SequenceEqual(new[] { "fs.read.open", "handle.close" }) && !client.IsBroken,
                    "metadata validation leaked the remote file or invalidated a synchronized connection");
            }
        });
        await Test("installation checks downloaded bytes despite unchanged length and timestamp", async () =>
        {
            var file = Path.Combine(root, "audited-install.nsp"); byte[] original = [1, 2, 3];
            await File.WriteAllBytesAsync(file, original); var stamp = File.GetLastWriteTimeUtc(file); var digest = Fingerprints.Of(original);
            await File.WriteAllBytesAsync(file, [3, 2, 1]); File.SetLastWriteTimeUtc(file, stamp);
            await using var peer = new Peer(); await using var client = await peer.ConnectAsync();
            await Throws<InvalidDataException>(() => Transfers.InstallAsync(client, file, "SdCard", null, default, digest, original.Length));
            Assert(peer.InstalledNames.Count == 0 && !client.IsBroken);
            await File.WriteAllBytesAsync(file, original);
            await Throws<InvalidDataException>(() => Transfers.InstallAsync(client, file, "SdCard", null, default, digest, 4));
            Assert(peer.InstalledNames.Count == 0);
            await Transfers.InstallAsync(client, file, "SdCard", null, default, digest, original.Length);
            Assert(peer.InstalledNames.SequenceEqual(new[] { Path.GetFileName(file) }));
        });
        await Test("FTP multiline banners terminate only at the matching response code", async () =>
        {
            using var server = new FtpMockServer { MultilineGreeting = true };
            await using var ftp = new FtpClient();
            await ftp.ConnectAsync(FtpOptions.Anonymous(server.Endpoint.Address.ToString(), server.Endpoint.Port));
            Assert(ftp.IsConnected && (await ftp.ListAsync("/")).Count == 2);
        });
        await Test("FTP passive port bytes must each be valid octets", async () =>
        {
            foreach (var reply in new[] { "Entering Passive Mode (127,0,0,1,1,-1)", "Entering Passive Mode (127,0,0,1,-1,300)" })
            {
                using var server = new FtpMockServer { PassiveReplyOverride = reply };
                await using var ftp = new FtpClient();
                await ftp.ConnectAsync(FtpOptions.Anonymous(server.Endpoint.Address.ToString(), server.Endpoint.Port));
                await Throws<InvalidDataException>(() => ftp.ListAsync("/"));
                Assert(ftp.IsConnected);
            }
        });
    }

    private static async Task<byte[]> Audit2Responses(params object[] results)
    {
        using var output = new MemoryStream(); var framed = new FramedConnection(output);
        for (var i = 0; i < results.Length; i++)
            await framed.WriteAsync(FrameKind.Response, JsonSerializer.SerializeToUtf8Bytes(new { id = i + 1, ok = true, result = results[i] }, Wire.Json), default);
        return output.ToArray();
    }

    private sealed class Audit2Duplex(byte[] replies) : Stream
    {
        private readonly MemoryStream response = new(replies);
        private readonly MemoryStream requests = new();
        public async Task<string[]> RequestMethodsAsync()
        {
            using var bytes = new MemoryStream(requests.ToArray()); var framed = new FramedConnection(bytes); var methods = new List<string>();
            while (bytes.Position < bytes.Length)
            {
                var frame = await framed.ReadAsync(default);
                using var json = JsonDocument.Parse(frame.Payload); methods.Add(json.RootElement.GetProperty("method").GetString()!);
            }
            return methods.ToArray();
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => response.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => response.ReadAsync(buffer, ct);
        public override void Write(byte[] buffer, int offset, int count) => requests.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => requests.WriteAsync(buffer, ct);
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { response.Dispose(); requests.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
