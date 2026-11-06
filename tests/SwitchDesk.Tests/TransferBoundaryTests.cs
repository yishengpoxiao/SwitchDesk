using System.IO;
using SwitchDesk.Core;

namespace SwitchDesk.Tests;

internal static partial class Program
{
    private static async Task TransferBoundaryTestsAsync()
    {
        await Test("USB download preserves a legal filename near the component limit", async () =>
        {
            var directory = Directory.CreateDirectory(Path.Combine(root, "usb-long-download")).FullName;
            var destination = Path.Combine(directory, new string('a', 235) + ".bin");
            await using var peer = new Peer();
            peer.SdFiles["/incoming/long.bin"] = [4, 5, 6];
            await using var client = await peer.ConnectAsync();
            await Transfers.DownloadAsync(client, "/incoming/long.bin", destination, null, default);
            Assert((await File.ReadAllBytesAsync(destination)).SequenceEqual(new byte[] { 4, 5, 6 }));
            Assert(Directory.GetFiles(directory).SequenceEqual(new[] { destination }) && peer.ActiveHandleCount == 0);
            await Throws<IOException>(() => Transfers.DownloadAsync(client, "/incoming/long.bin", destination, null, default));
            Assert((await File.ReadAllBytesAsync(destination)).SequenceEqual(new byte[] { 4, 5, 6 }));
        });
        await Test("FTP download preserves a legal filename near the component limit", async () =>
        {
            var directory = Directory.CreateDirectory(Path.Combine(root, "ftp-long-download")).FullName;
            var destination = Path.Combine(directory, new string('b', 235) + ".bin");
            using var server = new FtpMockServer();
            await using var ftp = new FtpClient();
            await ftp.ConnectAsync(FtpOptions.Anonymous(server.Endpoint.Address.ToString(), server.Endpoint.Port));
            await ftp.DownloadAsync("/readme.txt", destination, null);
            Assert((await File.ReadAllBytesAsync(destination)).SequenceEqual(server.Files["/readme.txt"]));
            Assert(Directory.GetFiles(directory).SequenceEqual(new[] { destination }));
            await Throws<IOException>(() => ftp.DownloadAsync("/readme.txt", destination, null));
            Assert(server.Downloads == 1);
        });
        await Test("FTP cancellation waits for delayed transfer replies before reusing control", async () =>
        {
            using var server = new FtpMockServer { RetrFinalReplyDelayMs = 1300 };
            await using var ftp = new FtpClient();
            await ftp.ConnectAsync(FtpOptions.Anonymous(server.Endpoint.Address.ToString(), server.Endpoint.Port));
            using var cancellation = new CancellationTokenSource();
            var directory = Directory.CreateDirectory(Path.Combine(root, "ftp-late-reply")).FullName;
            var download = ftp.DownloadAsync("/readme.txt", Path.Combine(directory, "cancelled.bin"), null, cancellation.Token);
            await server.RetrAwaitingFinalReply.Task.WaitAsync(TimeSpan.FromSeconds(3));
            cancellation.Cancel();
            await Throws<OperationCanceledException>(() => download);
            Assert(ftp.IsConnected && !Directory.GetFiles(directory).Any());
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            Assert((await ftp.ListAsync("/", timeout.Token)).Count == 2, "a delayed RETR reply was consumed as a later command's reply");
        });
        await Test("FTP cancellation closes control when reply synchronization times out", async () =>
        {
            using var server = new FtpMockServer { RetrFinalReplyDelayMs = 3500 };
            await using var ftp = new FtpClient();
            await ftp.ConnectAsync(FtpOptions.Anonymous(server.Endpoint.Address.ToString(), server.Endpoint.Port));
            using var cancellation = new CancellationTokenSource();
            var directory = Directory.CreateDirectory(Path.Combine(root, "ftp-unsynchronized")).FullName;
            var download = ftp.DownloadAsync("/readme.txt", Path.Combine(directory, "cancelled.bin"), null, cancellation.Token);
            await server.RetrAwaitingFinalReply.Task.WaitAsync(TimeSpan.FromSeconds(3));
            cancellation.Cancel();
            await Throws<OperationCanceledException>(() => download);
            Assert(!ftp.IsConnected && !Directory.GetFiles(directory).Any());
            await Throws<IOException>(() => ftp.MkdirAsync("/unexpected"));
            Assert(!server.Directories.Contains("/unexpected"));
        });
    }
}
