using System.Net.Http;
using System.Text.Json;
using SwitchDesk.Core;

// SwitchDesk 跨平台命令行（Windows / macOS / Linux）。
// 覆盖不依赖 WinUSB 的便携功能：局域网 FTP 传输、电脑游戏库维护、存档快照校验/对比/导出、
// 公共更新目录查询与 Homebrew 索引浏览、投屏参数构造。USB 主机管理仍在 Windows 桌面版。

if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
{
    Console.WriteLine("""
        SwitchDesk.Cli —— 跨平台命令行
        用法:
          --version                                           显示版本与源码标识
          ftp-ls <host> <port> [user] [password] [path]        列出局域网主机目录
          ftp-get <host> <port> <user> <password> <remote> <localFile>
          ftp-put <host> <port> <user> <password> <localFile> <remote>
          ftp-put-tree <host> <port> <user> <password> <localDir> <remoteDir>
          library-scan <db> <directory>                        扫描目录入索引
          library-list <db>                                    列出索引
          library-verify <db>                                  批量校验全部条目
          library-clean <db>                                   清理「文件缺失」索引
          library-dupes <db>                                   查找重复
          snapshot-verify <zip>                                校验快照
          snapshot-compare <oldZip> <newZip>                   对比两份快照
          snapshot-export <zip> <destDir>                      导出快照副本
          updates-check [cacheFile]                            查询公共更新目录并输出缓存
          homebrew-index <indexUrl> [cacheFile]                拉取 Homebrew JSON 索引
          stream-args [--mode usb|bridge] [--address ip] [--title text]
                                                               输出 SysDVR-Client 参数
        退出码: 0 成功；2 未知命令；3 参数或操作错误；4 校验存在失败；130 用户取消。
        ftp-ls 可省略匿名用户和密码；其他 FTP 命令用 - - 占位。Ctrl+C 取消并清理暂存文件。
        """);
    return 0;
}
using var cancellation = new CancellationTokenSource();
ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
Console.CancelKeyPress += onCancel;
var ct = cancellation.Token;
try
{
    try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
    if (args[0] == "--version")
    {
        if (args.Length != 1) throw new InvalidDataException("用法: --version");
        Console.WriteLine(BuildInfo.ClientId); return 0;
    }
    return args[0] switch
    {
        "ftp-ls" => await FtpListAsync(args[1..], ct),
        "ftp-get" => await FtpGetAsync(args[1..], ct),
        "ftp-put" => await FtpPutAsync(args[1..], ct),
        "ftp-put-tree" => await FtpPutTreeAsync(args[1..], ct),
        "library-scan" => await LibraryScanAsync(args[1..], ct),
        "library-list" => LibraryList(args[1..]),
        "library-verify" => await LibraryVerifyAsync(args[1..], ct),
        "library-clean" => LibraryClean(args[1..], ct),
        "library-dupes" => await LibraryDupesAsync(args[1..], ct),
        "snapshot-verify" => await SnapshotVerifyAsync(args[1..], ct),
        "snapshot-compare" => await SnapshotCompareAsync(args[1..], ct),
        "snapshot-export" => await SnapshotExportAsync(args[1..], ct),
        "updates-check" => await UpdatesCheckAsync(args[1..], ct),
        "homebrew-index" => await HomebrewIndexAsync(args[1..], ct),
        "stream-args" => StreamArgs(args[1..]),
        _ => Unknown(args[0]),
    };
}
catch (OperationCanceledException) when (ct.IsCancellationRequested)
{ Console.Error.WriteLine("已取消；未完成的操作不会标记为成功。"); return 130; }
catch (Exception ex) when (ex is IOException or InvalidDataException or HttpRequestException or System.Net.Sockets.SocketException or OperationCanceledException or UnauthorizedAccessException or ArgumentException or FormatException or OverflowException or System.Text.Json.JsonException)
{ Console.Error.WriteLine("失败：" + ex.Message); return 3; }
finally { Console.CancelKeyPress -= onCancel; }

static int Unknown(string command) { Console.Error.WriteLine("未知命令：" + command); return 2; }

