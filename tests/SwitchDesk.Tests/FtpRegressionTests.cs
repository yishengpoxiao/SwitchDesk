using System.IO;
using SwitchDesk.Core;

namespace SwitchDesk.Tests;

internal static partial class Program
{
    private static async Task RunFtpRegressionTestsAsync()
    {
        await Test("ftp upload never interprets unsupported SIZE as an absent destination", async () =>
        {
            using var server = new FtpMockServer { NoSize = true };
            await using var ftp = new FtpClient();
            await ftp.ConnectAsync(FtpOptions.Anonymous(server.Endpoint.Address.ToString(), server.Endpoint.Port));
            var local = Path.Combine(root, "ftp-size-unsupported.bin"); await File.WriteAllBytesAsync(local, [7, 8]);
            var original = server.Files["/readme.txt"].ToArray();
            await Throws<IOException>(() => ftp.UploadAsync(local, "/readme.txt", null));
            await Throws<IOException>(() => ftp.UploadAsync(local, "/switch", null));
            Assert(server.Uploads == 0 && server.Files["/readme.txt"].SequenceEqual(original));
            await ftp.UploadAsync(local, "/new.bin", null);
            Assert(server.Files["/new.bin"].SequenceEqual(new byte[] { 7, 8 }));
            Assert(!server.Files.Keys.Any(path => path.EndsWith(".partial", StringComparison.Ordinal)));
        });
        await Test("ftp publish rechecks a target created while the temporary file was uploading", async () =>
        {
            using var server = new FtpMockServer();
            server.AfterUpload = _ => server.Files["/raced.bin"] = [9, 9];
            await using var ftp = new FtpClient();
            await ftp.ConnectAsync(FtpOptions.Anonymous(server.Endpoint.Address.ToString(), server.Endpoint.Port));
            var local = Path.Combine(root, "ftp-publish-conflict.bin"); await File.WriteAllBytesAsync(local, [1, 2]);
            await Throws<IOException>(() => ftp.UploadAsync(local, "/raced.bin", null));
            Assert(server.Uploads == 1 && server.Files["/raced.bin"].SequenceEqual(new byte[] { 9, 9 }));
            Assert(!server.Files.Keys.Any(path => path.EndsWith(".partial", StringComparison.Ordinal)));
        });
        await Test("ftp servers without rename cannot silently fall back to non-atomic upload", async () =>
        {
            using var server = new FtpMockServer { NoRename = true };
            await using var ftp = new FtpClient();
            await ftp.ConnectAsync(FtpOptions.Anonymous(server.Endpoint.Address.ToString(), server.Endpoint.Port));
            var local = Path.Combine(root, "ftp-no-rename.bin"); await File.WriteAllBytesAsync(local, [3, 4]);
            await Throws<IOException>(() => ftp.UploadAsync(local, "/new.bin", null));
            Assert(server.Uploads == 1 && !server.Files.ContainsKey("/new.bin"));
            Assert(!server.Files.Keys.Any(path => path.EndsWith(".partial", StringComparison.Ordinal)));
        });
        await Test("ftp listings preserve punctuation in real filenames", async () =>
        {
            foreach (var legacy in new[] { false, true })
            {
                using var server = new FtpMockServer { NoMlsd = legacy };
                server.Files["/before; after.txt"] = [7, 8];
                server.Files["/before -> after.txt"] = [9, 10];
                await using var ftp = new FtpClient();
                await ftp.ConnectAsync(FtpOptions.Anonymous(server.Endpoint.Address.ToString(), server.Endpoint.Port));
                var entries = await ftp.ListAsync("/");
                foreach (var name in new[] { "before; after.txt", "before -> after.txt" })
                {
                    var entry = entries.Single(row => row.Name == name);
                    Assert(entry.Path == "/" + name && !entry.IsDirectory && entry.Size == 2);
                }
                var local = Path.Combine(root, "ftp-name-" + legacy + ".bin");
                await ftp.DownloadAsync(entries.Single(row => row.Name == "before; after.txt").Path, local, null);
                Assert((await File.ReadAllBytesAsync(local)).SequenceEqual(new byte[] { 7, 8 }));
            }
        });
        await Test("ftp empty folder upload verifies failed directory creation", async () =>
        {
            using var server = new FtpMockServer { RejectMkdir = true };
            await using var ftp = new FtpClient();
            await ftp.ConnectAsync(FtpOptions.Anonymous(server.Endpoint.Address.ToString(), server.Endpoint.Port));
            var local = Directory.CreateDirectory(Path.Combine(root, "ftp-empty-folder")).FullName;
            await Throws<IOException>(() => ftp.UploadTreeAsync(local, "/denied", null));
            Assert(!server.Directories.Contains("/denied") && server.Uploads == 0);
            // A 550 for an already existing directory is still supported.
            await ftp.UploadTreeAsync(local, "/switch", null);
        });
        await Test("ftp cancelled metadata request closes an unsynchronized connection", async () =>
        {
            using var server = new FtpMockServer { SizeReplyDelayMs = 300 };
            await using var ftp = new FtpClient();
            await ftp.ConnectAsync(FtpOptions.Anonymous(server.Endpoint.Address.ToString(), server.Endpoint.Port));
            using var cancellation = new CancellationTokenSource();
            var local = Path.Combine(root, "ftp-size-cancel.bin");
            var download = ftp.DownloadAsync("/readme.txt", local, null, cancellation.Token);
            await server.SizeRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();
            await Throws<OperationCanceledException>(() => download);
            Assert(!ftp.IsConnected && !File.Exists(local));
            await Throws<IOException>(() => ftp.MkdirAsync("/unexpected"));
            Assert(!server.Directories.Contains("/unexpected"));
        });
        await Test("protected subtrees cannot be moved or removed via their parents", async () =>
        {
            // Protection must apply before sending any FTP command, even for the rename destination.
            await using var ftp = new FtpClient();
            foreach (var path in new[] { "/Switch/", "/CONFIG" })
            {
                await Throws<DeviceException>(() => ftp.RenameAsync(path, "/moved"));
                await Throws<DeviceException>(() => ftp.RenameAsync("/other", path));
                await Throws<DeviceException>(() => ftp.DeleteAsync(path));
                await Throws<DeviceException>(() => ftp.RemoveDirectoryAsync(path));
                PathSafety.DemandWritable(path);
            }
            PathSafety.DemandWritable("/switch/another-app", protectDescendants: true);
        });
        await Test("ftp rejects a successful response with truncated content", async () =>
        {
            using var server = new FtpMockServer { TruncateSuccessfulRetr = true };
            await using var ftp = new FtpClient();
            await ftp.ConnectAsync(FtpOptions.Anonymous(server.Endpoint.Address.ToString(), server.Endpoint.Port));
            var destination = Path.Combine(root, "ftp-truncated-success.bin");
            await Throws<EndOfStreamException>(() => ftp.DownloadAsync("/readme.txt", destination, null));
            Assert(!File.Exists(destination) && !Directory.GetFiles(root, "ftp-truncated-success.bin.*.partial").Any());
            Assert((await ftp.ListAsync("/")).Count == 2, "length mismatch must consume the final FTP reply");
        });
        await Test("ftp cancelled directory listing keeps subsequent commands synchronized", async () =>
        {
            using var server = new FtpMockServer { ListDelayMs = 300 };
            await using var ftp = new FtpClient();
            await ftp.ConnectAsync(FtpOptions.Anonymous(server.Endpoint.Address.ToString(), server.Endpoint.Port));
            using var cancellation = new CancellationTokenSource(75);
            await Throws<OperationCanceledException>(() => ftp.ListAsync("/", cancellation.Token));
            server.ListDelayMs = 0;
            Assert((await ftp.ListAsync("/")).Count == 2);
        });
        await Test("ftp cancellation while awaiting final transfer reply keeps control synchronized", async () =>
        {
            using var server = new FtpMockServer { RetrFinalReplyDelayMs = 400 };
            await using var ftp = new FtpClient();
            await ftp.ConnectAsync(FtpOptions.Anonymous(server.Endpoint.Address.ToString(), server.Endpoint.Port));
            using var cancellation = new CancellationTokenSource();
            var destination = Path.Combine(root, "ftp-cancel-final-reply.bin");
            var download = ftp.DownloadAsync("/readme.txt", destination, null, cancellation.Token);
            await server.RetrAwaitingFinalReply.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(50); // Let the client observe data EOF and wait for the delayed 226.
            Assert(!download.IsCompleted);
            cancellation.Cancel();
            await Throws<OperationCanceledException>(() => download);
            Assert(!File.Exists(destination) && !Directory.GetFiles(root, "ftp-cancel-final-reply.bin.*.partial").Any());
            Assert((await ftp.ListAsync("/")).Count == 2);
        });
        await Test("ftp disconnect honors cancellation when QUIT receives no reply", async () =>
        {
            using var server = new FtpMockServer { IgnoreQuit = true };
            using var ftp = new FtpClient();
            await ftp.ConnectAsync(FtpOptions.Anonymous(server.Endpoint.Address.ToString(), server.Endpoint.Port));
            using var cancellation = new CancellationTokenSource(75);
            await ftp.DisconnectAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(2));
            Assert(!ftp.IsConnected);
        });
        await Test("ftp dispose clears connection state", async () =>
        {
            using var server = new FtpMockServer();
            using var ftp = new FtpClient();
            await ftp.ConnectAsync(FtpOptions.Anonymous(server.Endpoint.Address.ToString(), server.Endpoint.Port));
            ftp.Dispose();
            Assert(!ftp.IsConnected);
        });
    }
}
