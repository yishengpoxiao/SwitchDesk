using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SwitchDesk.Core;

public sealed record FtpEntry(string Name, string Path, bool IsDirectory, long Size)
{ public string SizeText => IsDirectory ? "文件夹" : Display.Size(Size); }

public sealed record FtpOptions(string Host, int Port, string User, string Password)
{
    public static FtpOptions Anonymous(string host, int port = 21) => new(host, port, "anonymous", "switchdesk@example.com");
}

/// <summary>
/// 局域网文件传输：连接 Switch 上现成的 ftpd / sys-ftpd 服务，与 USB 文件管理共用 PathSafety 保护规则。
/// 仅支持被动模式与二进制传输；列表优先 MLSD，回退 Unix/Windows 风格 LIST。
/// </summary>
public sealed class FtpClient : IDisposable, IAsyncDisposable
{
    private TcpClient? control;
    private StreamReader? reader;
    private StreamWriter? writer;
    public bool IsConnected { get; private set; }

    public async Task ConnectAsync(FtpOptions options, CancellationToken ct = default)
    {
        if (IsConnected) throw new IOException("已连接到局域网主机，请先断开");
        var host = options.Host.Trim();
        if (host.Length == 0) throw new InvalidDataException("请输入主机地址");
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown) throw new InvalidDataException("主机地址格式无效");
        if (options.Port is < 1 or > 65535) throw new InvalidDataException("端口应为 1–65535");
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, options.Port, ct);
            control = client;
            var stream = client.GetStream();
            reader = new StreamReader(stream, Encoding.UTF8);
            writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };
            Expect(await ReadReplyAsync(ct), 2, "连接被拒绝");
            var user = string.IsNullOrWhiteSpace(options.User) ? "anonymous" : options.User;
            var password = string.IsNullOrWhiteSpace(options.Password) ? "switchdesk@example.com" : options.Password;
            if (user.Contains('\r') || user.Contains('\n') || password.Contains('\r') || password.Contains('\n'))
                throw new InvalidDataException("用户名或密码不能包含换行");
            var (code, _) = await SendAsync("USER " + user, ct);
            if (code == 331) (code, _) = await SendAsync("PASS " + password, ct);
            if (code != 230) throw new IOException($"FTP 登录失败（响应 {code}）：请核对用户名与密码");
            Expect(await SendAsync("TYPE I", ct), 2, "无法切换二进制模式");
            IsConnected = true;
        }
        catch (Exception)
        {
            client.Dispose(); CloseControl();
            throw;
        }
    }

    public async Task<IReadOnlyList<FtpEntry>> ListAsync(string path, CancellationToken ct = default)
    {
        var guarded = Guard(path);
        var empty = Array.Empty<FtpEntry>();
        var (first, firstLines) = await TryListAsync("MLSD " + guarded, guarded, ParseMachineList, ct);
        if (first != null) return firstLines == 0 ? empty : first.Count > 0 ? first : await FallbackAsync(guarded, ct);
        var (unix, unixLines) = await TryListAsync("LIST -la " + guarded, guarded, ParseUnixList, ct);
        if (unix != null) return unixLines == 0 ? empty : unix.Count > 0 ? unix : await FallbackAsync(guarded, ct, dos: true);
        var (dos, dosLines) = await TryListAsync("LIST " + guarded, guarded, ParseDosList, ct);
        if (dos != null) return dosLines == 0 ? empty : dos.Count > 0 ? dos : throw new IOException("无法解析目录列表：" + path);
        throw new IOException("主机不支持目录列表命令：" + path);
    }

    private async Task<IReadOnlyList<FtpEntry>> FallbackAsync(string path, CancellationToken ct, bool dos = false)
    {
        if (!dos)
        {
            var (unix, unixLines) = await TryListAsync("LIST -la " + path, path, ParseUnixList, ct);
            if (unix != null && unixLines > 0 && unix.Count > 0) return unix;
            if (unix != null && unixLines == 0) return Array.Empty<FtpEntry>();
        }
        var (entries, lines) = await TryListAsync("LIST " + path, path, ParseDosList, ct);
        if (entries != null && lines == 0) return Array.Empty<FtpEntry>();
        if (entries != null && entries.Count > 0) return entries;
        throw new IOException("无法解析目录列表：" + path);
    }

    public async Task DownloadAsync(string remotePath, string destination, IProgress<TransferProgress>? progress, CancellationToken ct = default)
    {
        var remote = CommandPath(remotePath, writable: false);
        if (File.Exists(destination)) throw new IOException("目标文件已存在，请选择其他名称");
        // Keep staging on the destination volume without extending its filename.
        var partial = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(destination))!, ".switchdesk-" + Guid.NewGuid().ToString("N") + ".partial");
        try
        {
            var size = await SizeAsync(remote, ct);
            await using (var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                await TransferAsync("RETR " + remote, file, size, progress, remote, false, ct);
            File.Move(partial, destination);
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }

    public async Task UploadAsync(string sourcePath, string remotePath, IProgress<TransferProgress>? progress, CancellationToken ct = default)
    {
        var remote = CommandPath(remotePath, writable: true);
        if (await ExistsAsync(remote, ct)) throw new IOException("目标文件已存在，请先重命名或删除");
        var temporary = ParentDirectory(remote).TrimEnd('/') + "/.sdk-" + Guid.NewGuid().ToString("N") + ".partial";
        CommandPath(temporary, writable: true);
        var committed = false;
        try
        {
            await using (var file = File.OpenRead(sourcePath))
                await TransferAsync("STOR " + temporary, file, file.Length, progress, remote, true, ct);
            if (await ExistsAsync(remote, ct)) throw new IOException("上传期间目标名称已被占用，未覆盖现有文件");
            var (code, _) = await SendAsync("RNFR " + temporary, ct);
            if (code != 350)
            {
                // Direct STOR to the final name would expose partial data after a
                // failure and could overwrite a file created during the upload.
                throw new IOException("服务器无法安全完成上传（响应 " + code + "）；请使用支持重命名的 FTP 服务或 USB 传输");
            }
            Expect(await SendAsync("RNTO " + remote, ct), 2, "完成上传失败");
            committed = true;
        }
        finally { if (!committed) await DeleteQuietlyAsync(temporary); }
    }

    /// <summary>递归上传本地目录：逐级 MKD，文件仍走原子写入。</summary>
    public async Task UploadTreeAsync(string localDirectory, string remoteDirectory, IProgress<TransferProgress>? progress, CancellationToken ct = default, int depth = 0)
    {
        if (!Directory.Exists(localDirectory)) { await UploadAsync(localDirectory, remoteDirectory, progress, ct); return; }
        if (depth > 64) throw new IOException("目录层级过深");
        var directory = CommandPath(remoteDirectory, writable: true);
        await EnsureRemoteDirectoryAsync(directory, ct);
        foreach (var entry in Directory.EnumerateFileSystemEntries(localDirectory, "*", new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(localDirectory, entry).Replace(Path.DirectorySeparatorChar, '/');
            PathSafety.Relative(relative);
            var target = PathSafety.Remote(directory.TrimEnd('/') + "/" + relative);
            if (Directory.Exists(entry)) await UploadTreeAsync(entry, target, progress, ct, depth + 1);
            else await UploadAsync(entry, target, progress, ct);
        }
    }

    private async Task EnsureRemoteDirectoryAsync(string path, CancellationToken ct)
    {
        var (code, _) = await SendAsync("MKD " + path, ct);
        if (code / 100 == 2) return;
        // A failed MKD can mean an existing directory or denied access. Even an
        // empty upload must prove that its target directory actually exists.
        if (code is 550 or 521 && (await ListAsync(ParentDirectory(path), ct)).Any(entry => entry.IsDirectory && entry.Path == path)) return;
        throw new IOException("无法创建目标目录（响应 " + code + "）：" + path);
    }

    private async Task<bool> ExistsAsync(string path, CancellationToken ct)
    {
        var (code, _) = await SendAsync("SIZE " + path, ct);
        if (code == 213) return true;
        // SIZE is optional, and a 550 also covers access errors and directories.
        // A failed size query is never proof that a target name is available.
        if (code / 100 != 5) throw new IOException("无法核对目标文件（响应 " + code + "）");
        var entries = await ListAsync(ParentDirectory(path), ct);
        return entries.Any(entry => entry.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
    }

    private async Task DeleteQuietlyAsync(string path)
    {
        if (!IsConnected) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await SendAsync("DELE " + path, timeout.Token);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    private static string ParentDirectory(string path)
    {
        var cut = path.LastIndexOf('/');
        return cut <= 0 ? "/" : path[..cut];
    }

    public async Task MkdirAsync(string path, CancellationToken ct = default)
    {
        var remote = CommandPath(path, writable: true);
        Expect(await SendAsync("MKD " + remote, ct), 2, "创建目录失败");
    }

    public async Task RenameAsync(string path, string target, CancellationToken ct = default)
    {
        var remote = CommandPath(path, writable: true, protectDescendants: true);
        var destination = CommandPath(target, writable: true, protectDescendants: true);
        var (code, _) = await SendAsync("RNFR " + remote, ct);
        if (code != 350) throw new IOException("重命名失败：源路径不可用（响应 " + code + "）");
        Expect(await SendAsync("RNTO " + destination, ct), 2, "重命名失败");
    }

    public async Task DeleteAsync(string path, CancellationToken ct = default)
    {
        var remote = CommandPath(path, writable: true, protectDescendants: true);
        Expect(await SendAsync("DELE " + remote, ct), 2, "删除失败");
    }

    public async Task RemoveDirectoryAsync(string path, CancellationToken ct = default)
    {
        var remote = CommandPath(path, writable: true, protectDescendants: true);
        Expect(await SendAsync("RMD " + remote, ct), 2, "删除目录失败（目录需先清空）");
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        if (!IsConnected) return;
        IsConnected = false;
        try { await SendAsync("QUIT", ct); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        finally { CloseControl(); }
    }

    public void Dispose()
    {
        // 不做优雅 QUIT（可能死锁在 UI 线程 SynchronizationContext 上），直接释放。
        CloseControl();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (IsConnected)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await DisconnectAsync(timeout.Token);
        }
        CloseControl();
        GC.SuppressFinalize(this);
    }

    private static string Guard(string path)
    {
        var remote = PathSafety.Remote(path);
        if (remote.Contains('\r') || remote.Contains('\n')) throw new InvalidDataException("路径不允许包含换行");
        return remote;
    }

    /// <summary>所有命令统一走这里：规范化路径、拒绝换行注入，写操作再套用保护规则。</summary>
    private static string CommandPath(string path, bool writable, bool protectDescendants = false)
    {
        var remote = Guard(path);
        if (writable) PathSafety.DemandWritable(remote, protectDescendants: protectDescendants);
        return remote;
    }

    private async Task<long?> SizeAsync(string path, CancellationToken ct)
    {
        var (code, lines) = await SendAsync("SIZE " + Guard(path), ct);
        var text = lines[^1];
        var body = text.Length > 4 ? text[4..].Trim() : "";
        return code == 213 && long.TryParse(body, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) && size >= 0 ? size : null;
    }

    private async Task TransferAsync(string command, Stream stream, long? total, IProgress<TransferProgress>? progress, string stage, bool upload, CancellationToken ct)
    {
        var endpoint = await PassiveEndpointAsync(ct);
        using var data = new TcpClient();
        await data.ConnectAsync(endpoint.Address, endpoint.Port, ct);
        var (code, _) = await SendAsync(command, ct);
        if (code is not (125 or 150)) throw new IOException("传输请求被拒绝（响应 " + code + "）");
        long completed = 0;
        int finalCode;
        try
        {
            await using (var dataStream = data.GetStream())
            {
                var buffer = new byte[81920];
                if (upload)
                {
                    int read;
                    while ((read = await stream.ReadAsync(buffer, ct)) > 0)
                    {
                        await dataStream.WriteAsync(buffer.AsMemory(0, read), ct);
                        completed += read; progress?.Report(new(completed, total ?? 0, stage));
                    }
                }
                else
                {
                    int read;
                    while ((read = await dataStream.ReadAsync(buffer, ct)) > 0)
                    {
                        await stream.WriteAsync(buffer.AsMemory(0, read), ct);
                        completed += read; progress?.Report(new(completed, total ?? 0, stage));
                    }
                }
            }
            data.Close();
            (finalCode, _) = await ReadReplyAsync(ct);
        }
        catch (OperationCanceledException)
        {
            data.Close();
            await AbortAsync();
            throw;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // 非取消异常（磁盘满、连接重置等）：仍需排空服务器回复，否则控制连接失步。
            data.Close();
            try { await AbortAsync(); } catch { }
            throw;
        }
        if (finalCode is not (226 or 250)) throw new IOException($"传输失败（响应 {finalCode}）");
        if (total.HasValue && completed != total.Value) throw new EndOfStreamException("传输长度与文件大小不一致，文件未完整传输");
    }

    /// <summary>用 ABOR 后的 NOOP 回复确认旧传输回复已排空；超时则关闭无法确认同步的连接。</summary>
    private async Task AbortAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await writer!.WriteLineAsync("ABOR".AsMemory(), timeout.Token);
            await writer.WriteLineAsync("NOOP".AsMemory(), timeout.Token);
            while (true)
            {
                var (code, _) = await ReadReplyAsync(timeout.Token);
                // Transfer/ABOR replies use 125/150/225/226/250/426. Only
                // the subsequent NOOP's 200 establishes a drained boundary.
                if (code == 200) return;
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { CloseControl(); }
    }

    private async Task<IPEndPoint> PassiveEndpointAsync(CancellationToken ct)
    {
        var (code, lines) = await SendAsync("PASV", ct);
        if (code != 227) throw new IOException("主机不支持被动模式（响应 " + code + "）");
        var numbers = new List<int>();
        foreach (var token in lines[^1].Split('(', ')', ',', ' ', '\t'))
            if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) numbers.Add(value);
        if (numbers.Count >= 6)
        {
            var tail = numbers.TakeLast(6).ToArray();
            if (tail.All(octet => octet is >= 0 and <= 255) && tail[4] * 256 + tail[5] is > 0 and < 65536)
            {
                var address = new IPAddress(tail.Take(4).Select(octet => (byte)octet).ToArray());
                // 部分服务器在 PASV 中返回 0.0.0.0；此时沿用控制连接对端地址。
                if (address.Equals(IPAddress.Any) && control?.Client.RemoteEndPoint is IPEndPoint peer) address = peer.Address;
                return new(address, tail[4] * 256 + tail[5]);
            }
        }
        throw new InvalidDataException("无法解析被动模式地址：" + lines[^1]);
    }

    private async Task<(IReadOnlyList<FtpEntry>? Entries, int Lines)> TryListAsync(string command, string directory, Func<IReadOnlyList<string>, string, IReadOnlyList<FtpEntry>> parse, CancellationToken ct)
    {
        var endpoint = await PassiveEndpointAsync(ct);
        using var data = new TcpClient();
        await data.ConnectAsync(endpoint.Address, endpoint.Port, ct);
        var (code, _) = await SendAsync(command, ct);
        if (code is >= 400 and < 600) { data.Close(); return (null, 0); }
        if (code is not (125 or 150)) throw new IOException("列表请求被拒绝（响应 " + code + "）");
        var lines = new List<string>();
        try
        {
            await using (var dataStream = data.GetStream())
                using (var listReader = new StreamReader(dataStream, Encoding.UTF8))
                    while (await listReader.ReadLineAsync(ct) is { } line) lines.Add(line);
            data.Close();
            var (finalCode, _) = await ReadReplyAsync(ct);
            if (finalCode is >= 400 and < 600) return (null, 0); // 传输中止，让调用方回退
            if (finalCode is not (226 or 250)) throw new IOException($"目录传输失败（响应 {finalCode}）");
            return (parse(lines, directory), lines.Count);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            data.Close();
            try { await AbortAsync(); } catch { CloseControl(); }
            throw;
        }
    }

    private static IReadOnlyList<FtpEntry> ParseMachineList(IReadOnlyList<string> lines, string baseDirectory)
    {
        var result = new List<FtpEntry>();
        foreach (var line in lines)
        {
            if (line.Length == 0) continue;
            // RFC 3659: facts contain no spaces; everything after the first
            // separating space belongs to the pathname, including "; ".
            var separator = line.IndexOf(' ');
            if (separator < 0) continue;
            var name = line[(separator + 1)..];
            if (name.Length == 0 || name is "." or "..") continue;
            var isDirectory = false; long size = 0;
            foreach (var fact in line[..separator].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var pair = fact.Split('=', 2);
                if (pair.Length != 2) continue;
                var key = pair[0].ToLowerInvariant();
                if (key == "type") isDirectory = pair[1].Equals("dir", StringComparison.OrdinalIgnoreCase);
                else if (key is "size" or "unix.size") long.TryParse(pair[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out size);
            }
            result.Add(new(name, baseDirectory.TrimEnd('/') + "/" + name, isDirectory, size));
        }
        return result;
    }

    private static IReadOnlyList<FtpEntry> ParseUnixList(IReadOnlyList<string> lines, string baseDirectory)
    {
        var result = new List<FtpEntry>();
        foreach (var line in lines)
        {
            var tokens = line.Split(' ', 9, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 9 || tokens[0].Length == 0) continue;
            var kind = tokens[0][0];
            if (kind is not ('-' or 'd' or 'l')) continue;
            var name = tokens[8];
            var arrow = name.IndexOf(" -> ", StringComparison.Ordinal);
            if (kind == 'l' && arrow >= 0) name = name[..arrow];
            if (name.Length == 0 || name is "." or "..") continue;
            _ = long.TryParse(tokens[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size);
            result.Add(new(name, baseDirectory.TrimEnd('/') + "/" + name, kind == 'd', size));
        }
        return result;
    }

    private static IReadOnlyList<FtpEntry> ParseDosList(IReadOnlyList<string> lines, string baseDirectory)
    {
        var result = new List<FtpEntry>();
        foreach (var line in lines)
        {
            var tokens = line.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 4) continue;
            var date = tokens[0];
            var looksLikeDosDate = date.Length == 8 && date.Split('-').Length == 3 && date.All(c => char.IsAsciiDigit(c) || c == '-') &&
                tokens[1].Length is >= 4 and <= 7 && tokens[1].EndsWith("M", StringComparison.OrdinalIgnoreCase);
            if (!looksLikeDosDate) continue;
            var isDirectory = tokens[2].Equals("<DIR>", StringComparison.OrdinalIgnoreCase);
            _ = long.TryParse(tokens[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size);
            result.Add(new(tokens[3], baseDirectory.TrimEnd('/') + "/" + tokens[3], isDirectory, isDirectory ? 0 : size));
        }
        return result;
    }

    private void Expect((int Code, List<string> Lines) reply, int group, string message)
    { if (reply.Code / 100 != group) throw new IOException(message + "（响应 " + reply.Code + "）"); }

    private async Task<(int Code, List<string> Lines)> SendAsync(string command, CancellationToken ct)
    {
        if (writer == null || control == null) throw new IOException("未连接到局域网主机");
        ct.ThrowIfCancellationRequested();
        try
        {
            await writer.WriteLineAsync(command.AsMemory(), ct);
            return await ReadReplyAsync(ct);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The server may still send this command's reply after cancellation.
            // Do not let a later command consume it as its own result.
            CloseControl();
            throw;
        }
    }

    private async Task<(int Code, List<string> Lines)> ReadReplyAsync(CancellationToken ct)
    {
        var lines = new List<string>();
        int? multilineCode = null;
        try
        {
            while (true)
            {
                var line = await reader!.ReadLineAsync(ct) ?? throw new EndOfStreamException("主机关闭了控制连接");
                lines.Add(line);
                if (line.Length < 3 || !char.IsAsciiDigit(line[0]) || !char.IsAsciiDigit(line[1]) || !char.IsAsciiDigit(line[2])) continue;
                var code = int.Parse(line[..3], CultureInfo.InvariantCulture);
                if (multilineCode.HasValue)
                {
                    if (code == multilineCode.Value && (line.Length == 3 || line[3] == ' ')) return (code, lines);
                }
                else if (line.Length > 3 && line[3] == '-') multilineCode = code;
                else if (line.Length == 3 || line[3] == ' ') return (code, lines);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            IsConnected = false;
            throw;
        }
    }

    private void CloseControl()
    {
        IsConnected = false;
        reader?.Dispose(); writer?.Dispose(); control?.Dispose();
        reader = null; writer = null; control = null;
    }
}