static (string Host, int Port, string User, string Password) FtpArgs(string[] arguments, out string[] rest)
{
    if (arguments.Length < 4) throw new InvalidDataException("FTP 命令需要 <host> <port> <user> <password> 前缀");
    if (!int.TryParse(arguments[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
        throw new InvalidDataException("FTP 端口应为 1–65535");
    var user = arguments[2] == "-" ? "" : arguments[2];
    var password = arguments[3] == "-" ? "" : arguments[3];
    rest = arguments[4..];
    return (arguments[0], port, user, password);
}

static async Task<int> FtpListAsync(string[] arguments, CancellationToken ct)
{
    if (arguments.Length is < 2 or > 5) throw new InvalidDataException("用法: ftp-ls <host> <port> [user] [password] [path]");
    var prefix = new[] { arguments[0], arguments[1], arguments.ElementAtOrDefault(2) ?? "-", arguments.ElementAtOrDefault(3) ?? "-" };
    var (host, port, user, password) = FtpArgs(prefix, out _);
    await using var ftp = new FtpClient();
    await ftp.ConnectAsync(new FtpOptions(host, port, user, password), ct);
    var entries = await ftp.ListAsync(arguments.ElementAtOrDefault(4) ?? "/", ct);
    foreach (var entry in entries) Console.WriteLine((entry.IsDirectory ? "d " : "  ") + entry.Name + "\t" + entry.SizeText + "\t" + entry.Path);
    return 0;
}

static async Task<int> FtpGetAsync(string[] arguments, CancellationToken ct)
{
    var (host, port, user, password) = FtpArgs(arguments, out var rest);
    if (rest.Length != 2) throw new InvalidDataException("用法: ftp-get <host> <port> <user> <password> <remote> <localFile>");
    await using var ftp = new FtpClient();
    await ftp.ConnectAsync(new FtpOptions(host, port, user, password), ct);
    await ftp.DownloadAsync(rest[0], rest[1], new Progress<TransferProgress>(p => Console.Error.WriteLine($"{p.Percent:0}%  {p.Stage}")), ct);
    Console.WriteLine("已下载 → " + rest[1]); return 0;
}

static async Task<int> FtpPutAsync(string[] arguments, CancellationToken ct)
{
    var (host, port, user, password) = FtpArgs(arguments, out var rest);
    if (rest.Length != 2) throw new InvalidDataException("用法: ftp-put <host> <port> <user> <password> <localFile> <remote>");
    await using var ftp = new FtpClient();
    await ftp.ConnectAsync(new FtpOptions(host, port, user, password), ct);
    await ftp.UploadAsync(rest[0], rest[1], new Progress<TransferProgress>(p => Console.Error.WriteLine($"{p.Percent:0}%  {p.Stage}")), ct);
    Console.WriteLine("已上传 → " + rest[1]); return 0;
}

static async Task<int> FtpPutTreeAsync(string[] arguments, CancellationToken ct)
{
    var (host, port, user, password) = FtpArgs(arguments, out var rest);
    if (rest.Length != 2) throw new InvalidDataException("用法: ftp-put-tree <host> <port> <user> <password> <localDir> <remoteDir>");
    await using var ftp = new FtpClient();
    await ftp.ConnectAsync(new FtpOptions(host, port, user, password), ct);
    await ftp.UploadTreeAsync(rest[0], rest[1], new Progress<TransferProgress>(p => Console.Error.WriteLine($"{p.Percent:0}%  {p.Stage}")), ct);
    Console.WriteLine("已上传目录 → " + rest[1]); return 0;
}

static async Task<int> LibraryScanAsync(string[] arguments, CancellationToken ct)
{
    if (arguments.Length != 2) throw new InvalidDataException("用法: library-scan <db> <directory>");
    using var db = new LibraryDatabase(arguments[0]);
    await db.ScanAsync(arguments[1], ct);
    Console.WriteLine("索引条目：" + db.List().Count); return 0;
}

static int LibraryList(string[] arguments)
{
    if (arguments.Length != 1) throw new InvalidDataException("用法: library-list <db>");
    using var db = new LibraryDatabase(arguments[0]);
    foreach (var item in db.List()) Console.WriteLine(item.Status + "\t" + item.Name + "\t" + item.SizeText + "\t" + (item.TitleId ?? "-") + "\t" + item.Path);
    return 0;
}

static async Task<int> LibraryVerifyAsync(string[] arguments, CancellationToken ct)
{
    if (arguments.Length != 1) throw new InvalidDataException("用法: library-verify <db>");
    using var db = new LibraryDatabase(arguments[0]);
    var report = await db.VerifyAllAsync(new Progress<TransferProgress>(p => Console.Error.WriteLine($"{p.Completed}/{p.Total}  {p.Stage}")), ct);
    Console.WriteLine($"校验完成 · 通过 {report.Verified} · 不匹配 {report.Mismatched} · 跳过 {report.Skipped}");
    return report.Mismatched == 0 && report.Skipped == 0 ? 0 : 4;
}

static int LibraryClean(string[] arguments, CancellationToken ct)
{
    if (arguments.Length != 1) throw new InvalidDataException("用法: library-clean <db>");
    using var db = new LibraryDatabase(arguments[0]);
    Console.WriteLine("已清理 " + db.RemoveMissing(ct) + " 条缺失索引（文件不受影响）"); return 0;
}

static async Task<int> LibraryDupesAsync(string[] arguments, CancellationToken ct)
{
    if (arguments.Length != 1) throw new InvalidDataException("用法: library-dupes <db>");
    using var db = new LibraryDatabase(arguments[0]);
    var groups = await db.FindDuplicatesAsync(ct);
    foreach (var group in groups)
    {
        Console.WriteLine("== " + group.Reason);
        foreach (var item in group.Items) Console.WriteLine("   " + item.Path + "\t" + item.SizeText);
    }
    Console.WriteLine(groups.Count + " 组重复"); return 0;
}

static async Task<int> SnapshotVerifyAsync(string[] arguments, CancellationToken ct)
{
    if (arguments.Length != 1) throw new InvalidDataException("用法: snapshot-verify <zip>");
    var snapshot = await SnapshotStore.VerifyAsync(arguments[0], ct);
    Console.WriteLine(snapshot.Manifest.GameName + " · " + snapshot.Manifest.UserName + " · " + snapshot.TimeText + " · " + snapshot.Manifest.Files.Length + " 个文件 · 校验通过");
    return 0;
}

static async Task<int> SnapshotCompareAsync(string[] arguments, CancellationToken ct)
{
    if (arguments.Length != 2) throw new InvalidDataException("用法: snapshot-compare <oldZip> <newZip>");
    var diff = await SnapshotTools.CompareAsync(arguments[0], arguments[1], ct);
    Console.WriteLine(diff.Current.Manifest.GameName + " · " + diff.Summary);
    foreach (var line in diff.Details()) Console.WriteLine("  " + line);
    return 0;
}

static async Task<int> SnapshotExportAsync(string[] arguments, CancellationToken ct)
{
    if (arguments.Length != 2) throw new InvalidDataException("用法: snapshot-export <zip> <destDir>");
    var destination = await SnapshotTools.ExportSnapshotAsync(arguments[0], arguments[1], ct);
    Console.WriteLine("已导出 → " + destination); return 0;
}

static async Task<int> UpdatesCheckAsync(string[] arguments, CancellationToken ct)
{
    if (arguments.Length > 1) throw new InvalidDataException("用法: updates-check [cacheFile]");
    var catalog = await TitleDbProvider.FetchAsync(ct);
    Console.WriteLine("目录来源: " + catalog.Source + " · 记录 " + catalog.Components.Length + " 条 · 获取于 " + catalog.FetchedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
    if (arguments.Length > 0)
    {
        await WriteCacheAsync(arguments[0], catalog, ct);
        Console.WriteLine("目录缓存已写入 → " + arguments[0]);
    }
    return 0;
}

static async Task<int> HomebrewIndexAsync(string[] arguments, CancellationToken ct)
{
    if (arguments.Length is < 1 or > 2) throw new InvalidDataException("用法: homebrew-index <indexUrl> [cacheFile]");
    using var http = new HttpClient(new System.Net.Http.SocketsHttpHandler { AllowAutoRedirect = true, ConnectTimeout = TimeSpan.FromSeconds(10) });
    var apps = await HomebrewStore.FetchIndexAsync(http, arguments[0], ct);
    foreach (var app in apps) Console.WriteLine(app.Name + "\t" + app.Author + "\t" + app.Version + "\t" + app.DownloadUrl);
    Console.WriteLine(apps.Length + " 项免费开源内容");
    if (arguments.Length > 1) await WriteCacheAsync(arguments[1], apps, ct);
    return 0;
}

static async Task WriteCacheAsync<T>(string path, T value, CancellationToken ct)
{
    var target = Path.GetFullPath(path);
    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
    var temporary = target + "." + Guid.NewGuid().ToString("N") + ".partial";
    var created = false;
    try
    {
        await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
        {
            created = true;
            await JsonSerializer.SerializeAsync(output, value, Wire.Json, ct);
            ct.ThrowIfCancellationRequested(); output.Flush(true);
        }
        ct.ThrowIfCancellationRequested(); File.Move(temporary, target, true);
    }
    finally { if (created && File.Exists(temporary)) File.Delete(temporary); }
}

static int StreamArgs(string[] arguments)
{
    string mode = StreamingOptions.UsbMode, address = "", title = "";
    for (var index = 0; index < arguments.Length; index++)
    {
        switch (arguments[index])
        {
            case "--mode": if (++index >= arguments.Length) throw new InvalidDataException("--mode 需要值"); mode = arguments[index]; break;
            case "--address": if (++index >= arguments.Length) throw new InvalidDataException("--address 需要值"); address = arguments[index]; break;
            case "--title": if (++index >= arguments.Length) throw new InvalidDataException("--title 需要值"); title = arguments[index]; break;
            default: throw new InvalidDataException("未知参数：" + arguments[index]);
        }
    }
    var normalized = mode.Equals("bridge", StringComparison.OrdinalIgnoreCase) ? StreamingOptions.BridgeMode :
        mode.Equals("usb", StringComparison.OrdinalIgnoreCase) ? StreamingOptions.UsbMode : mode;
    var lines = Streaming.BuildArguments(new(normalized, address.Length == 0 ? null : address, title));
    var commandLine = string.Join(' ', lines.Select(QuoteShellArgument));
    Console.WriteLine(commandLine);
    Console.Error.WriteLine((OperatingSystem.IsWindows() ? "PowerShell 命令示例: " : "终端命令示例: ") + "SysDVR-Client " + commandLine);
    return 0;
}

static string QuoteShellArgument(string value)
{
    if (System.Text.RegularExpressions.Regex.IsMatch(value, @"\A[a-zA-Z0-9_./:-]+\z")) return value;
    // The CLI prints a command for the host shell; preserve spaces, apostrophes
    // and metacharacters as one literal argument when the example is copied.
    return "'" + value.Replace("'", OperatingSystem.IsWindows() ? "''" : "'\"'\"'") + "'";
}
