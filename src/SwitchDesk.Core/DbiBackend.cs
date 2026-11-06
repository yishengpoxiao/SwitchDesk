using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;

namespace SwitchDesk.Core;

public sealed record DbiBackendSource(string Path, long? ExpectedLength = null, string? ExpectedSha256 = null);

internal sealed record DbiBackendAsset(string Url, long Length, string Sha256);

/// <summary>Prepares an external official DBI backend. It never installs console content itself.</summary>
public sealed class DbiBackendService : IDisposable
{
    public const string OfficialVersion = "912ru";
    public const string OfficialUrl = "https://github.com/rashevskyv/dbi/releases/download/912ru/dbibackend.exe";
    public const long OfficialLength = 10609140;
    public const string OfficialSha256 = "D2CA9E101609DFDFF56F94446B08B9DE75E344489885A78DAAC6021E43992FF2";
    public const string ExecutableName = "dbibackend.exe";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> CacheGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> officialPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient http;
    private readonly DbiBackendAsset asset;
    private readonly bool ownsHttp;
    private bool disposed;

    public DbiBackendService() : this(new HttpClient(new SocketsHttpHandler
        { AllowAutoRedirect = true, MaxAutomaticRedirections = 5, AutomaticDecompression = DecompressionMethods.None })
        { Timeout = TimeSpan.FromMinutes(5) }, OfficialAsset, true) { }
    public DbiBackendService(HttpClient client) : this(client, OfficialAsset, false) { }
    internal DbiBackendService(HttpClient client, DbiBackendAsset fixtureAsset) : this(client, fixtureAsset, false) { }
    private DbiBackendService(HttpClient client, DbiBackendAsset downloadAsset, bool ownsClient)
    { http = client; asset = downloadAsset; ownsHttp = ownsClient; }
    private static DbiBackendAsset OfficialAsset => new(OfficialUrl, OfficialLength, OfficialSha256);
    public static string GetOfficialCachePath(string cacheDirectory)
    {
        ValidateAbsolutePath(cacheDirectory);
        return Path.Combine(Path.GetFullPath(cacheDirectory), OfficialVersion, ExecutableName);
    }

    public static void ValidateExecutable(string path)
    {
        ValidateAbsolutePath(path);
        if (!Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new FileNotFoundException("请选择已存在的 DBI 后端 .exe 文件。", path);
        LocalSafety.DemandRegularFile(path);
    }

    public async Task<string> ResolveAsync(string? configuredPath, string cacheDirectory,
        IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(configuredPath)) return await DownloadOfficialAsync(cacheDirectory, progress, ct);
        ValidateExecutable(configuredPath);
        var full = Path.GetFullPath(configuredPath);
        if (full.Equals(GetOfficialCachePath(cacheDirectory), StringComparison.OrdinalIgnoreCase))
        {
            await VerifyOfficialAsync(full, ct);
            officialPaths.TryAdd(full, 0);
        }
        return full;
    }

