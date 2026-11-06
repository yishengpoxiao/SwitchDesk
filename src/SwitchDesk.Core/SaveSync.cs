using System.Text.Json;

namespace SwitchDesk.Core;

public enum SaveSyncDirection { Unknown, UpToDate, NeedsFirstBackup, DeviceNewer, PcNewer, Conflict }

/// <summary>一台主机一个系统环境的双向同步状态；指纹为设备端存档树指纹，内容哈希为电脑快照清单哈希。</summary>
public sealed record SaveSyncState(string DeviceId, string Environment, string TitleId, string UserId, string SaveType,
    string? LastDeviceFingerprint, string? LastSyncedContentHash, DateTimeOffset LastSyncedAt)
{
    public string Key => $"{DeviceId}|{Environment}|{TitleId?.ToUpperInvariant()}|{UserId}|{SaveType}";
}

public sealed record SaveSyncPlan(SaveInfo Save, Snapshot? PcSnapshot, string DeviceFingerprint, SaveSyncDirection Direction);

/// <summary>双向同步状态保存在数据目录的 save-sync.json；损坏时视为无状态（退回冲突判定，不做危险假设）。</summary>
public sealed class SaveSyncStore(string directory)
{
    private string FilePath => Path.Combine(directory, "save-sync.json");

    public IReadOnlyList<SaveSyncState> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            using var document = JsonDocument.Parse(File.ReadAllText(FilePath), new JsonDocumentOptions { MaxDepth = 16 });
            var states = new List<SaveSyncState>();
            if (document.RootElement.ValueKind == JsonValueKind.Array)
                foreach (var item in document.RootElement.EnumerateArray())
                {
                    var state = item.Deserialize<SaveSyncState>(Wire.Json);
                    if (state is not null && !string.IsNullOrWhiteSpace(state.DeviceId) && !string.IsNullOrWhiteSpace(state.Environment) &&
                        ContentCatalog.ValidId(state.TitleId) && state.UserId != null && state.SaveType is "Account" or "Device" &&
                        (state.LastDeviceFingerprint == null || Fingerprints.Valid(state.LastDeviceFingerprint)) &&
                        (state.LastSyncedContentHash == null || Fingerprints.Valid(state.LastSyncedContentHash)))
                        states.Add(state with { TitleId = state.TitleId.ToUpperInvariant() });
                }
            // Conflicting duplicate baselines cannot establish sync direction.
            // Drop that identity instead of choosing a record by its timestamp.
            return states.GroupBy(s => s.Key, StringComparer.Ordinal).Where(g => g.Count() == 1).Select(g => g.Single()).ToArray();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }

    public void Save(IReadOnlyList<SaveSyncState> states)
    {
        Directory.CreateDirectory(directory);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(states, Wire.Json));
            File.Move(temporary, FilePath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

/// <summary>
/// Switch↔电脑 的通用存档同步：先读主机游戏与存档，再按设备指纹与电脑快照判断方向。
/// 不做任何格式转换，因此对主机上有存档的全部游戏通用；跨平台格式转换仍由 SaveAdapters 负责。
/// </summary>
public static class SaveSync
{
    public static string StateKey(SaveInfo save, DeviceInfo device) =>
        $"{device.DeviceId}|{device.Environment}|{save.TitleId.ToUpperInvariant()}|{save.UserId}|{save.Type}";

    public static SaveSyncState RecordSync(SaveInfo save, DeviceInfo device, string deviceFingerprint, string contentHash, DateTimeOffset now) =>
        new(device.DeviceId, device.Environment, save.TitleId.ToUpperInvariant(), save.UserId, save.Type, deviceFingerprint, contentHash, now);

    public static async Task<SaveSyncPlan[]> BuildPlansAsync(DeviceClient client, DeviceInfo device, SaveInfo[] saves,
        IReadOnlyList<Snapshot> snapshots, IReadOnlyDictionary<string, SaveSyncState> states,
        IProgress<TransferProgress>? progress, CancellationToken ct, string? backupRoot = null)
    {
        var plans = new List<SaveSyncPlan>();
        foreach (var save in saves)
        {
            ct.ThrowIfCancellationRequested();
            var state = states.GetValueOrDefault(StateKey(save, device));
            var candidates = snapshots.Where(s => SameTarget(s.Manifest, save, device) && !IsRecovery(s.Path, backupRoot))
                .OrderByDescending(s => s.Manifest.CreatedAt).ToArray();
            // Deduplication can reuse an older ZIP after the game returns to that
            // content. Prefer the recorded synced image unless a new backup was
            // created after the baseline; ZIP creation time is not play order.
            var pc = state == null ? candidates.FirstOrDefault() :
                candidates.FirstOrDefault(s => s.Manifest.CreatedAt > state.LastSyncedAt) ??
                candidates.FirstOrDefault(s => Fingerprints.Equal(s.Manifest.ContentHash, state.LastSyncedContentHash!)) ??
                candidates.FirstOrDefault();
            string fingerprint; var direction = SaveSyncDirection.Unknown;
            try
            {
                var inspection = await client.QueryAsync<SaveDeviceInspection>("saves.inspect",
                    new { key = save.Key, titleId = save.TitleId, userId = save.UserId, environment = device.Environment }, ct);
                fingerprint = inspection.Fingerprint;
                direction = Classify(state, pc, inspection);
            }
            catch (DeviceException ex) when (ex.Code == "SaveMissing") { fingerprint = ""; }
            plans.Add(new(save, pc, fingerprint, direction));
            progress?.Report(new(plans.Count, saves.Length, "同步检查 · " + save.GameName));
        }
        return [.. plans];
    }

    private static bool SameTarget(SaveManifest manifest, SaveInfo save, DeviceInfo device) =>
        manifest.DeviceId == device.DeviceId && manifest.TitleId.Equals(save.TitleId, StringComparison.OrdinalIgnoreCase) && manifest.UserId == save.UserId &&
        manifest.SaveType == save.Type && manifest.Environment == device.Environment;

    private static bool IsRecovery(string path, string? backupRoot)
    {
        if (backupRoot != null)
        {
            var recovery = Path.GetFullPath(Path.Combine(backupRoot, "Recovery")) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(recovery, StringComparison.OrdinalIgnoreCase);
        }
        // Compatibility for callers without a store root: err on preserving
        // recovery images instead of offering them as ordinary sync sources.
        for (var parent = Path.GetDirectoryName(Path.GetFullPath(path)); parent != null; parent = Path.GetDirectoryName(parent))
            if (Path.GetFileName(parent).Equals("Recovery", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static SaveSyncDirection Classify(SaveSyncState? state, Snapshot? pc, SaveDeviceInspection device)
    {
        if (pc == null) return SaveSyncDirection.NeedsFirstBackup;
        if (state == null || !Fingerprints.Valid(state.LastDeviceFingerprint) || !Fingerprints.Valid(state.LastSyncedContentHash))
            return SaveSyncDirection.Conflict;
        var deviceChanged = !Fingerprints.Equal(state.LastDeviceFingerprint!, device.Fingerprint);
        var pcChanged = !Fingerprints.Equal(state.LastSyncedContentHash!, pc.Manifest.ContentHash);
        if (!deviceChanged && !pcChanged) return SaveSyncDirection.UpToDate;
        if (deviceChanged && !pcChanged) return SaveSyncDirection.DeviceNewer;
        if (!deviceChanged && pcChanged) return SaveSyncDirection.PcNewer;
        // A clock cannot establish a common baseline or resolve divergent
        // contents. Both images must remain available for an explicit choice.
        return SaveSyncDirection.Conflict;
    }
}
