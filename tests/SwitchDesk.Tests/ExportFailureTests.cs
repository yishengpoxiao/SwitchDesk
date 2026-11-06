using System.IO;
using SwitchDesk.Core;

namespace SwitchDesk.Tests;

internal static partial class Program
{
    private static async Task ExportFailureTestsAsync()
    {
        await Test("safe filenames preserve Unicode pairs and trim alternating trailing spaces and dots", () => Sync(() =>
        {
            Assert(PathSafety.SafeName(new string('a', 119) + "🚀tail") == new string('a', 119));
            Assert(PathSafety.SafeName(new string('a', 118) + "🚀tail") == new string('a', 118) + "🚀");
            Assert(PathSafety.SafeName("🚀", 1) == "_");
            Assert(PathSafety.SafeName("name. . ") == "name");
            Assert(PathSafety.SafeName("ordinary title") == "ordinary title");
            Expect<ArgumentOutOfRangeException>(() => PathSafety.SafeName("name", 0));
        }));
        await Test("long snapshot game and user names export without exceeding the filesystem filename limit", async () =>
        {
            var directory = Path.Combine(root, "long-snapshot-export");
            await using var peer = new Peer(); await using var client = await peer.ConnectAsync();
            var store = new SnapshotStore(Path.Combine(root, "long-snapshot-source"));
            var save = Peer.Save with { GameName = new string('游', 119) + "🚀title", UserName = new string('名', 119) + "🚀user" };
            var snapshot = await store.BackupAsync(client, Peer.Info, save, null, default);
            var first = await SnapshotTools.ExportSnapshotAsync(snapshot.Path, directory);
            var second = await SnapshotTools.ExportSnapshotAsync(snapshot.Path, directory);
            Assert(first != second && Path.GetFileName(first).Length <= 255 && Path.GetFileName(second).Length <= 255);
            Assert(System.Text.Encoding.UTF8.GetByteCount(Path.GetFileName(first)) <= 255 && System.Text.Encoding.UTF8.GetByteCount(Path.GetFileName(second)) <= 255);
            Assert((await SnapshotStore.VerifyAsync(first)).Manifest.GameName == save.GameName);
            Assert((await SnapshotStore.VerifyAsync(second)).Manifest.UserName == save.UserName);
            Assert(Directory.EnumerateFiles(directory).Count() == 2);
        });
        await Test("export read failure closes handle and publishes no incomplete content", async () =>
        {
            var directory = Path.Combine(root, "export-failure");
            await using var peer = new Peer { FailRead = true };
            await using var client = await peer.ConnectAsync();
            await Throws<DeviceException>(() => Transfers.ExportGameAsync(client, Peer.Info,
                new(Peer.Save.TitleId, "Failed export", "", "1", "已安装", []),
                new(Peer.Save.TitleId, "Base", 0, "SdCard", 5), directory, null, default));
            Assert(peer.ExportOpens == 1 && peer.CloseOperations == 1 && peer.ActiveHandleCount == 0);
            Assert(!Directory.EnumerateFiles(directory).Any(), "failed export left a file or manifest");
        });
        await Test("cancelled export between chunks cleans only its own partials and closes handle", async () =>
        {
            var directory = Path.Combine(root, "export-cancel"); Directory.CreateDirectory(directory);
            var unrelated = Path.Combine(directory, "other-task.partial"); await File.WriteAllTextAsync(unrelated, "keep");
            await using var peer = new Peer(); peer.Files["main.dat"] = new byte[Wire.ChunkSize + 19];
            await using var client = await peer.ConnectAsync(); using var cancellation = new CancellationTokenSource();
            await Throws<OperationCanceledException>(() => Transfers.ExportGameAsync(client, Peer.Info,
                new(Peer.Save.TitleId, "Cancelled export", "", "1", "已安装", []),
                new(Peer.Save.TitleId, "Base", 0, "SdCard", peer.Files["main.dat"].Length), directory,
                new CancelExportProgress(cancellation), cancellation.Token));
            Assert(peer.CloseOperations == 1 && peer.ActiveHandleCount == 0);
            Assert(Directory.EnumerateFiles(directory).SequenceEqual(new[] { unrelated }));
            Assert(await File.ReadAllTextAsync(unrelated) == "keep");
        });
    }

    private sealed class CancelExportProgress(CancellationTokenSource cancellation) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) { if (value.Completed > 0) cancellation.Cancel(); }
    }
}
