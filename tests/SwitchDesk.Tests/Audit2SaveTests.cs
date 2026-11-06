using System.Buffers;
using System.IO;
using System.Text.Json;
using SwitchDesk.App;
using SwitchDesk.Core;

namespace SwitchDesk.Tests;

internal static partial class Program
{
    private static async Task Audit2SaveTestsAsync()
    {
        await Test("durable new files clean up cancellation and preserve competing existing files", async () =>
        {
            var directory = Directory.CreateDirectory(Path.Combine(root, "durable-create")).FullName;
            var target = Path.Combine(directory, "protection.bin");
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await Throws<OperationCanceledException>(() => DurableFile.WriteNewAsync(target, new byte[] { 1, 2 }, cancellation.Token));
            Assert(!File.Exists(target), "cancelled write left an incomplete protection image");
            using (var unreadable = new FailingSnapshotMemory())
                await Throws<IOException>(() => DurableFile.WriteNewAsync(target, unreadable.Value, default));
            Assert(!File.Exists(target), "failed write left a newly created incomplete protection image");
            await DurableFile.WriteNewAsync(target, new byte[] { 3, 4, 5 }, default);
            await Throws<IOException>(() => DurableFile.WriteNewAsync(target, new byte[] { 9 }, default));
            Assert((await File.ReadAllBytesAsync(target)).SequenceEqual(new byte[] { 3, 4, 5 }), "CreateNew collision deleted or replaced the existing image");
        });
        await Test("snapshot export rejects a valid archive substituted after selection", async () =>
        {
            var directory = Path.Combine(root, "snapshot-export-selection");
            await using var peer = new Peer(); await using var client = await peer.ConnectAsync();
            var store = new SnapshotStore(directory);
            var selected = await store.BackupAsync(client, Peer.Info, Peer.Save, null, default);
            var original = await File.ReadAllBytesAsync(selected.Path);
            peer.Files["main.dat"] = [8, 7];
            var other = await store.BackupAsync(client, Peer.Info, Peer.Save, null, default);
            File.Copy(other.Path, selected.Path, overwrite: true);
            var destination = Path.Combine(directory, "exported");
            await Throws<InvalidDataException>(() => SnapshotTools.ExportSnapshotAsync(selected.Path, destination,
                expectedManifest: selected.Manifest));
            Assert(!Directory.Exists(destination), "substituted archive was exported under the selected version");
            await File.WriteAllBytesAsync(selected.Path, original);
            var exported = await SnapshotTools.ExportSnapshotAsync(selected.Path, destination, expectedManifest: selected.Manifest);
            Assert((await File.ReadAllBytesAsync(exported)).SequenceEqual(original));
            Assert(Directory.GetFiles(destination).SequenceEqual(new[] { exported }), "export left a partial file");
        });
        await Test("delayed history drag verifies selection and pins its file through reads and clones", async () =>
        {
            var directory = Path.Combine(root, "snapshot-drag-selection");
            await using var peer = new Peer(); await using var client = await peer.ConnectAsync();
            var store = new SnapshotStore(directory);
            var selected = await store.BackupAsync(client, Peer.Info, Peer.Save, null, default);
            var original = await File.ReadAllBytesAsync(selected.Path);
            peer.Files["main.dat"] = [3, 2];
            var other = await store.BackupAsync(client, Peer.Info, Peer.Save, null, default);
            var source = new Lazy<Task<string>>(() => Task.FromResult(selected.Path));
            _ = await source.Value; File.Copy(other.Path, selected.Path, overwrite: true);
            using (var rejected = new SnapshotStream(source, selected.Manifest))
                await Throws<InvalidDataException>(() => Sync(() => rejected.Read(new byte[4], 4, 0)));
            await File.WriteAllBytesAsync(selected.Path, original);
            using (var stream = new SnapshotStream(source, selected.Manifest))
            {
                var bytes = new byte[4]; stream.Read(bytes, bytes.Length, 0);
                Assert(bytes.SequenceEqual(original.Take(4)));
                var locked = false;
                try { File.Copy(other.Path, selected.Path, overwrite: true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { locked = true; }
                Assert(locked, "verified drag source was replaceable during extraction");
                stream.Clone(out var clone);
                using ((IDisposable)clone)
                {
                    clone.Read(bytes, bytes.Length, 0);
                    Assert(bytes.SequenceEqual(original.Skip(4).Take(4)), "clone lost the verified source or stream position");
                }
            }
            File.Copy(other.Path, selected.Path, overwrite: true); // All drag handles were released.
        });
        await Test("a completed restore stays successful when its local completion log is locked", async () =>
        {
            var directory = Path.Combine(root, "restore-complete-log");
            await using var peer = new Peer(); await using var client = await peer.ConnectAsync();
            var store = new SnapshotStore(directory);
            var selected = await store.BackupAsync(client, Peer.Info, Peer.Save, null, default);
            peer.Files["main.dat"] = [9];
            FileStream? lockedLog = null;
            peer.OnRestoreCommitted = () => lockedLog = File.Open(
                Directory.GetFiles(Path.Combine(directory, "Recovery"), "*.restore.json", SearchOption.AllDirectories).Single(),
                FileMode.Open, FileAccess.Read, FileShare.Read);
            SnapshotRestoreResult result;
            try { result = await store.RestoreAsync(client, Peer.Info, Peer.Save, selected.Path, null, default); }
            finally { lockedLog?.Dispose(); }
            Assert(result.Warning?.Contains("无需重复写回") == true && peer.RestoreCommits == 1 && peer.ActiveHandleCount == 0);
            Assert(peer.Files["main.dat"].SequenceEqual(new byte[] { 1, 2, 3, 4, 5 }));
            await SnapshotStore.VerifyAsync(result.RecoveryPath);
            using var log = JsonDocument.Parse(await File.ReadAllTextAsync(result.RecoveryPath + ".restore.json"));
            Assert(log.RootElement.GetProperty("state").GetString() == "staging", "successful commit was falsely recorded as failed");
        });
        await Test("restore and conversion refuse a valid ZIP substituted after source selection", async () =>
        {
            var directory = Path.Combine(root, "snapshot-selection");
            await using var peer = new Peer(); await using var client = await peer.ConnectAsync();
            var store = new SnapshotStore(directory);
            var selected = await store.BackupAsync(client, Peer.Info, Peer.Save, null, default);
            peer.Files["main.dat"] = [4, 3, 2, 1];
            var other = await store.BackupAsync(client, Peer.Info, Peer.Save, null, default);
            File.Copy(other.Path, selected.Path, overwrite: true);
            var reads = peer.ReadOpens;
            await Throws<InvalidDataException>(() => store.RestoreAsync(client, Peer.Info, Peer.Save, selected.Path, null,
                default, expectedManifest: selected.Manifest));
            await Throws<InvalidDataException>(() => store.RestoreAsync(client, Peer.Info, Peer.Save, selected.Path, null,
                default, foreignSource: true, expectedManifest: other.Manifest with { Environment = "other-system" }));
            var destination = Path.Combine(directory, "converted.zip");
            await Throws<InvalidDataException>(() => SnapshotStore.CreateConvertedTargetAsync(selected.Path, "main.dat",
                new byte[] { 1 }, destination, default, selected.Manifest));
            Assert(peer.ReadOpens == reads && peer.WriteOperations == 0 && !File.Exists(destination));
        });
        await Test("restore stages bytes from the same archive that passed verification", async () =>
        {
            await using var peer = new Peer(); await using var client = await peer.ConnectAsync();
            var store = new SnapshotStore(Path.Combine(root, "snapshot-open-handle"));
            var original = peer.Files["main.dat"].ToArray();
            var selected = await store.BackupAsync(client, Peer.Info, Peer.Save, null, default);
            peer.Files["main.dat"] = [8];
            var other = await store.BackupAsync(client, Peer.Info, Peer.Save, null, default);
            peer.OnRestoreBegin = () =>
            {
                try { File.Move(other.Path, selected.Path, overwrite: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { /* Windows denies replacement while the verified archive is open. */ }
            };
            await store.RestoreAsync(client, Peer.Info, Peer.Save, selected.Path, null, default, expectedManifest: selected.Manifest);
            Assert(peer.RestoreCommits == 1 && peer.Files["main.dat"].SequenceEqual(original));
        });
        await Test("PC migration refuses a corrupt source protection image before replacing the target", async () =>
        {
            var directory = Path.Combine(root, "audit2-pc-source-protection"); Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "user1.dat"); var original = KnightSaveAdapter.EncodePc(KnightJson(1));
            await File.WriteAllBytesAsync(path, original);
            using var state = new Phase2State(Path.Combine(directory, "state.db"));
            var workspace = new MigrationWorkspace(state, directory, KnightCompatibility());
            var operation = await workspace.PrepareAsync(FixturePair(path), new KnightSaveAdapter(false), SavePlatform.Switch,
                original, KnightJson(2), [], [], default, FixtureVersions);
            await File.WriteAllBytesAsync(Path.Combine(operation.RecoveryDirectory, "source.bin"), [9]);
            await Throws<InvalidDataException>(() => workspace.CommitPcAsync(operation.Id, () => Task.CompletedTask, default));
            Assert((await File.ReadAllBytesAsync(path)).SequenceEqual(original));
            Assert(state.Get<MigrationOperation>("migrations", operation.Id)!.Value.Value.State == MigrationState.Prepared);
            Assert(!Directory.GetFiles(directory, "*.tmp").Any());
        });
        await Test("Switch migration refuses a corrupt source protection image before any device writes", async () =>
        {
            var directory = Path.Combine(root, "audit2-switch-source-protection"); Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "user1.dat"); var pc = KnightSaveAdapter.EncodePc(KnightJson(42));
            await File.WriteAllBytesAsync(path, pc); var nx = KnightJson(1);
            await using var peer = new Peer(); peer.Files["main.dat"] = nx;
            await using var client = await peer.ConnectAsync(); var snapshots = new SnapshotStore(Path.Combine(directory, "backups"));
            var target = await snapshots.BackupAsync(client, Peer.Info, Peer.Save, null, default);
            var inspection = await client.QueryAsync<SaveDeviceInspection>("saves.inspect");
            var pair = FixturePair(path) with { DeviceId = Peer.Info.DeviceId, Environment = Peer.Info.Environment, TitleId = Peer.Save.TitleId,
                UserId = Peer.Save.UserId, SaveKey = Peer.Save.Key, SwitchPath = "main.dat" };
            using var state = new Phase2State(Path.Combine(directory, "state.db"));
            var workspace = new MigrationWorkspace(state, directory, KnightCompatibility());
            var operation = await workspace.PrepareAsync(pair, new KnightSaveAdapter(false), SavePlatform.Steam, pc, nx, [], [], default, FixtureVersions);
            await File.WriteAllBytesAsync(Path.Combine(operation.RecoveryDirectory, "source.bin"), [9]);
            await Throws<InvalidDataException>(() => workspace.CommitSwitchAsync(operation.Id, pair, client, Peer.Info, Peer.Save,
                target.Path, inspection.Fingerprint, snapshots, () => Task.CompletedTask, null, default));
            Assert(peer.WriteOperations == 0 && peer.RestoreCommits == 0 && peer.Files["main.dat"].SequenceEqual(nx));
            Assert((await File.ReadAllBytesAsync(path)).SequenceEqual(pc));
        });
        await Test("PC snapshot discovery skips incomplete manifests without losing their original files", async () =>
        {
            for (var variant = 0; variant < 3; variant++)
            {
                var directory = Path.Combine(root, "audit2-pc-malformed-" + variant); Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "slot.dat"); await File.WriteAllBytesAsync(path, [1]);
                var source = new PcSaveCandidate("hades", path, "Windows fixture", "1", "fixture");
                var file = await StableSaveFile.ReadAsync(path, default); var store = new PcSnapshotStore(Path.Combine(directory, "history"));
                var first = await store.BackupAsync(source, file, Reading(1), 1, default);
                var malformed = variant switch
                {
                    0 => "{}",
                    1 => JsonSerializer.Serialize(first with { Reading = null! }, Wire.Json),
                    _ => JsonSerializer.Serialize(first with { Source = null! }, Wire.Json)
                };
                await File.WriteAllTextAsync(Path.Combine(first.Directory, "snapshot.json"), malformed);
                var replacement = await store.BackupAsync(source, file, Reading(1), 1, default);
                Assert(replacement.Directory != first.Directory && File.Exists(Path.Combine(first.Directory, "save.bin")));
                await PcSnapshotStore.VerifyAsync(replacement, default);
            }
        });
        await Test("PC snapshot duplicate reuse applies reduced retention and keeps the currently observed image", async () =>
        {
            var directory = Path.Combine(root, "audit2-pc-reuse-retention"); Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "slot.dat"); var history = Path.Combine(directory, "history");
            var source = new PcSaveCandidate("hades", path, "Windows fixture", "1", "fixture");
            var store = new PcSnapshotStore(history);
            await File.WriteAllBytesAsync(path, [1]); var firstFile = await StableSaveFile.ReadAsync(path, default);
            var first = await store.BackupAsync(source, firstFile, Reading(1), 3, default);
            await File.WriteAllBytesAsync(path, [2]); var secondFile = await StableSaveFile.ReadAsync(path, default);
            var second = await store.BackupAsync(source, secondFile, Reading(2), 3, default);
            await File.WriteAllTextAsync(Path.Combine(second.Directory, "user-note.txt"), "keep user file");
            var recovery = Path.Combine(history, "Recovery", "protected.bin"); Directory.CreateDirectory(Path.GetDirectoryName(recovery)!);
            await File.WriteAllBytesAsync(recovery, [9]);
            await File.WriteAllBytesAsync(path, [1]); firstFile = await StableSaveFile.ReadAsync(path, default);
            var duplicate = await store.BackupAsync(source, firstFile, Reading(1), 1, default);
            Assert(duplicate.Directory == first.Directory && Directory.GetFiles(history, "snapshot.json", SearchOption.AllDirectories).Length == 1);
            await PcSnapshotStore.VerifyAsync(duplicate, default);
            Assert(File.Exists(Path.Combine(second.Directory, "user-note.txt")) && File.Exists(recovery));
        });
        await Test("PC snapshot history cannot reuse or prune a record with a different source identity", async () =>
        {
            var directory = Path.Combine(root, "audit2-pc-source-scope"); Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "slot.dat"); await File.WriteAllBytesAsync(path, [3]);
            var source = new PcSaveCandidate("hades", path, "Windows fixture", "1", "fixture");
            var file = await StableSaveFile.ReadAsync(path, default); var store = new PcSnapshotStore(Path.Combine(directory, "history"));
            var first = await store.BackupAsync(source, file, Reading(3), 1, default);
            await File.WriteAllTextAsync(Path.Combine(first.Directory, "snapshot.json"), JsonSerializer.Serialize(first with { Source = source with { Owner = "different-account" } }, Wire.Json));
            var current = await store.BackupAsync(source, file, Reading(3), 1, default);
            Assert(current.Directory != first.Directory && current.Source == source);
            Assert(File.Exists(Path.Combine(first.Directory, "save.bin")), "history from another source was pruned");
            await Throws<InvalidDataException>(() => PcSnapshotStore.VerifyAsync(current with { Reading = null! }, default));
        });
    }

    private sealed class FailingSnapshotMemory : MemoryManager<byte>
    {
        internal Memory<byte> Value => CreateMemory(4);
        public override Span<byte> GetSpan() => throw new IOException("fixture failed while reading write buffer");
        public override MemoryHandle Pin(int elementIndex = 0) => throw new IOException("fixture failed while pinning write buffer");
        public override void Unpin() { }
        protected override void Dispose(bool disposing) { }
    }
}
