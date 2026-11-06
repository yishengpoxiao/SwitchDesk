using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using SwitchDesk.Core;

namespace SwitchDesk.App;

/// <summary>局域网传输（FTPD）、Homebrew 商店、金手指管理、电脑库维护与快照对比导出。</summary>
public partial class MainWindow
{
    private FtpClient? ftpClient;

    private string FtpPortText => FtpPort.Text.Trim();

    private async Task FtpConnectAsync()
    {
        if (ftpClient is { IsConnected: true }) throw new IOException("已连接到局域网主机，请先断开");
        var options = new FtpOptions(FtpHost.Text.Trim(), int.TryParse(FtpPortText, out var port) ? port : 0, FtpUser.Text.Trim(), FtpPassword.Password);
        await RunJob("连接局域网 FTP", async (_, ct) =>
        {
            var client = new FtpClient();
            try
            {
                await client.ConnectAsync(options, ct);
                SaveSettings(settings with { FtpHost = options.Host, FtpPort = options.Port, FtpUser = options.User, FtpPassword = options.Password });
            }
            catch { await client.DisposeAsync(); throw; }
            var previous = ftpClient; ftpClient = client;
            if (previous != null) await previous.DisposeAsync();
            await Dispatcher.InvokeAsync(() =>
            {
                FtpStatus.Text = "已连接 · " + options.Host + ":" + options.Port;
                FtpPath.Text = "/";
                CommandManager.InvalidateRequerySuggested();
            });
            await OpenFtpDirectoryAsync("/", ct);
            return true;
        });
        vm.Notify("已连接局域网主机 · FTP 传输可用", "");
    }

    private async Task FtpDisconnectAsync()
    {
        var client = ftpClient;
        await RunJob("断开局域网连接", async (_, ct) =>
        {
            ftpClient = null; await Dispatcher.InvokeAsync(() => { FtpStatus.Text = "未连接"; FtpGrid.ItemsSource = null; FtpBreadcrumbs.Children.Clear(); CommandManager.InvalidateRequerySuggested(); });
            if (client != null)
            {
                try { await client.DisconnectAsync(ct); }
                finally { await client.DisposeAsync(); }
            }
            return true;
        });
    }

    private async Task OpenFtpDirectoryAsync(string path, CancellationToken ct)
    {
        path = PathSafety.Remote(path);
        var client = ftpClient ?? throw new IOException("请先连接局域网主机");
        var entries = await client.ListAsync(path, ct);
        await Dispatcher.InvokeAsync(() =>
        {
            FtpPath.Text = path;
            UpdateFtpBreadcrumbs(path);
            FtpGrid.ItemsSource = entries.OrderByDescending(x => x.IsDirectory).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        });
    }

