using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace SwitchDesk.Core;

public sealed class LibraryDatabase : IDisposable
{
    private readonly nint db;
    private readonly object sync = new();
    private bool disposed;
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    public LibraryDatabase(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        try
        {
            Check(Native.sqlite3_open_v2(path, out db, 6, 0));
            Exec("PRAGMA journal_mode=WAL;");
            Exec("CREATE TABLE IF NOT EXISTS library(path TEXT PRIMARY KEY, name TEXT NOT NULL, title_id TEXT, kind TEXT NOT NULL, version TEXT NOT NULL, size INTEGER NOT NULL, sha256 TEXT, status TEXT NOT NULL);");
            Exec("CREATE TABLE IF NOT EXISTS fingerprints(path TEXT PRIMARY KEY, modified INTEGER NOT NULL, sidecar_modified INTEGER NOT NULL, size INTEGER NOT NULL);");
        }
        catch { if (db != 0) Native.sqlite3_close_v2(db); throw; }
    }
    private void Check(int code) { if (code != 0) throw new IOException("SQLite: " + Marshal.PtrToStringUTF8(Native.sqlite3_errmsg(db))); }
    private void Exec(string sql)
    {
        var code = Native.sqlite3_exec(db, sql, 0, 0, out var error);
        try { Check(code); } finally { if (error != 0) Native.sqlite3_free(error); }
    }
    public void Upsert(LibraryItem item)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Check(Native.sqlite3_prepare_v2(db, "INSERT INTO library VALUES(?1,?2,?3,?4,?5,?6,?7,?8) ON CONFLICT(path) DO UPDATE SET name=excluded.name,title_id=excluded.title_id,kind=excluded.kind,version=excluded.version,size=excluded.size,sha256=excluded.sha256,status=excluded.status", -1, out var stmt, 0));
            try
            {
                string?[] values = [item.Path, item.Name, item.TitleId, item.Kind, item.Version, null, item.Sha256, item.Status];
                for (var i = 0; i < values.Length; i++) { if (i == 5) Check(Native.sqlite3_bind_int64(stmt, i + 1, item.Size)); else Check(Native.sqlite3_bind_text(stmt, i + 1, values[i], -1, new nint(-1))); }
                if (Native.sqlite3_step(stmt) != 101) Check(1);
            }
            finally { Native.sqlite3_finalize(stmt); }
        }
    }
    public IReadOnlyList<LibraryItem> List()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Check(Native.sqlite3_prepare_v2(db, "SELECT path,name,title_id,kind,version,size,sha256,status FROM library ORDER BY name", -1, out var stmt, 0));
            var list = new List<LibraryItem>();
            try
            {
                int code;
                while ((code = Native.sqlite3_step(stmt)) == 100)
                {
                    string? Text(int i) => Marshal.PtrToStringUTF8(Native.sqlite3_column_text(stmt, i));
                    list.Add(new(Text(0)!, Text(1)!, Text(2), Text(3)!, Text(4)!, Native.sqlite3_column_int64(stmt, 5), Text(6), Text(7)!));
                }
                if (code != 101) Check(code);
            }
            finally { Native.sqlite3_finalize(stmt); }
            return list;
        }
    }
    public async Task ScanAsync(string directory, CancellationToken ct)
    {
        Directory.CreateDirectory(directory); var fullDirectory = Path.GetFullPath(directory);
        // Older Windows scans could index the same path under different letter
        // casing. Tolerate those aliases and reuse the stored path for SQLite's
        // case-sensitive primary key and fingerprint lookup. Other platforms
        // may have distinct files differing only by case.
        var known = List().GroupBy(i => i.Path, PathComparer).ToDictionary(g => g.Key, g => g.First(), PathComparer);
        foreach (var path in Directory.EnumerateFiles(directory, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            ct.ThrowIfCancellationRequested();
            if (!new[] { ".nsp", ".xci", ".nsz", ".xcz" }.Contains(Path.GetExtension(path).ToLowerInvariant())) continue;
            var info = new FileInfo(path);
            if (known.TryGetValue(info.FullName, out var previous)) info = new FileInfo(previous.Path);
            var fullPath = info.FullName;
            if (previous != null && MatchesFingerprint(info)) continue;
            LibraryItem item = new(fullPath, info.Name, null, Path.GetExtension(path)[1..].ToUpperInvariant(), "待识别", info.Length, null, "待识别");
            var sidecar = fullPath + ".switchdesk.json";
            var rememberFingerprint = true;
            if (File.Exists(sidecar))
            {
                try
                {
                    await using var metadata = new FileStream(sidecar, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                    if (metadata.Length > 16 * 1024 * 1024) throw new InvalidDataException("导出清单超过 16 MiB 解析预算");
                    var manifest = await JsonSerializer.DeserializeAsync<GameManifest>(metadata, Wire.Json, ct);
                    if (manifest?.FormatVersion == 1 && manifest.Size == info.Length && manifest.Component != null &&
                        !string.IsNullOrWhiteSpace(manifest.Name) && ContentCatalog.ValidId(manifest.TitleId) &&
                        ContentCatalog.ValidId(manifest.Component.TitleId) && !string.IsNullOrWhiteSpace(manifest.Component.Kind) && Fingerprints.Valid(manifest.Sha256))
                        item = item with { Name = manifest.Name, TitleId = manifest.TitleId, Kind = manifest.Component.Kind, Version = manifest.Component.Version.ToString(), Sha256 = manifest.Sha256, Status = "有导出清单，待校验" };
                    else item = item with { Status = "清单无效" };
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException) { item = item with { Status = "清单无效" }; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Sharing/permission failures may disappear without any timestamp change.
                    // Do not let the unchanged-file shortcut cache that failure permanently.
                    item = item with { Status = "清单暂不可读取" }; rememberFingerprint = false;
                }
            }
            Upsert(item);
            if (rememberFingerprint) RecordFingerprint(info);
        }
        await MarkMissingAsync(fullDirectory, ct);
    }
    /// <summary>ROADMAP 第三阶段：只把本次扫描目录内、磁盘上已不存在的索引标记为「文件缺失」，其他库目录不受影响。</summary>
    public async Task MarkMissingAsync(string directory, CancellationToken ct)
    {
        var prefix = Path.EndsInDirectorySeparator(directory) ? directory : directory + Path.DirectorySeparatorChar;
        foreach (var item in List())
        {
            ct.ThrowIfCancellationRequested();
            if (!Path.GetFullPath(item.Path).StartsWith(prefix, PathComparison)) continue;
            if (File.Exists(item.Path))
            {
                // 文件重新出现：清除「文件缺失」标记，恢复到待识别状态
                if (item.Status == "文件缺失") Upsert(item with { Status = "待识别" });
                continue;
            }
            if (item.Status == "文件缺失") continue;
            Upsert(item with { Status = "文件缺失" });
        }
        await Task.CompletedTask;
    }
    /// <summary>显式移除单个索引（文件可仍在磁盘上）；不删除任何文件。</summary>
    public void Remove(string path)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Check(Native.sqlite3_prepare_v2(db, "DELETE FROM library WHERE path=?1", -1, out var statement, 0));
            try
            {
                Check(Native.sqlite3_bind_text(statement, 1, Path.GetFullPath(path), -1, new nint(-1)));
                if (Native.sqlite3_step(statement) != 101) Check(1);
                Check(Native.sqlite3_prepare_v2(db, "DELETE FROM fingerprints WHERE path=?1", -1, out var marks, 0));
                try
                {
                    Check(Native.sqlite3_bind_text(marks, 1, Path.GetFullPath(path), -1, new nint(-1)));
                    if (Native.sqlite3_step(marks) != 101) Check(1);
                }
                finally { Native.sqlite3_finalize(marks); }
            }
            finally { Native.sqlite3_finalize(statement); }
        }
    }
    /// <summary>清理全部「文件缺失」索引；不删除任何文件。</summary>
    public int RemoveMissing(CancellationToken ct = default)
    {
        var removed = 0;
        foreach (var item in List().Where(x => x.Status == "文件缺失"))
        { ct.ThrowIfCancellationRequested(); Remove(item.Path); removed++; }
        return removed;
    }
    public sealed record LibraryVerifyReport(int Verified, int Mismatched, int Skipped);
    /// <summary>批量校验：对所有带预期摘要的条目重算 SHA-256。</summary>
    public async Task<LibraryVerifyReport> VerifyAllAsync(IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var targets = List().Where(x => x.Status != "文件缺失" && x.Sha256?.Length == 64).ToArray();
        int verified = 0, mismatched = 0, skipped = 0;
        for (var index = 0; index < targets.Length; index++)
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(targets[index].Path)) { skipped++; continue; }
            try { if ((await VerifyAsync(targets[index], ct)).Status == "已校验") verified++; else mismatched++; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
            progress?.Report(new(index + 1, targets.Length, "批量校验 · " + targets[index].Name));
        }
        return new(verified, mismatched, skipped);
    }
    public sealed record DuplicateGroup(string Reason, LibraryItem[] Items);
    /// <summary>重复检测：同 TitleID 与类型的元数据重复，以及同大小再比对内容的完全一致副本。</summary>
    public async Task<IReadOnlyList<DuplicateGroup>> FindDuplicatesAsync(CancellationToken ct)
    {
        var items = List().Where(x => x.Status != "文件缺失").ToArray();
        var groups = new List<DuplicateGroup>();
        foreach (var titled in items.Where(x => !string.IsNullOrWhiteSpace(x.TitleId))
                     .GroupBy(x => x.TitleId + "|" + x.Kind, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).OrderBy(g => g.Key, StringComparer.Ordinal))
            groups.Add(new("同 TitleID 与类型", [.. titled.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)]));
        foreach (var bucket in items.GroupBy(x => x.Size).Where(g => g.Count() > 1 && g.Key > 0).OrderBy(g => g.Key))
        {
            ct.ThrowIfCancellationRequested();
            var hashes = new Dictionary<string, List<LibraryItem>>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in bucket)
            {
                try { await using var stream = File.OpenRead(item.Path); var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
                    if (!hashes.TryGetValue(hash, out var list)) hashes[hash] = list = []; list.Add(item); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            foreach (var pair in hashes.Where(x => x.Value.Count > 1).OrderBy(x => x.Key, StringComparer.Ordinal))
                groups.Add(new("内容完全一致", [.. pair.Value.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)]));
        }
        return groups;
    }
    public async Task<LibraryItem> VerifyAsync(LibraryItem item, CancellationToken ct)
    {
        await using var stream = File.OpenRead(item.Path); var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
        var result = item with { Sha256 = item.Sha256 ?? hash, Size = stream.Length, Status = item.Sha256 != null && !hash.Equals(item.Sha256, StringComparison.OrdinalIgnoreCase) ? "校验不匹配" : "已校验" };
        Upsert(result); return result;
    }
    private static long SidecarStamp(string path) => File.Exists(path + ".switchdesk.json") ? File.GetLastWriteTimeUtc(path + ".switchdesk.json").Ticks : 0;
    private bool MatchesFingerprint(FileInfo info)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Check(Native.sqlite3_prepare_v2(db, "SELECT modified,sidecar_modified,size FROM fingerprints WHERE path=?1", -1, out var stmt, 0));
            try
            {
                Check(Native.sqlite3_bind_text(stmt, 1, info.FullName, -1, new nint(-1)));
                return Native.sqlite3_step(stmt) == 100 && Native.sqlite3_column_int64(stmt, 0) == info.LastWriteTimeUtc.Ticks &&
                    Native.sqlite3_column_int64(stmt, 1) == SidecarStamp(info.FullName) && Native.sqlite3_column_int64(stmt, 2) == info.Length;
            }
            finally { Native.sqlite3_finalize(stmt); }
        }
    }
    private void RecordFingerprint(FileInfo info)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Check(Native.sqlite3_prepare_v2(db, "INSERT OR REPLACE INTO fingerprints VALUES(?1,?2,?3,?4)", -1, out var stmt, 0));
            try
            {
                Check(Native.sqlite3_bind_text(stmt, 1, info.FullName, -1, new nint(-1)));
                Check(Native.sqlite3_bind_int64(stmt, 2, info.LastWriteTimeUtc.Ticks)); Check(Native.sqlite3_bind_int64(stmt, 3, SidecarStamp(info.FullName))); Check(Native.sqlite3_bind_int64(stmt, 4, info.Length));
                if (Native.sqlite3_step(stmt) != 101) Check(1);
            }
            finally { Native.sqlite3_finalize(stmt); }
        }
    }
    public void Dispose() { lock (sync) { if (disposed) return; disposed = true; Native.sqlite3_close_v2(db); } }
    internal static class Native
    {
        private const string Dll = "winsqlite3.dll";
        // Phase2State can be the first database opened. Initializing this nested
        // type must register the resolver independently of LibraryDatabase.
        static Native() => NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, Resolve);
        private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (libraryName != Dll || OperatingSystem.IsWindows()) return nint.Zero;
            foreach (var candidate in new[] { "libsqlite3.dylib", "sqlite3", "libsqlite3.so" })
                if (NativeLibrary.TryLoad(candidate, assembly, searchPath, out var handle)) return handle;
            return nint.Zero;
        }
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out nint db, int flags, nint vfs);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_close_v2(nint db);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern nint sqlite3_errmsg(nint db);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_exec(nint db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, nint cb, nint arg, out nint error);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void sqlite3_free(nint ptr);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_prepare_v2(nint db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int bytes, out nint stmt, nint tail);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_bind_text(nint stmt, int index, [MarshalAs(UnmanagedType.LPUTF8Str)] string? value, int bytes, nint destructor);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_bind_int64(nint stmt, int index, long value);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_step(nint stmt);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern nint sqlite3_column_text(nint stmt, int index);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern long sqlite3_column_int64(nint stmt, int index);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_finalize(nint stmt);
    }
}
