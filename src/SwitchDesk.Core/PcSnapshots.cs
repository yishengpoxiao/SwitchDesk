using System.Text.Json;

namespace SwitchDesk.Core;

public sealed record PcSnapshot(string Directory, PcSaveCandidate Source, SaveReading Reading, long Size, DateTimeOffset CreatedAt);
public sealed class PcSnapshotStore(string root)
{
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public async Task<PcSnapshot> BackupAsync(PcSaveCandidate source, StableSaveFile file, SaveReading reading, int keep, CancellationToken ct)
    {
        if (keep < 1 || !Fingerprints.Equal(file.Fingerprint, reading.Fingerprint) || !Path.GetFullPath(source.Path).Equals(file.Path, PathComparison)) throw new InvalidDataException("电脑存档快照来源与读取结果不一致");
        var scope = Fingerprints.Of(JsonSerializer.SerializeToUtf8Bytes(new { source.AdapterId, source.Owner, path = OperatingSystem.IsWindows() ? file.Path.ToUpperInvariant() : file.Path, source.Slot }, Wire.Json));
        var managed = Path.Combine(root, "Snapshots", scope); Directory.CreateDirectory(managed); LocalSafety.DemandDirectory(managed);
        var history = await ListVerifiedAsync(managed, source, ct);
        var duplicate = history.FirstOrDefault(s => Fingerprints.Equal(s.Reading.Fingerprint, file.Fingerprint));
        var snapshot = duplicate;
        if (snapshot == null)
        {
            var directory = Path.Combine(managed, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            snapshot = new PcSnapshot(directory, source, reading, file.Bytes.LongLength, DateTimeOffset.UtcNow);
            await DurableFile.WriteNewAsync(Path.Combine(directory, "save.bin"), file.Bytes, ct);
            await DurableFile.WriteNewAsync(Path.Combine(directory, "snapshot.json"), JsonSerializer.SerializeToUtf8Bytes(snapshot, Wire.Json), ct);
            await VerifyAsync(snapshot, ct); history.Add(snapshot);
        }
        // Reusing a prior image still applies the current retention setting.
        // Keep the image just observed even when its original creation time is old.
        foreach (var old in history.OrderByDescending(x => x.Directory == snapshot.Directory).ThenByDescending(x => x.CreatedAt).Skip(keep))
        {
            ct.ThrowIfCancellationRequested(); var full = Path.GetFullPath(old.Directory); var parent = Path.GetFullPath(managed);
            if (!string.Equals(Path.GetDirectoryName(full), parent, PathComparison) || !Guid.TryParseExact(Path.GetFileName(full), "N", out _)) throw new InvalidDataException("历史保留目录越界");
            LocalSafety.DemandDirectory(full); LocalSafety.DemandRegularFile(Path.Combine(full, "save.bin")); LocalSafety.DemandRegularFile(Path.Combine(full, "snapshot.json"));
            File.Delete(Path.Combine(full, "save.bin")); File.Delete(Path.Combine(full, "snapshot.json"));
            // Never recursively delete user-added files or follow a link as part of retention.
            if (!Directory.EnumerateFileSystemEntries(full).Any()) Directory.Delete(full);
        }
        return snapshot;
    }
    public static async Task VerifyAsync(PcSnapshot snapshot, CancellationToken ct)
    {
        ValidateMetadata(snapshot);
        var data = await StableSaveFile.ReadAsync(Path.Combine(snapshot.Directory, "save.bin"), ct);
        if (data.Bytes.LongLength != snapshot.Size || !Fingerprints.Equal(data.Fingerprint, snapshot.Reading.Fingerprint)) throw new InvalidDataException("电脑历史存档校验失败");
    }
    private static void ValidateMetadata(PcSnapshot? snapshot)
    {
        if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.Directory) || !Path.IsPathFullyQualified(snapshot.Directory) ||
            snapshot.Source == null || string.IsNullOrWhiteSpace(snapshot.Source.Path) || !Path.IsPathFullyQualified(snapshot.Source.Path) ||
            string.IsNullOrWhiteSpace(snapshot.Source.AdapterId) || string.IsNullOrWhiteSpace(snapshot.Source.Owner) || string.IsNullOrWhiteSpace(snapshot.Source.Slot) ||
            snapshot.Reading == null || !Fingerprints.Valid(snapshot.Reading.Fingerprint) || snapshot.Size is <= 0 or > StableSaveFile.MaximumBytes)
            throw new InvalidDataException("电脑历史存档清单缺少有效身份或内容信息");
        try { _ = Path.GetFullPath(snapshot.Directory); _ = Path.GetFullPath(snapshot.Source.Path); }
        catch (ArgumentException ex) { throw new InvalidDataException("电脑历史存档路径无效", ex); }
    }

    private static async Task<List<PcSnapshot>> ListVerifiedAsync(string directory, PcSaveCandidate source, CancellationToken ct)
    {
        var found = new List<PcSnapshot>();
        foreach (var item in Directory.EnumerateDirectories(directory))
        {
            ct.ThrowIfCancellationRequested(); if (!Guid.TryParseExact(Path.GetFileName(item), "N", out _)) continue;
            var path = Path.Combine(item, "snapshot.json");
            try
            {
                LocalSafety.DemandRegularFile(path); if (new FileInfo(path).Length > 65536) continue;
                var snapshot = JsonSerializer.Deserialize<PcSnapshot>(await File.ReadAllTextAsync(path, ct), Wire.Json)
                    ?? throw new InvalidDataException("电脑历史存档清单为空");
                ValidateMetadata(snapshot);
                if (!Path.GetFullPath(snapshot.Directory).Equals(Path.GetFullPath(item), PathComparison) ||
                    snapshot.Source.AdapterId != source.AdapterId || snapshot.Source.Owner != source.Owner || snapshot.Source.Slot != source.Slot ||
                    !Path.GetFullPath(snapshot.Source.Path).Equals(Path.GetFullPath(source.Path), PathComparison)) continue;
                await VerifyAsync(snapshot, ct); found.Add(snapshot);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException) { }
        }
        return found;
    }
}
