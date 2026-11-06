using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SwitchDesk.Core;

namespace SwitchDesk.App;
public partial class MainWindow
{
    private async Task RenderSmokeAsync()
    {
        var args = Environment.GetCommandLineArgs(); var output = Path.GetFullPath(args[Array.IndexOf(args, "--smoke-test") + 1]); Directory.CreateDirectory(output);
        var report = new List<string>();
        var bindingErrors = new BindingErrorListener(); PresentationTraceSources.DataBindingSource.Listeners.Add(bindingErrors); PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException("UI check failed: " + name); report.Add("PASS " + name); }
        async Task Idle() { await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); UpdateLayout(); }
        async Task Render(string name, double scale = 1)
        {
            await Idle(); var visual = (FrameworkElement)Content;
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth * scale), (int)Math.Ceiling(visual.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32); bitmap.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(Path.Combine(output, name + ".png")); encoder.Save(file);
        }
        ThemeManager.Apply("Light"); await Render("00-first-connect-light");
        vm.Fixture = true;
        device = new("UI-FIXTURE", "Switch OLED · 测试", "测试 19.0.1", "测试 1.8.0", "0.2.0-test", "emuMMC · 测试环境", "USB 3.0 · 测试", [], true); UpdateDevice(device); synced = DateTimeOffset.Now; vm.Connection = "离线记录 · 界面测试";
        string[] names = ["远山来信", "星海漫游", "暮色回廊", "夏日岛屿", "纸间旅程", "环形竞速", "深林物语", "雨后咖啡"];
        titles = Enumerable.Range(0, 500).Select(i => new GameTitle($"01000000{i:X8}", $"{i + 1:000}  {names[i % names.Length]}", "SwitchDesk 原创界面夹具", "1.2.0", i % 19 == 0 ? "已归档" : "已安装", i % 19 == 0 ? [] : [new($"01000000{i:X8}", "Base", 0, "SdCard", 5L << 30), new($"01000000{i + 2048:X8}", "Update", 65536, "SdCard", 1L << 30), new($"01000000{i + 4096:X8}", "DLC", 0, "BuiltInUser", 64L << 20)])).ToArray();
        saves = [new("fixture", titles[1].TitleId, titles[1].Name, "USER-A", "一生破晓 · 测试用户", "Account", 2L << 20, device.Environment), new("shared", titles[1].TitleId, titles[1].Name, "", "共享存档", "Device", 1024, device.Environment)];
        vm.Storages = [new("SdCard", "SD 卡", 256L << 30, 120L << 30), new("BuiltInUser", "当前 emuMMC 用户分区", 29L << 30, 18L << 30)];
        var locals = new[] { new LibraryItem(Path.Combine(output, "test.nsp"), titles[1].Name, titles[1].TitleId, "Base", "0", 5L << 30, null, "已识别 · 测试"), new LibraryItem(Path.Combine(output, "unknown.xcz"), "未识别文件.xcz", null, "", "", 2L << 30, null, "待识别") };
        vm.Library.SaveTitles = saves.Select(x => x.TitleId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        vm.Library.Merge(titles, locals); foreach (var card in vm.Library.All.Where(x => !x.Unknown)) card.Icon = FixtureIcon(Array.IndexOf(titles, card.Console));
        settings = settings with { BackupDirectory = Path.Combine(output, "fixture-backups") };
        Directory.CreateDirectory(settings.BackupDirectory);
        for (var i = 0; i < 5; i++)
        {
            byte[] bytes = [(byte)i, 2, 3, 4]; SnapshotFile[] files = [new("main.dat", bytes.Length, Fingerprints.Of(bytes))];
            var hash = Fingerprints.Of(JsonSerializer.SerializeToUtf8Bytes(new { files, directories = Array.Empty<string>() }, Wire.Json));
            var manifest = new SaveManifest(1, device.DeviceId, device.Environment, titles[1].TitleId, titles[1].Name,
                "USER-A", "一生破晓 · 测试用户", "Account", DateTimeOffset.Now.AddDays(-i), files, hash, []);
            using var zip = ZipFile.Open(Path.Combine(settings.BackupDirectory, $"fixture-{i}.zip"), ZipArchiveMode.Create);
            using (var entry = zip.CreateEntry("manifest.json").Open()) JsonSerializer.Serialize(entry, manifest, Wire.Json);
            using (var entry = zip.CreateEntry("data/main.dat").Open()) entry.Write(bytes);
        }
        vm.Saves.SetData(saves, new SnapshotStore(settings.BackupDirectory).List().ToArray());
        vm.Library.Selected = vm.Library.All.First(x => x.TitleId == titles[1].TitleId); UpdateDetails(); UpdateSyncText();
        await Idle(); Check(vm.Library.All.Count == 501, "500 games plus unknown file; known local content merged");
        Check(Descendants<Button>(Gallery).Count() < 70, "recycling gallery realizes fewer than 70 cards");
        var watch = Stopwatch.StartNew(); vm.Library.Search = "星海"; await Idle(); watch.Stop(); Check(vm.Library.Filtered.Count == 63 && watch.ElapsedMilliseconds < 1000, $"500-item search {watch.ElapsedMilliseconds} ms"); vm.Library.Search = "";
        await Render("01-library-light"); ThemeManager.Apply("Dark"); await Render("02-library-dark");
        Navigate("saves"); await Render("03-saves-dark");
        Check(vm.SaveLibrary.Games.Single().BackupCount == 5, "save directory discovers all five fixture backups independently of game packages");
        vm.SaveLibrary.OpenGameCommand.Execute(vm.SaveLibrary.Games[0]); await Render("03-save-timeline-dark");
        Check(vm.SaveLibrary.Detail.History.Count == 5, "save timeline filters by full source identity");
        var selectedKey = vm.Library.Selected!.Key; var search = vm.Library.Search; await DisconnectAsync(); Check(vm.Library.Selected.Key == selectedKey && vm.Library.Search == search && vm.Library.All.Count == 501, "disconnect preserves inventory, filters and selection");
        Navigate("files"); FilesGrid.ItemsSource = new[] { new RemoteEntry("switch", "/switch", true, 0), new RemoteEntry("config", "/config", true, 0), new RemoteEntry("测试文件.bin", "/测试文件.bin", false, 128L << 20) }; await Render("04-files-dark");
        Navigate("network"); FtpHost.Text = "192.168.1.30"; FtpPort.Text = "21"; FtpStatus.Text = "未连接 · 界面夹具";
        FtpGrid.ItemsSource = new[] { new FtpEntry("switch", "/switch", true, 0), new FtpEntry("工具示例.nro", "/switch/工具示例.nro", false, 8L << 20) };
        vm.HomebrewApps.Clear(); vm.HomebrewApps.Add(new HomebrewChoice(new("工具示例", "dev", "1.0", "界面夹具 · 免费开源", "https://example.com/app.nro", "app.nro")));
        await Render("04b-network-dark");
        Navigate("console"); await Render("05-console-dark"); Navigate("settings"); await Render("06-settings-dark");
        vm.RecordLog("界面测试 · 连接断开，保留缓存；未进行实机连接。", "界面测试 · 连接断开，保留缓存；未进行实机连接。"); Navigate("diagnostics"); await Render("07-diagnostics-dark");
        Navigate("library"); vm.Tasks.Add(new() { Name = "备份 · 星海漫游（夹具）", State = "完成", Indeterminate = false, Percent = 100, CanCancel = false });
        var commit = new Job { Name = "恢复 · 不可取消状态（夹具）" }; commit.Report(new(0, 0, "正在写回存档，请保持连接")); vm.Tasks.Add(commit); await Idle(); Check(!QueuePanel.IsVisible, "task history stays out of normal pages"); Navigate("diagnostics"); DiagnosticTabs.SelectedIndex = 0;
        Check(!commit.CanCancel && commit.Indeterminate, "save commit indeterminate and non-cancellable"); await Render("08-tasks-dark");
        Check(QueuePanel.IsVisible, "tasks are available inside diagnostics");
        Check(JobActionsColumn.Visibility == Visibility.Collapsed, "no empty action column for completed or locked tasks");
        var completedTask = vm.Tasks.Items.First(x => x.State == "完成");
        Check(!completedTask.CanCancel && completedTask.DiagnosticDetail.Length == 0, "completed task hides inapplicable actions and duplicate result");
        commit.State = "完成"; commit.Indeterminate = false; commit.Percent = 100; Navigate("library");
        var failure = new Job { Name = "读取主机资料 · 测试", State = "需要处理", Detail = "USB 写入中断 · 测试", CanCancel = false };
        vm.Tasks.Add(failure);
        vm.RecordLog("12:00:01  失败：读取主机资料 · USB 写入中断 · 测试", "失败：读取主机资料 · USB 写入中断 · 测试");
        vm.RecordLog("12:00:02  USB 写入中断 · 测试", "USB 写入中断 · 测试");
        Check(!vm.ConnectionEvents.Any(x => x.Contains("USB 写入中断")), "task failure is not repeated as a connection event");
        vm.Notify(failure.Detail); Navigate("diagnostics"); ThemeManager.Apply("Light"); await Idle();
        Check(!NoticeBox.IsVisible, "diagnostics does not repeat the task error as a banner");
        await Render("diagnostics-tasks-light"); vm.Notify(""); Navigate("library");
        InstallOverlay.Visibility = Visibility.Visible; InstallContext.Text = "界面测试清单 · Switch OLED / emuMMC · 不执行真实安装";
        installEntries.Add(new("原创建模测试.nsp") { State = "就绪" }); installEntries.Add(new("测试更新.nsz") { State = "空间不足（测试）" }); InstallSummary.Text = "预计需要 8 GiB · 可用 6 GiB"; InstallError.Text = "目标空间不足，请更换存储位置"; StartInstall.Content = "开始安装 2 项"; StartInstall.IsEnabled = false;
        await Render("09-install-review-dark"); InstallOverlay.Visibility = Visibility.Collapsed;
        InstallOverlay.Visibility = Visibility.Visible;
        var hashWarning = new InstallWarning("NcaHashMismatch", "fixture.nca", new string('1', 32), new string('2', 64));
        var signatureWarning = new InstallWarning("NcaHeaderSignatureMismatch", "fixture.nca", "", "")
        { HeaderSha256 = new string('3', 64), VerificationResult = "00000000000081F9" };
        installEntries[0].Complete(new([hashWarning, signatureWarning])); InstallGrid.SelectedItem = installEntries[0];
        await Idle();
        Check(InstallWarningsPanel.IsVisible && InstallWarningText.Text.Contains(hashWarning.ActualSha256, StringComparison.Ordinal), "completed installation keeps full copyable checksum warning");
        Check(InstallWarningText.Text.Contains(signatureWarning.HeaderSha256!, StringComparison.Ordinal) && InstallWarningText.Text.Contains(signatureWarning.VerificationResult!, StringComparison.Ordinal), "completed installation keeps full copyable signature warning");
        Check(installEntries[0].IsCompleted && !installEntries[0].IsPending, "completed installation with warnings cannot be retried as pending");
        await Render("install-warning-dark"); InstallOverlay.Visibility = Visibility.Collapsed;
        var dbiFixture = Path.Combine(output, "dbi-fixture.nsp"); await File.WriteAllBytesAsync(dbiFixture, [1, 2, 3]);
        var dbiDialog = new DbiInstallWindow("", Path.Combine(settingsStore.Root, "Tools", "DBI"), _ => { })
        { Owner = this, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -16000, Top = -16000 };
        dbiDialog.Show(); dbiDialog.AddFiles([dbiFixture]);
        ((TextBlock)dbiDialog.FindName("StatusText")).Text = "界面夹具 · 未下载、运行后端或连接真实主机。";
        foreach (var theme in new[] { "Light", "Dark" })
        {
            ThemeManager.Apply(theme); await Idle(); dbiDialog.UpdateLayout();
            var visual = (FrameworkElement)dbiDialog.Content;
            var screenshotBounds = new Rect(visual.RenderSize);
            var windowBounds = new Rect(dbiDialog.RenderSize);
            var clientBounds = visual.TransformToAncestor(dbiDialog).TransformBounds(screenshotBounds);
            Check(windowBounds.Contains(clientBounds), $"{theme} DBI screenshot content fits the actual window");
            foreach (var button in Descendants<Button>(visual).Where(button => button.IsVisible))
            {
                var bounds = button.TransformToAncestor(visual).TransformBounds(new Rect(button.RenderSize));
                var windowButtonBounds = button.TransformToAncestor(dbiDialog).TransformBounds(new Rect(button.RenderSize));
                Check(button.ActualWidth > 0 && button.ActualHeight > 0 && screenshotBounds.Contains(bounds) && windowBounds.Contains(windowButtonBounds),
                    $"{theme} DBI action {button.Content} stays fully inside the window and screenshot");
            }
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(screenshotBounds.Width), (int)Math.Ceiling(screenshotBounds.Height), 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var image = File.Create(Path.Combine(output, $"dbi-install-{theme}.png")); encoder.Save(image);
            Check(((Button)dbiDialog.FindName("StartBackend")).IsEnabled && !dbiDialog.SessionActive, $"{theme} DBI review is ready without starting a backend");
        }
        dbiDialog.Close();
        Check(vm.Commands["DbiInstall"].CanExecute(null) && HeaderDbi.IsVisible, "DBI entry stays available while management is offline");
        vm.StreamGuide = true; StreamStatusText.Text = "投屏页面布局检查 · 非真实游戏画面"; await Render("10-stream-guide-dark"); vm.StreamGuide = false;
        DetailsTabs.SelectedIndex = 0;
        OpenCrossSaveHome(); await Render("cross-save-home-dark");
        Check(vm.CrossSave.Games.Count == 6, "cross-save navigation exposes six games without a library selection");
        vm.CrossSave.SelectedGame = vm.CrossSave.Games.First(g => g.Definition.AdapterId == "vampire-survivors");
        crossContextUpdating = true;
        try
        {
            vm.CrossSave.TargetGames = [new("010089A0197E4000", "吸血鬼幸存者（界面夹具）")];
            vm.CrossSave.SelectedTargetGame = vm.CrossSave.TargetGames[0];
            vm.CrossSave.Users = [new("fixture-cross", "一生破晓 · 测试用户", "Account",
                new("fixture-cross", "010089A0197E4000", "吸血鬼幸存者（界面夹具）", "USER-A", "一生破晓 · 测试用户", "Account", 1024, device.Environment))];
            vm.CrossSave.SelectedUser = vm.CrossSave.Users[0];
        }
        finally { crossContextUpdating = false; }
        vm.CrossSave.Candidates = [new("vampire-survivors", "fixture-only", "一生破晓 · Windows 测试用户", "1", "界面夹具")]; vm.CrossSave.SelectedPc = vm.CrossSave.Candidates[0];
        vm.CrossSave.SwitchFiles = ["user1.dat"]; vm.CrossSave.SelectedSwitchFile = "user1.dat";
        vm.CrossSave.FileStatus = "已发现 1 个进度文件 · 界面夹具";
        vm.CrossSave.PcSummary = "游戏记录的保存时间  2026-10-23 12:00\n角色与场景进度（测试数据）"; vm.CrossSave.SwitchSummary = "最近读取时间（保存时间不可用）\n2026-10-23 12:01 · 界面夹具";
        vm.CrossSave.Comparison = "双方均有变化，请比较后手动选择 · 测试场景"; vm.CrossSave.Gate = "此版本组合未进入迁移支持清单，双端备份可用";
        Navigate("library");
        foreach (var theme in new[] { "Light", "Dark" }) foreach (var width in new[] { 1050, 1280, 1920 })
        {
            ThemeManager.Apply(theme); Width = width; Height = 820; LayoutDetails(); await Idle();
            Check(DetailColumn.ActualWidth >= 300 && LibraryPage.ActualWidth >= 450, $"{theme} {width} DIP detail and library fit");
            Check(vm.Library.Rows[0].Cards.Sum(x => x.Width + 16) <= LibraryPage.ActualWidth + 1, $"{theme} {width} DIP cards stay in viewport");
            var exportBounds = ExportButton.TransformToAncestor(Root).TransformBounds(new Rect(ExportButton.RenderSize));
            Check(exportBounds.Bottom <= Root.ActualHeight && ExportButton.ActualWidth > 100, $"{theme} {width} DIP export action stays visible");
            await Render($"matrix-{theme}-{width}-100"); await Render($"matrix-{theme}-{width}-200", 2);
            Navigate("saves"); await Render($"saves-{theme}-{width}-100"); await Render($"saves-{theme}-{width}-200", 2);
            Navigate("cross-save"); await Render($"cross-save-{theme}-{width}-100"); await Render($"cross-save-{theme}-{width}-200", 2);
            var filePicker = Descendants<ComboBox>(CrossSavePage).Single(c => System.Windows.Automation.AutomationProperties.GetName(c) == "Switch 进度文件与槽位");
            filePicker.BringIntoView(); await Idle();
            Check(filePicker.SelectedValue?.ToString() == "user1.dat", $"{theme} {width} file selector displays a selected file");
            await Render($"cross-save-files-{theme}-{width}");
            Check(!Descendants<TextBlock>(CrossSavePage).Any(t => t.Text.Contains("SwitchDesk.Core.", StringComparison.Ordinal) || t.Text.Contains("PcSaveCandidate {", StringComparison.Ordinal)), $"{theme} {width} comparison selectors show human labels");
            Check(CrossSavePage.IsVisible && !vm.Commands["CrossSaveToSwitch"].CanExecute(null) && !vm.Commands["CrossSaveToPc"].CanExecute(null), $"{theme} {width} unadmitted migration stays disabled");
            Check(!QueuePanel.IsVisible, $"{theme} {width} cross-save page keeps task history in diagnostics");
            Navigate("resources");
            var resourceSample = new ResourceEntry("开源工具示例 · 界面夹具", ResourceKind.Homebrew, "https://fixture.invalid/tool.nro", "tool.nro")
            { Sha256 = Fingerprints.Of([1]), SourceName = "资源索引夹具", Description = "用于检查资源列表和手动安装入口的合成记录" };
            vm.Resources.SetResults([new(resourceSample), new(new PublicResourceArticle("游戏资源文章 · 界面夹具", "https://www.gamer520.com/123.html", "Gamer520", ResourceKind.Game))]);
            vm.Resources.Status = "界面夹具 · 网站文章与可下载文件分别显示";
            await Render($"resources-{theme}-{width}-100"); await Render($"resources-{theme}-{width}-200", 2);
            var resourceAction = Descendants<Button>(ResourcesPage).Single(b => b.Command == vm.Commands["ResourceDownload"]);
            Check(resourceAction.TransformToAncestor(Root).TransformBounds(new Rect(resourceAction.RenderSize)).Bottom <= Root.ActualHeight, $"{theme} {width} resource download action fits");
            vm.Resources.SetDownloads([new(resourceSample, Path.Combine(settingsStore.Root, "fixture-only-tool.nro"), 128, Fingerprints.Of([1]), true, DateTimeOffset.UtcNow)]);
            vm.Resources.SelectedTab = 1; await Render($"resource-downloads-{theme}-{width}");
            Check(!vm.Commands["ResourceInstall"].CanExecute(null), $"{theme} {width} missing resource file cannot install");
            Navigate("library");
            vm.StreamGuide = true; await Render($"usb-stream-{theme}-{width}-100"); await Render($"usb-stream-{theme}-{width}-200", 2);
            var streamActionBounds = StartStreamButton.TransformToAncestor(Root).TransformBounds(new Rect(StartStreamButton.RenderSize));
            Check(StreamOverlay.IsVisible && streamActionBounds.Bottom <= Root.ActualHeight && StreamVideoArea.ActualHeight >= 220, $"{theme} {width} USB viewport and actions fit");
            Check(!StreamVideoHost.IsVisible, $"{theme} {width} USB page does not fake a live video window"); vm.StreamGuide = false;
        }
        Width = 1050; Height = 640; Navigate("cross-save"); await Render("cross-save-minimum-height");
        var compareAction = Descendants<Button>(CrossSavePage).Single(b => b.Command == vm.Commands["CrossSaveRead"]);
        Check(compareAction.TransformToAncestor(Root).TransformBounds(new Rect(compareAction.RenderSize)).Bottom <= Root.ActualHeight, "minimum-height comparison action remains visible");
        Navigate("library"); vm.StreamGuide = true; await Render("usb-stream-minimum-height");
        Check(StartStreamButton.TransformToAncestor(Root).TransformBounds(new Rect(StartStreamButton.RenderSize)).Bottom <= Root.ActualHeight && StreamVideoArea.ActualHeight >= 220, "minimum-height USB stream controls remain visible");
        vm.StreamGuide = false; Height = 820;
        Width = 1050; Height = 640; Navigate("resources"); vm.Resources.SelectedTab = 0; await Render("resources-minimum-height");
        var resourceMinimumAction = Descendants<Button>(ResourcesPage).Single(b => b.Command == vm.Commands["ResourceDownload"]);
        Check(resourceMinimumAction.TransformToAncestor(Root).TransformBounds(new Rect(resourceMinimumAction.RenderSize)).Bottom <= Root.ActualHeight, "minimum-height resource action remains visible");
        Navigate("diagnostics");
        int binds = 0, inspections = 0;
        const string fixtureUsb = "fixture-switchdesk-interface";
        UsbDriverDiagnostic MissingDriver() { inspections++; return new("界面夹具 · 设备管理器错误 28，未操作真实接口。", fixtureUsb); }
        Task<string> ForbiddenBind(string id) { binds++; throw new InvalidOperationException("unexpected fixture binding"); }
        await RunDriverCheckAsync(MissingDriver, ForbiddenBind);
        Check(binds == 0 && inspections == 0 && !BindWinUsbButton.IsEnabled, "driver binding refuses to run without an explicit prior check");
        await RunDriverCheckAsync(MissingDriver, null);
        Check(DriverTab.IsSelected && BindWinUsbButton.IsEnabled && DiagnosticsText().Contains("错误 28", StringComparison.Ordinal),
            "eligible offline interface enables an explicit binding and retains copyable diagnostics");
        await Render("diagnostics-driver-minimum-height");
        foreach (var button in new[] { CheckDriverButton, BindWinUsbButton, ZadigButton, DeviceManagerButton })
            Check(button.IsEnabled && button.IsVisible && new Rect(Root.RenderSize).Contains(button.TransformToAncestor(Root).TransformBounds(new Rect(button.RenderSize))),
                "offline minimum-height driver action is reachable: " + button.Content);
        await RunDriverCheckAsync(MissingDriver, id => { binds++; Check(id == fixtureUsb && !Root.IsEnabled, "binding locks the reviewed target"); throw new OperationCanceledException(); });
        Check(binds == 1 && inspections == 2 && BindWinUsbButton.IsEnabled && DriverReport.Text.Contains("已取消", StringComparison.Ordinal), "cancelled binding rechecks without retrying");
        await RunDriverCheckAsync(() => new("其他接口已使用 WinUSB · 夹具", null, "different-interface"), _ => Task.FromResult("夹具请求结束"));
        Check(!BindWinUsbButton.IsEnabled && DriverReport.Text.Contains("尚未确认", StringComparison.Ordinal), "another healthy interface cannot verify this binding");
        await RunDriverCheckAsync(MissingDriver, null);
        await RunDriverCheckAsync(() => new("该接口已使用 WinUSB · 夹具", null, fixtureUsb), _ => Task.FromResult("夹具请求结束"));
        Check(!BindWinUsbButton.IsEnabled && DriverReport.Text.Contains("复查已确认", StringComparison.Ordinal) && Root.IsEnabled,
            "only observed WinUSB on the original interface verifies binding; healthy interfaces cannot be rebound");
        DiagnosticTabs.SelectedIndex = 0;
        Height = 820; Navigate("library");
        Width = 1280; DetailsTabs.SelectedIndex = 0; ThemeManager.Apply("Light"); await Render("preview-light"); ThemeManager.Apply("Dark"); await Render("preview-dark");
        vm.Cheats.Clear(); vm.Cheats.Add(new CheatEntry("0d784a915f5b4ac2.cheat", "/atmosphere/contents/0100000000010000/cheats/0d784a915f5b4ac2.cheat", "0d784a915f5b4ac2", true, 128, 2));
        vm.Cheats.Add(new CheatEntry("0d784a915f5b4ac2.cheat.off", "/atmosphere/contents/0100000000010000/cheats/0d784a915f5b4ac2.cheat.off", "0d784a915f5b4ac2", false, 96, 1));
        DetailsTabs.SelectedIndex = 3; await Idle(); await Render("cheats-tab-dark"); DetailsTabs.SelectedIndex = 0;
        Check(!Descendants<TextBlock>(Details).Any(x => x.Text.Contains("SaveChoice {", StringComparison.Ordinal)), "user display contains no internal object dump");
        await CheckUiPolishAsync(output, Check);
        Check(bindingErrors.Messages.Count == 0, "no WPF binding errors: " + string.Join(" | ", bindingErrors.Messages));
        report.Add("DPI images are WPF render-scale checks at 96/192 DPI, not an OS monitor DPI transition test."); report.Add("All art, records and tasks above are labelled UI fixtures. No Switch hardware validation.");
        File.WriteAllLines(Path.Combine(output, "smoke-result.txt"), report);
    }
    private sealed class BindingErrorListener : TraceListener
    {
        public List<string> Messages { get; } = [];
        public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) Messages.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
    private static ImageSource FixtureIcon(int index)
    {
        string[] palette = ["#4B675E", "#314F70", "#5E4461", "#BA8058", "#5D7272", "#BC6F4C", "#3D6859", "#8B6853"];
        var drawing = new DrawingGroup(); using (var dc = drawing.Open())
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette[Math.Abs(index) % palette.Length]));
            dc.DrawRectangle(brush, null, new Rect(0, 0, 256, 256)); dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(60, 255, 244, 205)), null, new Point(184, 66), 46, 46);
            var path = Geometry.Parse("M0,220 L70,85 135,190 182,125 256,230 256,256 0,256Z"); dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), null, path);
            dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(100, 255, 255, 255)), 2), new Point(22, 230), new Point(234, 230));
        }
        drawing.Freeze(); return new DrawingImage(drawing);
    }
}
