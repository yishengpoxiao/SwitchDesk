using System.IO;
using System.Text.Json;
using SwitchDesk.Core;

namespace SwitchDesk.Tests;

internal static partial class Program
{
    private static async Task SaveSyncRegressionTests()
    {
        var directory = Path.Combine(root, "save-sync-review");
        Snapshot Image(string name, byte content, DateTimeOffset created, string? deviceId = null) =>
            new(Path.Combine(directory, name), new(1, deviceId ?? Peer.Info.DeviceId, Peer.Info.Environment,
                Peer.Save.TitleId, Peer.Save.GameName, Peer.Save.UserId, Peer.Save.UserName, Peer.Save.Type,
                created, [], Fingerprints.Of([content]), []));

        await Test("save sync state rejects malformed identities and ambiguous duplicate baselines", () => Sync(() =>
        {
            var stateDirectory = Path.Combine(directory, "state"); Directory.CreateDirectory(stateDirectory);
            var now = DateTimeOffset.UtcNow;
            var state = SaveSync.RecordSync(Peer.Save with { TitleId = "01000000000000AB" }, Peer.Info, Fingerprints.Of([1]), Fingerprints.Of([2]), now);
            var valid = state with { UserId = "OTHER-USER", TitleId = state.TitleId.ToLowerInvariant() };
            var values = new[] { state, state with { TitleId = state.TitleId.ToLowerInvariant(), LastSyncedAt = now.AddDays(1) },
                state with { TitleId = null! }, state with { SaveType = "unknown" }, state with { LastDeviceFingerprint = "broken" }, valid };
            File.WriteAllText(Path.Combine(stateDirectory, "save-sync.json"), JsonSerializer.Serialize(values, Wire.Json));
            var loaded = new SaveSyncStore(stateDirectory).Load();
            Assert(loaded.Count == 1 && loaded[0].UserId == valid.UserId && loaded[0].TitleId == state.TitleId);
            Assert(valid.Key == SaveSync.StateKey(Peer.Save with { TitleId = valid.TitleId, UserId = valid.UserId }, Peer.Info));
        }));

        await Test("save sync excludes other consoles and nested recovery backups", async () =>
        {
            await using var peer = new Peer(); await using var client = await peer.ConnectAsync();
            var now = DateTimeOffset.UtcNow;
            var foreign = Image("foreign.zip", 1, now, "OTHER-DEVICE");
            var recovery = Image(Path.Combine("Recovery", "device", "environment", "title", "user", "protected.zip"), 2, now);
            var states = new Dictionary<string, SaveSyncState>();
            var plan = (await SaveSync.BuildPlansAsync(client, Peer.Info, [Peer.Save], [foreign, recovery], states, null, default)).Single();
            Assert(plan.Direction == SaveSyncDirection.NeedsFirstBackup && plan.PcSnapshot == null);
            var normal = Image(Path.Combine("Snapshots", "history.zip"), 3, now.AddDays(-1));
            plan = (await SaveSync.BuildPlansAsync(client, Peer.Info, [Peer.Save], [foreign, recovery, normal], states, null, default)).Single();
            Assert(plan.PcSnapshot == normal && plan.Direction == SaveSyncDirection.Conflict);
            var selectedRoot = Path.Combine(directory, "Recovery");
            var rootedNormal = normal with { Path = Path.Combine(selectedRoot, "Snapshots", "history.zip") };
            var rootedRecovery = recovery with { Path = Path.Combine(selectedRoot, "Recovery", "nested", "protected.zip") };
            plan = (await SaveSync.BuildPlansAsync(client, Peer.Info, [Peer.Save], [rootedNormal, rootedRecovery], states, null, default, selectedRoot)).Single();
            Assert(plan.PcSnapshot == rootedNormal);
        });

        await Test("save sync never resolves first pairing or divergent content by save clock", async () =>
        {
            await using var peer = new Peer(); await using var client = await peer.ConnectAsync();
            var now = DateTimeOffset.UtcNow; var image = Image("clock.zip", 1, now);
            var baseline = SaveSync.RecordSync(Peer.Save, Peer.Info, Fingerprints.Of([2]), Fingerprints.Of([3]), now.AddDays(-1));
            foreach (var savedAt in new[] { now.AddDays(10).ToUnixTimeSeconds(), now.AddDays(-10).ToUnixTimeSeconds(), long.MaxValue })
            {
                peer.SaveInspectionOverride = new(Fingerprints.Of([4]), savedAt, SaveTimeSource.SaveCommit, "fixture");
                foreach (var states in new[] { new Dictionary<string, SaveSyncState>(), new Dictionary<string, SaveSyncState> { [baseline.Key] = baseline } })
                {
                    var plan = (await SaveSync.BuildPlansAsync(client, Peer.Info, [Peer.Save], [image], states, null, default)).Single();
                    Assert(plan.Direction == SaveSyncDirection.Conflict);
                }
            }
            var partial = baseline with { LastSyncedContentHash = null };
            peer.SaveInspectionOverride = new(partial.LastDeviceFingerprint!, null, SaveTimeSource.ObservedOnly, "fixture");
            var partialPlan = (await SaveSync.BuildPlansAsync(client, Peer.Info, [Peer.Save], [image],
                new Dictionary<string, SaveSyncState> { [partial.Key] = partial }, null, default)).Single();
            Assert(partialPlan.Direction == SaveSyncDirection.Conflict);
        });

        await Test("save sync uses deduplicated synced image until a later backup is created", async () =>
        {
            await using var peer = new Peer(); await using var client = await peer.ConnectAsync();
            var store = new SnapshotStore(Path.Combine(directory, "repeated-content"));
            var first = await store.BackupAsync(client, Peer.Info, Peer.Save, null, default);
            peer.Files["main.dat"] = [9];
            var second = await store.BackupAsync(client, Peer.Info, Peer.Save, null, default);
            peer.Files["main.dat"] = [1, 2, 3, 4, 5];
            var reused = await store.BackupAsync(client, Peer.Info, Peer.Save, null, default);
            Assert(reused.Path == first.Path && second.Manifest.CreatedAt > first.Manifest.CreatedAt);
            var inspection = await client.QueryAsync<SaveDeviceInspection>("saves.inspect");
            var baseline = SaveSync.RecordSync(Peer.Save, Peer.Info, inspection.Fingerprint, reused.Manifest.ContentHash, DateTimeOffset.UtcNow);
            var states = new Dictionary<string, SaveSyncState> { [baseline.Key] = baseline };
            var plan = (await SaveSync.BuildPlansAsync(client, Peer.Info, [Peer.Save], store.List(), states, null, default)).Single();
            Assert(plan.Direction == SaveSyncDirection.UpToDate && plan.PcSnapshot!.Path == first.Path);
            store.Prune(1, target: reused.Manifest);
            Assert(File.Exists(first.Path) && !File.Exists(second.Path) && store.List().Count == 1);
            plan = (await SaveSync.BuildPlansAsync(client, Peer.Info, [Peer.Save], store.List(), states, null, default)).Single();
            Assert(plan.Direction == SaveSyncDirection.UpToDate && plan.PcSnapshot!.Path == first.Path);
            // A newly created image still supersedes the baseline, even when its
            // content differs from the most recently synchronized ZIP.
            peer.Files["main.dat"] = [8];
            var later = await store.BackupAsync(client, Peer.Info, Peer.Save, null, default);
            peer.Files["main.dat"] = [1, 2, 3, 4, 5];
            plan = (await SaveSync.BuildPlansAsync(client, Peer.Info, [Peer.Save], store.List(), states, null, default)).Single();
            Assert(plan.Direction == SaveSyncDirection.PcNewer && plan.PcSnapshot!.Path == later.Path);
        });

        await Test("ordinary backup never deduplicates into a protected recovery copy", async () =>
        {
            await using var peer = new Peer(); await using var client = await peer.ConnectAsync();
            var store = new SnapshotStore(Path.Combine(directory, "recovery-dedup"));
            var recovery = await store.BackupAsync(client, Peer.Info, Peer.Save, null, default, recovery: true);
            var normal = await store.BackupAsync(client, Peer.Info, Peer.Save, null, default);
            Assert(normal.Path != recovery.Path && store.Scan().RecoveryPaths is { Length: 1 });
            var duplicate = await store.BackupAsync(client, Peer.Info, Peer.Save, null, default);
            Assert(duplicate.Path == normal.Path && store.List().Count == 2);
        });
    }
}