    public async Task<string> DownloadOfficialAsync(string cacheDirectory, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var destination = GetOfficialCachePath(cacheDirectory);
        var directory = Path.GetDirectoryName(destination)!;
        EnsureDirectory(directory);
        var gate = CacheGates.GetOrAdd(destination, _ => new(1));
        await gate.WaitAsync(ct);
        try
        {
            if (File.Exists(destination))
            {
                // A damaged cache is never treated as an executable chosen by the user.
                await VerifyOfficialAsync(destination, ct);
                officialPaths.TryAdd(destination, 0);
                return destination;
            }
            var partial = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, asset.Url);
                request.Headers.AcceptEncoding.ParseAdd("identity");
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentEncoding.Any(value => !value.Equals("identity", StringComparison.OrdinalIgnoreCase))
                    || response.Content.Headers.ContentLength is { } length && length != asset.Length)
                    throw new InvalidDataException("DBI 官方后端下载长度或编码与锁定版本不符。");
                if (response.RequestMessage?.RequestUri is { } final && final.Scheme != Uri.UriSchemeHttps)
                    throw new InvalidDataException("DBI 官方后端下载不能降级为非 HTTPS 连接。");
                await using (var input = await response.Content.ReadAsStreamAsync(ct))
                await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, Wire.ChunkSize, true))
                {
                    var buffer = new byte[Wire.ChunkSize]; long written = 0;
                    while (written < asset.Length)
                    {
                        var read = await StreamReads.WithInactivityTimeoutAsync(input,
                            buffer.AsMemory(0, (int)Math.Min(buffer.Length, asset.Length - written)), ct);
                        if (read == 0) throw new EndOfStreamException("DBI 官方后端下载中断，未发布缓存文件。");
                        await output.WriteAsync(buffer.AsMemory(0, read), ct); written += read;
                        progress?.Report(new(written, asset.Length, "下载官方 DBI 后端到电脑"));
                    }
                    if (await StreamReads.WithInactivityTimeoutAsync(input, buffer.AsMemory(0, 1), ct) != 0)
                        throw new InvalidDataException("DBI 官方后端下载内容超过锁定长度。");
                    output.Flush(true);
                }
                await VerifyOfficialAsync(partial, ct);
                ct.ThrowIfCancellationRequested();
                File.Move(partial, destination);
                officialPaths.TryAdd(destination, 0);
                return destination;
            }
            finally { if (File.Exists(partial)) File.Delete(partial); }
        }
        finally { gate.Release(); }
    }

    private async Task VerifyOfficialAsync(string path, CancellationToken ct)
    {
        LocalSafety.DemandRegularFile(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, Wire.ChunkSize, true);
        await VerifyOfficialStreamAsync(stream, ct);
    }
    private async Task VerifyOfficialStreamAsync(FileStream stream, CancellationToken ct)
    {
        if (stream.Length != asset.Length || !Fingerprints.Equal(Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)), asset.Sha256))
            throw new InvalidDataException("DBI 官方缓存的长度或 SHA-256 不匹配；请删除该缓存后重新下载，或选择自备后端。");
        stream.Position = 0;
    }

    public Task<DbiBackendSession> PrepareAsync(string backendPath, IEnumerable<DbiBackendSource> sources, CancellationToken ct) =>
        PrepareAsync(backendPath, sources, info => new DbiBackendProcess(info), ct);

    internal async Task<DbiBackendSession> PrepareAsync(string backendPath, IEnumerable<DbiBackendSource> sources,
        Func<ProcessStartInfo, IDbiBackendProcess> startProcess, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(sources);
        var selected = sources.ToArray();
        if (selected.Any(source => source is null)) throw new InvalidDataException("DBI 文件列表包含空项。");
        var info = BuildStartInfo(backendPath, selected.Select(source => source.Path));
        var locked = new List<DbiLockedFile>();
        try
        {
            ct.ThrowIfCancellationRequested();
            var backend = DbiLockedFile.Open(info.FileName);
            locked.Add(backend);
            if (officialPaths.ContainsKey(info.FileName)) await VerifyOfficialStreamAsync(backend.Stream, ct);
            foreach (var source in selected)
            {
                ct.ThrowIfCancellationRequested();
                if (source.ExpectedLength is <= 0 || source.ExpectedSha256 is not null && !Fingerprints.Valid(source.ExpectedSha256))
                    throw new InvalidDataException("DBI 文件长度或摘要约束无效。");
                var file = DbiLockedFile.Open(Path.GetFullPath(source.Path)); locked.Add(file);
                if (file.Stream.Length <= 0 || source.ExpectedLength.HasValue && file.Stream.Length != source.ExpectedLength)
                    throw new InvalidDataException("交给 DBI 的文件长度与已确认记录不符：" + Path.GetFileName(source.Path));
                if (source.ExpectedSha256 is not null && !Fingerprints.Equal(Convert.ToHexString(await SHA256.HashDataAsync(file.Stream, ct)), source.ExpectedSha256))
                    throw new InvalidDataException("交给 DBI 的文件 SHA-256 与已确认记录不符：" + Path.GetFileName(source.Path));
                file.Stream.Position = 0;
                file.Validate();
            }
            ct.ThrowIfCancellationRequested();
            return new(info, selected, locked, startProcess);
        }
        catch { foreach (var file in locked) file.Dispose(); throw; }
    }

    public static ProcessStartInfo BuildStartInfo(string backendPath, IEnumerable<string> paths)
    {
        ValidateExecutable(backendPath);
        ArgumentNullException.ThrowIfNull(paths);
        var info = new ProcessStartInfo(Path.GetFullPath(backendPath))
        {
            UseShellExecute = false, CreateNoWindow = false, WindowStyle = ProcessWindowStyle.Normal,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(backendPath))!
        };
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long commandLength = QuotedLength(info.FileName) + 1;
        foreach (var path in paths)
        {
            ValidateAbsolutePath(path);
            var full = Path.GetFullPath(path);
            if (Path.GetExtension(full).ToLowerInvariant() is not (".nsp" or ".nsz" or ".xci" or ".xcz"))
                throw new InvalidDataException("DBI 文件列表仅支持 NSP、NSZ、XCI、XCZ。");
            if (!names.Add(Path.GetFileName(full))) throw new InvalidDataException("DBI 无法区分同名文件，请分开提供：" + Path.GetFileName(full));
            commandLength += QuotedLength(full) + 1;
            if (commandLength > 32767) throw new InvalidDataException("DBI 文件列表超过 Windows 命令行长度限制，请分批提供。");
            info.ArgumentList.Add(full);
        }
        if (info.ArgumentList.Count == 0) throw new InvalidDataException("请先选择要交给 DBI 的文件。");
        return info;
    }

    private static long QuotedLength(string value)
    {
        // Count a conservatively quoted CreateProcess argument, including escaped trailing backslashes.
        long length = 2, slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            length += character == '"' ? slashes * 2 + 2 : slashes + 1;
            slashes = 0;
        }
        return length + slashes * 2;
    }
    private static void ValidateAbsolutePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.Any(char.IsControl))
            throw new InvalidDataException("DBI 路径必须为绝对路径，且不能包含换行或控制字符。");
    }
    private static void EnsureDirectory(string path)
    {
        for (var ancestor = path; ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
            if (Directory.Exists(ancestor)) { LocalSafety.DemandDirectory(ancestor); break; }
        Directory.CreateDirectory(path); LocalSafety.DemandDirectory(path);
    }
    public void Dispose() { if (disposed) return; disposed = true; if (ownsHttp) http.Dispose(); }
}

