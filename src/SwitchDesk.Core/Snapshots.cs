using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace SwitchDesk.Core;

public sealed record SnapshotScanIssue(string Path, string Reason);
public sealed record SnapshotScanResult(Snapshot[] Snapshots, SnapshotScanIssue[] Issues, string[]? RecoveryPaths = null);
public sealed record SnapshotRestoreResult(string RecoveryPath, string? Warning = null);

public sealed class SnapshotStore(string root)
{
    private static StringComparison LocalPathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static string Digest(SnapshotFile[] files, string[]? directories) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new { files = files.OrderBy(f => f.Path, StringComparer.Ordinal).ToArray(), directories = (directories ?? []).Order(StringComparer.Ordinal).ToArray() }, Wire.Json)));

    public IReadOnlyList<Snapshot> List() => Scan().Snapshots;

    // Directory discovery reads manifests only. Payload checks remain mandatory for
    // verification, dragging and restoration; listing is not a successful hash check.
    public SnapshotScanResult Scan(CancellationToken ct = default)
    {
        if (!Directory.Exists(root)) return new([], []);
        var result = new List<Snapshot>();
        var recoveryPaths = new List<string>();
        var issues = new List<SnapshotScanIssue>();
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            ct.ThrowIfCancellationRequested();
            string[] entries;
            try { entries = Directory.GetFileSystemEntries(directory); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { issues.Add(new(directory, "无法读取此备份目录")); continue; }
            foreach (var path in entries)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var attributes = File.GetAttributes(path);
                    if (attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                    if (attributes.HasFlag(FileAttributes.Directory)) { pending.Push(path); continue; }
                    if (!Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                    using var zip = ZipFile.OpenRead(path); var entry = zip.GetEntry("manifest.json");
                    if (entry is null) { issues.Add(new(path, "缺少快照清单，无法识别")); continue; }
                    if (entry.Length > 16 * 1024 * 1024) { issues.Add(new(path, "快照清单过大")); continue; }
                    using var stream = entry.Open(); var manifest = JsonSerializer.Deserialize<SaveManifest>(stream, Wire.Json);
                    if (manifest == null) { issues.Add(new(path, "快照清单为空")); continue; }
                    if (manifest.FormatVersion != 1) { issues.Add(new(path, "此快照格式版本尚不支持")); continue; }
                    ValidateManifest(manifest); result.Add(new(path, manifest));
                    if (Path.GetRelativePath(root, path).StartsWith("Recovery" + Path.DirectorySeparatorChar, LocalPathComparison)) recoveryPaths.Add(path);
                }
                catch (Exception e) when (e is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
                { issues.Add(new(path, "快照清单损坏或文件不可读取")); }
            }
        }
        return new(result.OrderByDescending(s => s.Manifest.CreatedAt).ToArray(), issues.ToArray(), recoveryPaths.ToArray());
    }
    public async Task<Snapshot> BackupAsync(DeviceClient client, DeviceInfo device, SaveInfo save,
        IProgress<TransferProgress>? progress, CancellationToken ct, bool recovery = false)
    {
        if (save.Environment != device.Environment) throw new DeviceException("SaveMismatch", "存档来源系统环境已变化，请重新读取列表");
        var directory = Path.Combine(root, recovery ? "Recovery" : "Snapshots", PathSafety.SafeName(device.DeviceId),
            PathSafety.SafeName(device.Environment), PathSafety.SafeName(save.TitleId), PathSafety.SafeName(save.UserId + "_" + save.Type));
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + ".zip");
        var partial = destination + ".partial";
        var open = await client.QueryAsync<JsonElement>("saves.read.open", new { key = save.Key, titleId = save.TitleId, userId = save.UserId, type = save.Type }, ct);
        var handle = open.GetProperty("handle").GetString()!;
        try
        {
            var entries = await client.QueryAsync<RemoteEntry[]>("handle.tree", new { handle }, ct);
            var files = new List<SnapshotFile>();
            await using (var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, Wire.ChunkSize, true))
            {
                using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true);
                foreach (var entry in entries.Where(e => !e.IsDirectory).OrderBy(e => e.Path, StringComparer.Ordinal))
                {
                    var relative = PathSafety.Relative(entry.Path); using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    await using var output = zip.CreateEntry("data/" + relative, CompressionLevel.Fastest).Open();
                    await Transfers.ReadToAsync(client, handle, relative, entry.Size, output, hash, progress, ct);
                    files.Add(new(relative, entry.Size, Convert.ToHexString(hash.GetHashAndReset())));
                }
                var list = files.ToArray(); var dirs = entries.Where(e => e.IsDirectory).Select(e => PathSafety.Relative(e.Path)).Order(StringComparer.Ordinal).ToArray();
                var manifest = new SaveManifest(1, device.DeviceId, device.Environment, save.TitleId,
                    save.GameName, save.UserId, save.UserName, save.Type, DateTimeOffset.UtcNow, list, Digest(list, dirs), dirs);
                await using var meta = zip.CreateEntry("manifest.json").Open(); await JsonSerializer.SerializeAsync(meta, manifest, Wire.Json, ct);
            }
            var snapshot = await VerifyAsync(partial, ct);
            Snapshot? duplicate = null;
            if (!recovery)
            {
                var managed = Path.GetFullPath(Path.Combine(root, "Snapshots")) + Path.DirectorySeparatorChar;
                foreach (var candidate in List().Where(s => Path.GetFullPath(s.Path).StartsWith(managed, LocalPathComparison) &&
                             SameTarget(s.Manifest, snapshot.Manifest) && s.Manifest.ContentHash == snapshot.Manifest.ContentHash))
                    try { duplicate = await VerifyAsync(candidate.Path, ct); break; }
                    catch (Exception ex) when (ex is InvalidDataException or IOException or JsonException) { }
            }
            if (duplicate != null) { File.Delete(partial); return duplicate; }
            File.Move(partial, destination); return new(destination, snapshot.Manifest);
        }
        finally { await Transfers.CloseAsync(client, handle); if (File.Exists(partial)) File.Delete(partial); }
    }
    public static bool SameTarget(SaveManifest a, SaveManifest b) => a.DeviceId == b.DeviceId && a.Environment == b.Environment &&
        a.TitleId == b.TitleId && a.UserId == b.UserId && a.SaveType == b.SaveType;

    /// <summary>Builds a new snapshot from the target's complete verified image. Never changes target identity.</summary>
    public static async Task<Snapshot> CreateConvertedTargetAsync(string targetSnapshot, string slotPath, ReadOnlyMemory<byte> payload, string destination, CancellationToken ct, SaveManifest? expectedManifest = null)
    {
        using var source = ZipFile.OpenRead(targetSnapshot);
        var target = await VerifyArchiveAsync(source, targetSnapshot, ct, expectedManifest); PathSafety.Relative(slotPath);
        if (!target.Manifest.Files.Any(f => f.Path == slotPath) || payload.Length is 0 or > StableSaveFile.MaximumBytes) throw new InvalidDataException("只能替换已初始化的目标槽位文件");
        var files = target.Manifest.Files.Select(f => f.Path == slotPath ? new SnapshotFile(f.Path, payload.Length, Fingerprints.Of(payload.Span)) : f).ToArray();
        var manifest = target.Manifest with { Files = files, CreatedAt = DateTimeOffset.UtcNow, ContentHash = Digest(files, target.Manifest.Directories) };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!); var partial = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, Wire.ChunkSize, true))
            {
                using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
                {
                    foreach (var item in files)
                    {
                        await using var entry = zip.CreateEntry("data/" + item.Path, CompressionLevel.Fastest).Open();
                        if (item.Path == slotPath) await entry.WriteAsync(payload, ct);
                        else { await using var input = source.GetEntry("data/" + item.Path)!.Open(); await input.CopyToAsync(entry, ct); }
                    }
                    await using var metadata = zip.CreateEntry("manifest.json").Open(); await JsonSerializer.SerializeAsync(metadata, manifest, Wire.Json, ct);
                }
                await output.FlushAsync(ct); output.Flush(true);
            }
            var verified = await VerifyAsync(partial, ct); File.Move(partial, destination); return new(destination, verified.Manifest);
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }

    private static void ValidateManifest(SaveManifest manifest)
    {
        if (manifest.FormatVersion != 1 || string.IsNullOrWhiteSpace(manifest.DeviceId) || string.IsNullOrWhiteSpace(manifest.Environment) ||
            manifest.TitleId is not { Length: 16 } || !manifest.TitleId.All(Uri.IsHexDigit) ||
            manifest.UserId == null || manifest.SaveType is not ("Account" or "Device") ||
            manifest.GameName == null || manifest.UserName == null || manifest.ContentHash is not { Length: 64 } ||
            manifest.Files == null || manifest.Files.Length > 100000 ||
            manifest.Files.Any(f => f == null || f.Path == null || f.Size < 0 || f.Sha256 is not { Length: 64 } || !f.Sha256.All(Uri.IsHexDigit)) ||
            manifest.Directories is { Length: > 100000 } || manifest.Directories?.Any(d => d == null) == true)
            throw new InvalidDataException("快照清单损坏或身份信息不完整");
    }

    public static async Task<Snapshot> VerifyAsync(string path, CancellationToken ct = default, SaveManifest? expectedManifest = null)
    {
        using var zip = ZipFile.OpenRead(path);
        return await VerifyArchiveAsync(zip, path, ct, expectedManifest);
    }

    /// <summary>Verify a caller-owned seekable stream without closing it; the caller retains the verified source handle.</summary>
    public static async Task<Snapshot> VerifyAsync(Stream source, string path, CancellationToken ct = default, SaveManifest? expectedManifest = null)
    {
        using var zip = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
        return await VerifyArchiveAsync(zip, path, ct, expectedManifest);
    }

    private static async Task<Snapshot> VerifyArchiveAsync(ZipArchive zip, string path, CancellationToken ct, SaveManifest? expectedManifest = null)
    {
        var entry = zip.GetEntry("manifest.json") ?? throw new InvalidDataException("不是本工具生成的存档快照");
        if (entry.Length > 16 * 1024 * 1024) throw new InvalidDataException("快照清单过大");
        SaveManifest manifest;
        await using (var stream = entry.Open()) manifest = await JsonSerializer.DeserializeAsync<SaveManifest>(stream, Wire.Json, ct) ?? throw new InvalidDataException("快照清单为空");
        ValidateManifest(manifest);
        if (expectedManifest != null && (!SameTarget(manifest, expectedManifest) ||
            !Fingerprints.Equal(manifest.ContentHash, expectedManifest.ContentHash)))
            throw new InvalidDataException("所选存档快照已变化，请刷新并重新确认版本");
        foreach (var dir in manifest.Directories ?? []) PathSafety.Relative(dir);
        var filePaths = manifest.Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var directoryPaths = (manifest.Directories ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (directoryPaths.Count != (manifest.Directories?.Length ?? 0) || filePaths.Overlaps(directoryPaths)) throw new InvalidDataException("快照文件与目录冲突");
        foreach (var pathValue in filePaths.Concat(directoryPaths))
        {
            var slash = pathValue.LastIndexOf('/');
            while (slash > 0) { if (filePaths.Contains(pathValue[..slash])) throw new InvalidDataException("快照把文件用作父目录"); slash = pathValue.LastIndexOf('/', slash - 1); }
        }
        if (manifest.Files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Files.Length ||
            zip.Entries.Select(e => e.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != zip.Entries.Count) throw new InvalidDataException("快照包含重复路径");
        if (zip.Entries.Count != manifest.Files.Length + 1) throw new InvalidDataException("快照包含未登记的文件");
        if (Digest(manifest.Files, manifest.Directories) != manifest.ContentHash) throw new InvalidDataException("快照清单校验失败");
        foreach (var item in manifest.Files)
        {
            PathSafety.Relative(item.Path); var data = zip.GetEntry("data/" + item.Path) ?? throw new InvalidDataException("快照缺少文件：" + item.Path);
            if (data.Length != item.Size) throw new InvalidDataException("快照文件长度不匹配");
            await using var source = data.Open(); var hash = Convert.ToHexString(await SHA256.HashDataAsync(source, ct));
            if (!hash.Equals(item.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("快照文件校验失败：" + item.Path);
        }
        return new(path, manifest);
    }
    public async Task<SnapshotRestoreResult> RestoreAsync(DeviceClient client, DeviceInfo device, SaveInfo target, string path,
        IProgress<TransferProgress>? progress, CancellationToken ct, string? expectedFingerprint = null, string? operationId = null, bool foreignSource = false, SaveManifest? expectedManifest = null)
    {
        // Verify the complete archive before reading or changing any live save.
        using var zip = ZipFile.OpenRead(path);
        var snapshot = await VerifyArchiveAsync(zip, path, ct, expectedManifest); var m = snapshot.Manifest;
        if (m.TitleId != target.TitleId || m.UserId != target.UserId || m.SaveType != target.Type)
            throw new DeviceException("SaveMismatch", "快照与目标游戏或用户不匹配");
        if (!foreignSource && (m.DeviceId != device.DeviceId || m.Environment != device.Environment))
            throw new DeviceException("SaveMismatch", "快照来自其他主机或系统环境；跨系统迁移请使用「迁移到当前系统」入口");
        var atomic = device.Capabilities.Contains("restore.atomic.v1");
        if ((expectedFingerprint != null || operationId != null || foreignSource) && !atomic)
            throw new DeviceException("CapabilityMissing", "跨系统迁移与恢复保护需要新版配套程序（restore.atomic.v1）");
        SaveDeviceInspection? before = null;
        if (atomic) before = await client.QueryAsync<SaveDeviceInspection>("saves.inspect", new { key = target.Key, titleId = target.TitleId, userId = target.UserId, environment = target.Environment }, ct);
        expectedFingerprint ??= foreignSource ? before?.Fingerprint : null;
        if (foreignSource && expectedFingerprint == null) throw new IOException("无法读取目标存档指纹，已取消迁移");
        if (expectedFingerprint != null && !Fingerprints.Equal(expectedFingerprint, before!.Fingerprint)) throw new IOException("目标存档已变化，请重新比较");
        var rescue = await BackupAsync(client, device, target, progress, ct, recovery: true);
        await VerifyAsync(rescue.Path, ct);
        operationId ??= Guid.NewGuid().ToString("N");
        var open = await client.QueryAsync<JsonElement>("saves.restore.begin", new { key = target.Key, manifest = m, expectedFingerprint = before?.Fingerprint, operationId, foreignSource }, ct);
        var handle = open.GetProperty("handle").GetString()!;
        var log = rescue.Path + ".restore.json";
        try
        {
            await File.WriteAllTextAsync(log, JsonSerializer.Serialize(new { operationId, atomic, snapshot = path, recovery = rescue.Path, target, state = "staging", time = DateTimeOffset.UtcNow }, Wire.Json), ct);
            foreach (var item in m.Files)
            {
                await client.CallAsync("saves.restore.file", new { handle, path = item.Path, size = item.Size, sha256 = item.Sha256 }, ct);
                await using var data = zip.GetEntry("data/" + item.Path)!.Open();
                await Transfers.WriteFromAsync(client, handle, item.Path, data, progress, ct);
            }
            ct.ThrowIfCancellationRequested(); progress?.Report(new(0, 0, "正在写回存档，请保持连接"));
            ct.ThrowIfCancellationRequested();
            // Commit is deliberately non-cancellable once live savedata is being replaced.
            await client.CallAsync("saves.restore.commit", new { handle }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            ex.Data["SwitchDesk.RecoveryPath"] = rescue.Path;
            try { await File.WriteAllTextAsync(log, JsonSerializer.Serialize(new { operationId, atomic, snapshot = path, recovery = rescue.Path, state = "failed", error = ex.Message, time = DateTimeOffset.UtcNow }, Wire.Json), CancellationToken.None); }
            catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { }
            throw;
        }
        finally { await Transfers.CloseAsync(client, handle); }
        // The device has confirmed the commit. A local logging failure must not
        // rewrite that outcome as a failed restore or encourage another write.
        try
        {
            await File.WriteAllTextAsync(log, JsonSerializer.Serialize(new { operationId, atomic, snapshot = path, recovery = rescue.Path, state = "complete", time = DateTimeOffset.UtcNow }, Wire.Json), CancellationToken.None);
            return new(rescue.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(rescue.Path, "主机存档已写回，但电脑完成记录保存失败；请保留 Recovery 备份，无需重复写回。" + ex.Message);
        }
    }
    public void Prune(int keep, CancellationToken ct = default, SaveManifest? target = null)
    {
        if (keep < 1) throw new ArgumentOutOfRangeException(nameof(keep));
        var valid = new List<Snapshot>();
        foreach (var candidate in List().Where(s => target == null || SameTarget(s.Manifest, target)))
            try { ct.ThrowIfCancellationRequested(); valid.Add(VerifyAsync(candidate.Path, ct).GetAwaiter().GetResult()); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or JsonException) { }
        var managed = Path.GetFullPath(Path.Combine(root, "Snapshots")) + Path.DirectorySeparatorChar;
        foreach (var group in valid.Where(s => Path.GetFullPath(s.Path).StartsWith(managed, LocalPathComparison))
                     .GroupBy(s => (s.Manifest.DeviceId, s.Manifest.Environment, s.Manifest.TitleId, s.Manifest.UserId, s.Manifest.SaveType)))
            // A freshly observed version may reuse an old ZIP. Keep that image
            // even when another distinct version has a later creation time.
            foreach (var old in group.OrderByDescending(s => target != null && Fingerprints.Equal(s.Manifest.ContentHash, target.ContentHash))
                         .ThenByDescending(s => s.Manifest.CreatedAt).Skip(keep))
                try { ct.ThrowIfCancellationRequested(); File.Delete(old.Path); } catch (IOException) { /* A drag consumer may still hold this snapshot open. Retry on the next backup. */ }
    }
}

public sealed record SnapshotDiff(Snapshot Old, Snapshot Current, SnapshotFile[] Added, SnapshotFile[] Removed, SnapshotFile[] Changed, int Unchanged)
{
    public string Summary => $"新增 {Added.Length} · 删除 {Removed.Length} · 变更 {Changed.Length} · 未变 {Unchanged}";
    public IEnumerable<string> Details()
    {
        foreach (var file in Changed) yield return "变更  " + file.Path + $"  ·  {Display.Size(file.Size)}";
        foreach (var file in Added) yield return "新增  " + file.Path + $"  ·  {Display.Size(file.Size)}";
        foreach (var file in Removed) yield return "删除  " + file.Path + $"  ·  {Display.Size(file.Size)}";
    }
}

public static class SnapshotTools
{
    /// <summary>对比同一存档的两份快照；先完整校验，再按相对路径比对大小与 SHA-256。</summary>
    public static async Task<SnapshotDiff> CompareAsync(string oldPath, string currentPath, CancellationToken ct = default)
    {
        var oldSnapshot = await SnapshotStore.VerifyAsync(oldPath, ct);
        var current = await SnapshotStore.VerifyAsync(currentPath, ct);
        if (!SnapshotStore.SameTarget(oldSnapshot.Manifest, current.Manifest))
            throw new InvalidDataException("两份快照不属于同一主机、系统环境、游戏与用户；拒绝对比");
        var before = oldSnapshot.Manifest.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var after = current.Manifest.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var added = after.Values.Where(f => !before.ContainsKey(f.Path)).ToArray();
        var removed = before.Values.Where(f => !after.ContainsKey(f.Path)).ToArray();
        var changed = after.Values.Where(f => before.TryGetValue(f.Path, out var old) &&
            (old.Size != f.Size || !old.Sha256.Equals(f.Sha256, StringComparison.OrdinalIgnoreCase))).ToArray();
        return new(oldSnapshot, current, added, removed, changed, current.Manifest.Files.Length - added.Length - changed.Length);
    }

    /// <summary>把校验通过的快照导出（复制）到所选目录，命名含游戏、用户与时间；同秒重复导出自动加序号，绝不覆盖。</summary>
    public static async Task<string> ExportSnapshotAsync(string path, string directory, CancellationToken ct = default, SaveManifest? expectedManifest = null)
    {
        // Keep the verified source open through the copy. On Windows this also
        // prevents another process replacing the selected snapshot midway.
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        var snapshot = await SnapshotStore.VerifyAsync(input, path, ct, expectedManifest);
        var manifest = snapshot.Manifest;
        Directory.CreateDirectory(directory);
        // Leave room for the timestamp and collision suffix under both the
        // Windows UTF-16 and Unix UTF-8 filename component limits.
        var baseName = $"{PathSafety.SafeName(manifest.GameName, 100, 100)}_{PathSafety.SafeName(manifest.UserName, 100, 100)}_{manifest.CreatedAt.ToLocalTime():yyyyMMdd-HHmmss}";
        var destination = Path.Combine(directory, baseName + ".zip");
        for (var attempt = 2; File.Exists(destination) && attempt < 1000; attempt++)
            destination = Path.Combine(directory, $"{baseName}-{attempt}.zip");
        if (File.Exists(destination)) throw new IOException("导出目录中同名文件过多，请清理后重试");
        var partial = Path.Combine(directory, ".switchdesk-" + Guid.NewGuid().ToString("N") + ".partial");
        var created = false;
        try
        {
            input.Position = 0;
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, true))
            {
                created = true; await input.CopyToAsync(output, ct); await output.FlushAsync(ct);
                // Unix does not enforce the source sharing lock against all writers.
                // Verify the actual copied bytes before publishing the export.
                output.Position = 0;
                await SnapshotStore.VerifyAsync(output, partial, ct, manifest);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(partial, destination);
            return destination;
        }
        finally { if (created && File.Exists(partial)) File.Delete(partial); }
    }
}