    private void UpdateFtpBreadcrumbs(string path)
    {
        FtpBreadcrumbs.Children.Clear();
        void Add(string text, string target)
        {
            var button = new System.Windows.Controls.Button { Content = text, Style = (Style)FindResource("Quiet"), Padding = new(8, 4, 8, 4), MinHeight = 30, ToolTip = target };
            button.Click += async (_, _) => { FtpPath.Text = target; await FtpNavigateAsync(); };
            FtpBreadcrumbs.Children.Add(button);
        }
        Add("局域网", "/");
        var current = "";
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries)) { current += "/" + part; Add("› " + part, current); }
    }

    private Task FtpNavigateAsync()
    {
        var path = FtpPath.Text; // UI 线程捕获
        return Guard(async () => await RunJob("打开局域网目录", async (_, ct) => { await OpenFtpDirectoryAsync(path, ct); return true; }));
    }
    private async void OnFtpPathKeyDown(object sender, System.Windows.Input.KeyEventArgs e) { if (e.Key == System.Windows.Input.Key.Enter) await FtpNavigateAsync(); }
    private async void OnFtpParent(object sender, RoutedEventArgs e)
    { var path = FtpPath.Text.TrimEnd('/'); var cut = path.LastIndexOf('/'); FtpPath.Text = cut <= 0 ? "/" : path[..cut]; await FtpNavigateAsync(); }
    private async void OnFtpOpen(object sender, System.Windows.Input.MouseButtonEventArgs e)
    { if (FtpGrid.SelectedItem is FtpEntry { IsDirectory: true } entry) { FtpPath.Text = entry.Path; await FtpNavigateAsync(); } }

    private FtpEntry SelectedFtpEntry() => FtpGrid.SelectedItem as FtpEntry ?? throw new IOException("请选择局域网文件");

    private async Task FtpUploadAsync()
    {
        var client = ftpClient ?? throw new IOException("请先连接局域网主机");
        var pick = new OpenFileDialog { Multiselect = true }; if (pick.ShowDialog(this) != true) return;
        var directory = PathSafety.Remote(FtpPath.Text);
        foreach (var path in pick.FileNames) PathSafety.DemandWritable(directory.TrimEnd('/') + "/" + Path.GetFileName(path));
        var existing = (FtpGrid.ItemsSource as IEnumerable<FtpEntry> ?? []).Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (pick.FileNames.Any(path => existing.Contains(Path.GetFileName(path)))) throw new IOException("目标目录存在同名文件，请先重命名或更换目录");
        await RunJob("上传到局域网 · " + pick.FileNames.Length + " 项", async (job, ct) =>
        {
            for (var index = 0; index < pick.FileNames.Length; index++)
                await client.UploadAsync(pick.FileNames[index], directory.TrimEnd('/') + "/" + Path.GetFileName(pick.FileNames[index]), Progress(job), ct);
            await OpenFtpDirectoryAsync(directory, ct); return true;
        });
        vm.Notify("上传完成", directory);
    }

    private async Task FtpUploadFolderAsync()
    {
        var client = ftpClient ?? throw new IOException("请先连接局域网主机");
        var pick = new OpenFolderDialog { Title = "选择要上传的文件夹" }; if (pick.ShowDialog(this) != true) return;
        var directory = PathSafety.Remote(FtpPath.Text);
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(pick.FolderName));
        if (string.IsNullOrWhiteSpace(name)) throw new IOException("无法读取文件夹名称");
        var target = PathSafety.Remote(directory.TrimEnd('/') + "/" + PathSafety.Relative(name));
        PathSafety.DemandWritable(target);
        await RunJob("上传文件夹 · " + name, async (job, ct) =>
        {
            await client.UploadTreeAsync(pick.FolderName, target, Progress(job), ct);
            await OpenFtpDirectoryAsync(directory, ct); return true;
        });
        vm.Notify("文件夹已上传", target);
    }

    private async Task FtpDownloadAsync()
    {
        var client = ftpClient ?? throw new IOException("请先连接局域网主机");
        var entry = SelectedFtpEntry();
        if (entry.IsDirectory) throw new IOException("局域网目录请逐个文件下载");
        var pick = new OpenFolderDialog { Title = "选择电脑目标目录" }; if (pick.ShowDialog(this) != true) return;
        var destination = PathSafety.Child(pick.FolderName, entry.Name);
        await RunJob("下载 · " + entry.Name, async (job, ct) => { await client.DownloadAsync(entry.Path, destination, Progress(job), ct); return true; });
        vm.Notify("下载完成", pick.FolderName);
    }

    private async Task FtpMkdirAsync()
    {
        var client = ftpClient ?? throw new IOException("请先连接局域网主机");
        var name = Dialogs.Text(this, "新建文件夹", "文件夹名称"); if (name == null) return;
        var path = PathSafety.Remote(FtpPath.Text.TrimEnd('/') + "/" + SingleName(name));
        await RunJob("新建局域网目录", async (_, ct) => { await client.MkdirAsync(path, ct); await OpenFtpDirectoryAsync(path, ct); return true; });
    }

    private async Task FtpRenameAsync()
    {
        var client = ftpClient ?? throw new IOException("请先连接局域网主机");
        var entry = SelectedFtpEntry();
        var name = Dialogs.Text(this, "重命名", "新名称", entry.Name); if (name == null || name == entry.Name) return;
        var target = PathSafety.Remote(entry.Path[..(entry.Path.LastIndexOf('/') + 1)] + SingleName(name));
        var currentDir = PathSafety.Remote(FtpPath.Text); // UI 线程捕获
        await RunJob("重命名局域网文件", async (_, ct) => { await client.RenameAsync(entry.Path, target, ct); await OpenFtpDirectoryAsync(currentDir, ct); return true; });
    }

    /// <summary>新建/重命名只接受单段名称，避免输入路径分隔符或换行注入。</summary>
    private static string SingleName(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0 || trimmed is "." or ".." || trimmed.Contains('/') || trimmed.Contains('\\') ||
            trimmed.Contains('\r') || trimmed.Contains('\n')) throw new InvalidDataException("名称不能为空、包含路径分隔符或换行");
        return PathSafety.Relative(trimmed);
    }

    private async Task FtpDeleteAsync()
    {
        var client = ftpClient ?? throw new IOException("请先连接局域网主机");
        var entry = SelectedFtpEntry();
        if (!Dialogs.Confirm(this, "删除局域网文件", "将删除 " + entry.Path + (entry.IsDirectory ? "（目录需已清空）" : ""), "删除")) return;
        var currentDir = PathSafety.Remote(FtpPath.Text); // UI 线程捕获
        await RunJob("删除局域网文件", async (_, ct) =>
        {
            if (entry.IsDirectory) await client.RemoveDirectoryAsync(entry.Path, ct); else await client.DeleteAsync(entry.Path, ct);
            await OpenFtpDirectoryAsync(currentDir, ct); return true;
        });
    }

    private async Task HomebrewRefreshAsync()
    {
        var index = settings.HomebrewIndex;
        if (index.Length == 0)
        {
            var input = Dialogs.Text(this, "Homebrew 索引", "JSON 索引地址（社区商店提供）", "https://");
            if (input == null || input.Trim().Length == 0) return;
            index = input.Trim();
            var updated = settings with { HomebrewIndex = index };
            settingsStore.Save(updated); settings = updated;
        }
        var apps = await RunJob("获取 Homebrew 索引", async (_, ct) =>
        {
            using var http = new HttpClient(new System.Net.Http.SocketsHttpHandler { AllowAutoRedirect = true, ConnectTimeout = TimeSpan.FromSeconds(10) });
            return await HomebrewStore.FetchIndexAsync(http, index, ct);
        });
        await Dispatcher.InvokeAsync(() =>
        {
            vm.HomebrewApps.Clear();
            foreach (var app in apps) vm.HomebrewApps.Add(new HomebrewChoice(app));
            vm.SelectedHomebrew = vm.HomebrewApps.FirstOrDefault();
        });
        vm.Notify($"索引已加载 · {apps.Length} 项");
    }

    private async Task HomebrewInstallAsync()
    {
        var choice = vm.SelectedHomebrew ?? throw new IOException("请选择要安装的 Homebrew 应用");
        var (peer, _) = RequireDevice("files");
        var path = await RunJob("安装 " + choice.Value.Name, async (job, ct) =>
        {
            using var http = new HttpClient(new System.Net.Http.SocketsHttpHandler { AllowAutoRedirect = true, ConnectTimeout = TimeSpan.FromSeconds(10) });
            var payload = await HomebrewStore.DownloadAsync(http, choice.Value, Progress(job), ct);
            return await HomebrewStore.InstallAsync(peer, choice.Value, payload, null, Progress(job), ct);
        });
        vm.Notify("已安装到 SD 卡：" + path, "");
    }

    private async Task RefreshCheatsAsync(string titleId, CancellationToken ct)
    {
        var (peer, _) = RequireDevice("files");
        var entries = await CheatManager.ListAsync(peer, titleId, ct);
        await Dispatcher.InvokeAsync(() =>
        {
            // 防止竞争：用户切了游戏后，旧结果不能填入新游戏的详情
            if (vm.Library.Selected?.TitleId?.Equals(titleId, StringComparison.OrdinalIgnoreCase) != true) { vm.Cheats.Clear(); return; }
            vm.Cheats.Clear(); foreach (var entry in entries) vm.Cheats.Add(entry);
        });
    }

    /// <summary>手动/切到标签时刷新当前游戏的金手指列表（只读，不要求 cheats 写能力）。</summary>
    private async Task CheatRefreshAsync()
    {
        var titleId = vm.Library.Selected?.TitleId ?? throw new IOException("请选择已读取的主机游戏");
        await RunJob("读取金手指", async (_, ct) => { await RefreshCheatsAsync(titleId, ct); return true; });
    }

    private async Task CheatImportAsync()
    {
        var titleId = vm.Library.Selected?.TitleId ?? throw new IOException("请选择已读取的主机游戏");
        var pick = new OpenFolderDialog { Title = "选择包含 <构建ID>.txt 金手指的文件夹（兼容 .cheat）" }; if (pick.ShowDialog(this) != true) return;
        var (_, identity) = RequireDevice("cheats.text.v1");
        var installed = await RunJob("导入金手指", async (job, ct) =>
        {
            var (peer, current) = RequireDevice("cheats.text.v1");
            if (current.DeviceId != identity.DeviceId || current.Environment != identity.Environment) throw new IOException("主机环境已变化");
            var count = await CheatManager.InstallFolderAsync(peer, titleId, pick.FolderName, Progress(job), ct);
            if (count == 0) throw new IOException("文件夹内没有以 16 位构建 ID 命名的 .txt 或 .cheat 金手指文件");
            await RefreshCheatsAsync(titleId, ct); return count;
        });
        vm.Notify($"已导入 {installed} 个金手指");
    }

    private async Task CheatToggleAsync()
    {
        var entry = CheatsList.SelectedItem as CheatEntry ?? throw new IOException("请选择金手指文件");
        var titleId = vm.Library.Selected?.TitleId ?? throw new IOException("请选择已读取的主机游戏");
        var (_, identity) = RequireDevice("files");
        await RunJob((entry.Enabled ? "停用" : "启用") + "金手指", async (_, ct) =>
        {
            var (peer, current) = RequireDevice("files");
            if (current.DeviceId != identity.DeviceId || current.Environment != identity.Environment) throw new IOException("主机环境已变化");
            await CheatManager.SetEnabledAsync(peer, entry.Path, !entry.Enabled, ct);
            await RefreshCheatsAsync(titleId, ct); return true;
        });
        vm.Notify("金手指已" + (entry.Enabled ? "停用（.off）" : "启用"));
    }

    private async Task CheatDeleteAsync()
    {
        var entry = CheatsList.SelectedItem as CheatEntry ?? throw new IOException("请选择金手指文件");
        var titleId = vm.Library.Selected?.TitleId ?? throw new IOException("请选择已读取的主机游戏");
        var (_, identity) = RequireDevice("files");
        if (!Dialogs.Confirm(this, "删除金手指", "将删除 " + entry.Path, "删除")) return;
        await RunJob("删除金手指", async (_, ct) =>
        {
            var (peer, current) = RequireDevice("files");
            if (current.DeviceId != identity.DeviceId || current.Environment != identity.Environment) throw new IOException("主机环境已变化");
            await CheatManager.DeleteAsync(peer, entry.Path, ct);
            await RefreshCheatsAsync(titleId, ct); return true;
        });
        vm.Notify("金手指已删除");
    }

    private async Task CleanMissingAsync()
    {
        var removed = await RunJob("清理缺失索引", async (_, ct) => library.RemoveMissing(ct));
        await Dispatcher.InvokeAsync(LoadLocalLists);
        vm.Notify($"已清理 {removed} 条缺失索引（文件不受影响）");
    }

    private async Task VerifyAllLibraryAsync()
    {
        var report = await RunJob("批量校验电脑游戏库", async (job, ct) => await library.VerifyAllAsync(Progress(job), ct));
        await Dispatcher.InvokeAsync(LoadLocalLists);
        vm.Notify($"批量校验完成 · 通过 {report.Verified} · 不匹配 {report.Mismatched} · 跳过 {report.Skipped}");
    }

    private async Task FindDupesAsync()
    {
        var groups = await RunJob("查找重复文件", async (_, ct) => await library.FindDuplicatesAsync(ct));
        if (groups.Count == 0) { vm.Notify("没有发现重复的游戏文件"); return; }
        Dialogs.DuplicateReport(this, groups);
    }

    private async Task CompareSnapshotAsync()
    {
        var current = ActiveSnapshot ?? throw new IOException("请选择历史版本");
        // 存档页使用独立备份库；对比候选必须与当前详情上下文一致。
        var candidates = ActiveSaves.Backups.Where(x => x.Path != current.Path && SnapshotStore.SameTarget(x.Manifest, current.Manifest)).ToArray();
        if (candidates.Length == 0) throw new IOException("没有可与所选版本对比的其他快照");
        var older = Dialogs.SnapshotPick(this, "选择要对比的历史版本", candidates, current);
        if (older == null) return;
        var diff = await RunJob("对比存档快照", async (_, ct) => await SnapshotTools.CompareAsync(older.Path, current.Path, ct));
        Dialogs.DiffReport(this, older, diff);
    }

    private async Task ExportSnapshotAsync()
    {
        var snapshot = ActiveSnapshot ?? throw new IOException("请选择历史版本");
        var pick = new OpenFolderDialog { Title = "选择导出目录", InitialDirectory = settings.ExportDirectory }; if (pick.ShowDialog(this) != true) return;
        var destination = await RunJob("导出存档快照", async (_, ct) => await SnapshotTools.ExportSnapshotAsync(snapshot.Path, pick.FolderName, ct, expectedManifest: snapshot.Manifest));
        vm.Notify("快照已导出", destination);
    }
}