internal sealed class DbiLockedFile(string path, FileStream stream, long length, DateTime modified) : IDisposable
{
    internal FileStream Stream => stream;
    internal static DbiLockedFile Open(string path)
    {
        LocalSafety.DemandRegularFile(path);
        var stamp = File.GetLastWriteTimeUtc(path);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, Wire.ChunkSize, true);
        var result = new DbiLockedFile(path, stream, stream.Length, stamp);
        try { result.Validate(); return result; }
        catch { stream.Dispose(); throw; }
    }
    internal void Validate()
    {
        if (!File.Exists(path) || stream.Length != length || File.GetLastWriteTimeUtc(path) != modified)
            throw new IOException("DBI 文件在准备后发生变化，请重新选择：" + Path.GetFileName(path));
    }
    public void Dispose() => stream.Dispose();
}

internal interface IDbiBackendProcess : IDisposable
{
    int Id { get; }
    bool HasExited { get; }
    int ExitCode { get; }
    Task WaitForExitAsync(CancellationToken ct);
    bool CloseMainWindow();
}
internal sealed class DbiBackendProcess : IDbiBackendProcess
{
    private readonly Process process;
    internal DbiBackendProcess(ProcessStartInfo info)
    {
        process = new() { StartInfo = info };
        try { if (!process.Start()) throw new IOException("DBI 后端进程未能启动。"); }
        catch { process.Dispose(); throw; }
    }
    public int Id => process.Id;
    public bool HasExited => process.HasExited;
    public int ExitCode => process.ExitCode;
    public Task WaitForExitAsync(CancellationToken ct) => process.WaitForExitAsync(ct);
    public bool CloseMainWindow() => process.CloseMainWindow();
    public void Dispose() => process.Dispose();
}

/// <summary>Holds package files read-only until the external backend really exits. Exit is not an installation result.</summary>
public sealed class DbiBackendSession : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly ProcessStartInfo startInfo;
    private readonly List<DbiLockedFile> files;
    private readonly Func<ProcessStartInfo, IDbiBackendProcess> startProcess;
    private IDbiBackendProcess? process;
    private Task<int>? completion;
    private bool disposed, started, exited;
    private int processId;
    internal DbiBackendSession(ProcessStartInfo info, DbiBackendSource[] sources, List<DbiLockedFile> locked,
        Func<ProcessStartInfo, IDbiBackendProcess> factory)
    { startInfo = info; Sources = Array.AsReadOnly(sources); files = locked; startProcess = factory; }
    public IReadOnlyList<DbiBackendSource> Sources { get; }
    public bool HasStarted { get { lock (gate) return started; } }
    public bool HasExited { get { lock (gate) return exited || process?.HasExited == true; } }
    public int ProcessId { get { lock (gate) return processId; } }
    public void Start()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (started) throw new InvalidOperationException("DBI 后端会话已经启动。");
            try
            {
                foreach (var file in files) file.Validate();
                process = startProcess(startInfo); processId = process.Id; started = true;
                completion = ObserveExitAsync(process);
            }
            catch { ReleaseFiles(); disposed = true; throw; }
        }
    }
    private async Task<int> ObserveExitAsync(IDbiBackendProcess owned)
    {
        await owned.WaitForExitAsync(CancellationToken.None);
        if (!owned.HasExited) throw new IOException("DBI 后端尚未退出，文件保持只读锁定。");
        var code = owned.ExitCode;
        lock (gate) { exited = true; ReleaseFiles(); }
        return code;
    }
    public async Task WaitForExitAsync(CancellationToken ct = default)
    {
        Task<int> pending;
        lock (gate) pending = completion ?? throw new InvalidOperationException("DBI 后端会话尚未启动。");
        var code = await pending.WaitAsync(ct);
        if (code != 0) throw new IOException($"DBI 后端已退出，退出码 {code}。请查看外部 DBI 后端窗口及主机提示。");
    }
    public bool RequestClose()
    {
        lock (gate)
            return !disposed && process is not null && !exited && !process.HasExited && process.CloseMainWindow();
    }
    public async ValueTask DisposeAsync()
    {
        Task<int>? pending;
        lock (gate) { if (disposed) return; pending = completion; }
        if (pending is not null) await pending; // Never terminate the external process or unlock while it is alive.
        lock (gate)
        {
            if (disposed) return;
            ReleaseFiles(); process?.Dispose(); disposed = true;
        }
    }
    private void ReleaseFiles() { foreach (var file in files) file.Dispose(); files.Clear(); }
}
