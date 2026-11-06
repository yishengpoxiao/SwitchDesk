using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SwitchDesk.Core;

namespace SwitchDesk.App;
public partial class MainWindow : Window
{
    private readonly ShellViewModel vm = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim jobsGate = new(1);
    private readonly SettingsStore settingsStore;
    private readonly LibraryDatabase library;
    private readonly DeviceCache cache;
    private readonly Phase2State phase2;
    private readonly SaveSyncStore syncStore;
    private AppSettings settings;
    private DeviceClient? client;
    private DeviceInfo? device;
    private GameTitle[] titles = [];
    private SaveInfo[] saves = [];
    private int activeJobs;
    private bool streaming, initialized, batchRunning, reviewing;
    private DateTimeOffset? synced;
    private string lastConnectionMessage = "";
    private readonly object logLock = new();
    private readonly HashSet<string> loadingIcons = [];
    private readonly ObservableCollection<InstallEntry> installEntries = [];
    private readonly ObservableCollection<UploadEntry> uploadEntries = [];
    private DeviceInfo? reviewedDevice;
    private string? cheatTitle;
    private string uploadDirectory = "/";
    private Point dragOrigin;
    private object? dragItem;
    private FrameworkElement? dragElement;
    private readonly bool fixtureMode;
    public MainWindow() : this(null, false) { }
    public MainWindow(string? dataDirectory, bool fixtureMode)
    {
        this.fixtureMode = fixtureMode;
        var args = Environment.GetCommandLineArgs(); var index = Array.IndexOf(args, "--data-dir");
        var root = dataDirectory ?? (index >= 0 && index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwitchDesk"));
        settingsStore = new(root); Directory.CreateDirectory(root); settings = settingsStore.Load();
        library = new(Path.Combine(root, "library.db")); cache = new(Path.Combine(root, "ConsoleCache"));
        phase2 = new(Path.Combine(root, "Phase2", "state.db"));
        syncStore = new(Path.Combine(root, "SaveSync"));
        new MigrationWorkspace(phase2, Path.Combine(root, "Phase2"), CompatibilityCatalog.Released).RecoverInterruptedOperations();
        ThemeManager.Apply(settings.Theme); InitializeComponent(); SourceInitialized += (_, _) => ThemeManager.ApplyCaption(this); BindCommands(); InitializePhase2(); InitializeSaveLibrary(); InitializeResources(); DataContext = vm;
        vm.Library.ListView = settings.ListView;
        vm.Library.SelectionChanged += () => { if (vm.Library.Selected != null) { vm.DetailOpen = true; UpdateDetails(); Animate(Details); } LayoutDetails(); };
        vm.Saves.SelectionChanged += UpdateAutoPolicy; vm.Saves.AutoChanged += SetAutoPolicy;
        vm.SaveLibrary.Detail.SelectionChanged += UpdateAutoPolicy; vm.SaveLibrary.Detail.AutoChanged += SetAutoPolicy;
        vm.Tasks.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.Tasks.HasActions)) JobActionsColumn.Visibility = vm.Tasks.HasActions ? Visibility.Visible : Visibility.Collapsed; };
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.Notice)) UpdateNotice(); if (e.PropertyName == nameof(vm.DetailOpen)) LayoutDetails(); };
        vm.Library.Filtered.CollectionChanged += (_, _) => EmptyLibrary.Visibility = vm.Library.Filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        InstallGrid.ItemsSource = installEntries; UploadGrid.ItemsSource = uploadEntries;
        var cached = cache.Latest();
        if (cached != null) { device = cached.Device; titles = cached.Games; saves = cached.Saves; synced = cached.SyncedAt; vm.Storages = cached.Storages; UpdateDevice(cached.Device); vm.Connection = "离线记录"; }
        VersionLabel.Text = $"SwitchDesk {BuildInfo.Version}"; LoadLocalLists(); ThemeChoice.SelectedValue = settings.Theme; UpdateSettingsText(); initialized = true;
        FtpHost.Text = settings.FtpHost; FtpPort.Text = settings.FtpPort.ToString(); FtpUser.Text = settings.FtpUser; FtpPassword.Password = settings.FtpPassword;
        Navigate("library"); UpdateNotice();
        if (settingsStore.RecoveryMessage.Length > 0) vm.Notify(settingsStore.RecoveryMessage);
        if (args.Contains("--smoke-test") || fixtureMode) { vm.Fixture = fixtureMode; ShowActivated = false; ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.Manual; Left = -16000; Top = -16000; }
    }
    private void BindCommands()
    {
        void Async(string name, Func<Task> action, Func<bool>? can = null) => vm.Bind(name, p => { _ = Guard(action); }, p => can?.Invoke() ?? true);
        bool Ready(string capability) => !ManagementPaused && !closingApplication && vm.Online && client?.IsBroken == false && device?.Capabilities.Contains(capability) == true;
        bool LiveSave() => Ready("saves") && Volatile.Read(ref activeJobs) == 0 && ActiveSaves.Selected?.Live != null;
        vm.Bind("Navigate", p => { if (p?.ToString() == "cross-save" && vm.CrossSave.CanSelect) OpenCrossSaveHome(); else Navigate(p?.ToString() ?? "library"); });
        vm.Bind("SelectGame", p => { if (p is GameCard card) { vm.Library.Selected = card; vm.DetailOpen = true; UpdateDetails(); LayoutDetails(); } });
        vm.Bind("CloseDetails", _ => vm.DetailOpen = false);
        vm.Bind("Source", p => { vm.Library.Source = p?.ToString() ?? "全部"; MarkFilters(); });
        vm.Bind("ClearFilters", _ => { vm.Library.Search = ""; vm.Library.Source = "全部"; vm.Library.StatusFilter = "全部状态"; Navigate("library"); });
        vm.Bind("Grid", _ => SetView(false)); vm.Bind("List", _ => SetView(true));
        vm.Bind("Dismiss", _ => vm.Notice = ""); vm.Bind("NoticeOpen", _ => Reveal(vm.NoticePath), _ => vm.NoticePath.Length > 0);
        Async("InstallPick", PickInstallAsync); Async("InstallLocal", () => ReviewInstallAsync([SelectedLocal().Path]), () => LocalFiles.SelectedItem is LibraryItem);
        Async("DbiInstall", () => DbiInstallAsync(false), CanStartDbi);
        Async("DbiLaunch", () => DbiInstallAsync(true), CanLaunchConsoleDbi);
        Async("Export", ExportAsync, () => Ready("export") && vm.Library.Selected?.Console?.Components.Length > 0);
        Async("ExportDirectory", () => { var pick = new OpenFolderDialog { Title = "选择游戏导出目录", InitialDirectory = settings.ExportDirectory }; if (pick.ShowDialog(this) == true) { SaveSettings(settings with { ExportDirectory = pick.FolderName }); UpdateSettingsText(); } return Task.CompletedTask; });
        Async("Backup", async () => { var result = await BackupAsync(SelectedSave()); vm.Notify("已备份 · " + result.Manifest.GameName, Path.GetDirectoryName(result.Path)!); }, LiveSave);
        Async("Restore", RestoreAsync, () => LiveSave() && CanRestoreSnapshot(ActiveSnapshot));
        vm.Bind("RestoreVersion", p => { if (p is Snapshot snapshot) { ActiveSnapshot = snapshot; _ = Guard(RestoreAsync); } }, p => LiveSave() && p is Snapshot snapshot && CanRestoreSnapshot(snapshot));
        Async("VerifySnapshot", VerifySnapshotAsync, () => ActiveSnapshot != null);
        Async("AddLibrary", AddLibraryAsync); Async("ScanLibrary", () => ScanLibraryAsync(settings.LibraryDirectory));
        Async("VerifyLocal", VerifyLocalAsync, () => LocalFiles.SelectedItem is LibraryItem);
        Async("InspectLocal", InspectLocalAsync, () => Ready("inspect") && LocalFiles.SelectedItem is LibraryItem);
        vm.Bind("RevealLocal", _ => Reveal(Path.GetDirectoryName(SelectedLocal().Path)!), _ => LocalFiles.SelectedItem is LibraryItem);
        Async("UploadPick", PickUploadAsync, () => Ready("files")); Async("Download", DownloadAsync, () => Ready("files") && FilesGrid.SelectedItem != null);
        Async("Rename", RenameAsync, () => Ready("files") && FilesGrid.SelectedItem != null); Async("Delete", DeleteAsync, () => Ready("files") && FilesGrid.SelectedItem != null);
        Async("Mkdir", MkdirAsync, () => Ready("files"));
        Async("SaveSyncCheck", SaveSyncCheckAsync, () => Ready("saves.inspect") && Volatile.Read(ref activeJobs) == 0);
        vm.Bind("SaveSyncRowBackup", p => { _ = Guard(() => SaveSyncRowBackupAsync(p)); }, p => Ready("saves.inspect") && Volatile.Read(ref activeJobs) == 0 && IsCurrentSyncRow(p) && p is SaveSyncRow { CanBackup: true });
        vm.Bind("SaveSyncRowRestore", p => { _ = Guard(() => SaveSyncRowRestoreAsync(p)); }, p => Ready("restore.atomic.v1") && Volatile.Read(ref activeJobs) == 0 && IsCurrentSyncRow(p) && p is SaveSyncRow { CanRestore: true });
        Async("MigrateToCurrentDevice", MigrateToCurrentDeviceAsync, () => vm.SaveLibrary.SelectedSource is { IsCurrentDevice: false } && vm.SaveLibrary.SelectedSnapshot != null && Ready("restore.atomic.v1") && Volatile.Read(ref activeJobs) == 0);
        bool FtpLive() => ftpClient is { IsConnected: true } && Volatile.Read(ref activeJobs) == 0;
        Async("FtpConnect", FtpConnectAsync, () => ftpClient is not { IsConnected: true } && Volatile.Read(ref activeJobs) == 0 && !closingApplication);
        Async("FtpDisconnect", FtpDisconnectAsync, () => ftpClient is { IsConnected: true } && Volatile.Read(ref activeJobs) == 0);
        Async("FtpUpload", FtpUploadAsync, FtpLive); Async("FtpUploadFolder", FtpUploadFolderAsync, FtpLive); Async("FtpDownload", FtpDownloadAsync, () => FtpLive() && FtpGrid.SelectedItem != null);
        Async("FtpRename", FtpRenameAsync, () => FtpLive() && FtpGrid.SelectedItem != null); Async("FtpMkdir", FtpMkdirAsync, FtpLive);
        Async("FtpDelete", FtpDeleteAsync, () => FtpLive() && FtpGrid.SelectedItem != null);
        Async("HomebrewRefresh", HomebrewRefreshAsync, () => Volatile.Read(ref activeJobs) == 0 && !closingApplication);
        Async("HomebrewInstall", HomebrewInstallAsync, () => vm.SelectedHomebrew != null && Ready("files") && Volatile.Read(ref activeJobs) == 0);
        Async("CheatRefresh", CheatRefreshAsync, () => Ready("files") && vm.Library.Selected?.TitleId != null && Volatile.Read(ref activeJobs) == 0);
        Async("CheatImport", CheatImportAsync, () => Ready("cheats.text.v1") && vm.Library.Selected?.TitleId != null && Volatile.Read(ref activeJobs) == 0);
        bool CheatWritable() => CheatsList.SelectedItem is CheatEntry cheat && Volatile.Read(ref activeJobs) == 0 &&
            Ready(cheat.Path.EndsWith(".cheat", StringComparison.OrdinalIgnoreCase) || cheat.Path.EndsWith(".cheat.off", StringComparison.OrdinalIgnoreCase) ? "cheats.write.v1" : "cheats.text.v1");
        Async("CheatToggle", CheatToggleAsync, CheatWritable);
        Async("CheatDelete", CheatDeleteAsync, CheatWritable);
        Async("CleanMissing", CleanMissingAsync, () => Volatile.Read(ref activeJobs) == 0); Async("VerifyAllLibrary", VerifyAllLibraryAsync, () => Volatile.Read(ref activeJobs) == 0);
        Async("FindDupes", FindDupesAsync, () => Volatile.Read(ref activeJobs) == 0);
        Async("CompareSnapshot", CompareSnapshotAsync, () => ActiveSnapshot != null && Volatile.Read(ref activeJobs) == 0);
        Async("ExportSnapshot", ExportSnapshotAsync, () => ActiveSnapshot != null && Volatile.Read(ref activeJobs) == 0);
        Async("Refresh", () => RunJob("刷新主机资料", async (_, ct) => { await RefreshDeviceAsync(ct); return true; }), () => vm.Online);
        Async("Reconnect", async () => { await StopStreamAsync(); await DisconnectAsync(); }, () => Volatile.Read(ref activeJobs) == 0 && !closingApplication);
        vm.Bind("CancelJob", p => { if (p is Job j && j.CanCancel) { j.Cancellation.Cancel(); j.Detail = "正在请求取消"; } }, p => p is Job { CanCancel: true });
        vm.Bind("RetryJob", p => { if (p is Job { Retry: {} retry }) _ = Guard(retry); }, p => p is Job { State: "需要处理", Retry: not null } && Volatile.Read(ref activeJobs) == 0);
        vm.Bind("StreamGuide", _ => ShowStreamGuide());
        Async("CloseStream", () => StopStreamAsync(true)); Async("StopStream", () => StopStreamAsync(), () => streaming);
        Async("Stream", StreamAsync, () => Volatile.Read(ref activeJobs) == 0 && !streaming && !dbiMode && !closingApplication);
    }
    private async void OnLoaded(object sender, RoutedEventArgs e)
    { LayoutDetails(); if (Environment.GetCommandLineArgs().Contains("--smoke-test")) { await RenderSmokeAsync(); Close(); return; } if (fixtureMode) return; Log(BuildInfo.ClientId + " 启动；等待配套程序。"); _ = Task.Run(ObserveSteamChangesAsync); await MonitorAsync(); }
    private void Navigate(string page)
    {
        SearchHeader.Visibility = page == "library" ? Visibility.Visible : Visibility.Collapsed;
        HeaderInstall.Visibility = HeaderDbi.Visibility = SearchHeader.Visibility;
        vm.Page = page; vm.Library.SavesOnly = false; LibraryPage.Visibility = page == "library" ? Visibility.Visible : Visibility.Collapsed;
        SavesPage.Visibility = page == "saves" ? Visibility.Visible : Visibility.Collapsed;
        CrossSavePage.Visibility = page == "cross-save" ? Visibility.Visible : Visibility.Collapsed;
        ResourcesPage.Visibility = page == "resources" ? Visibility.Visible : Visibility.Collapsed;
        FilesPage.Visibility = page == "files" ? Visibility.Visible : Visibility.Collapsed;
        NetworkPage.Visibility = page == "network" ? Visibility.Visible : Visibility.Collapsed;
        ConsolePage.Visibility = page == "console" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed; DiagnosticsPage.Visibility = page == "diagnostics" ? Visibility.Visible : Visibility.Collapsed;
        if (page == "saves") { RefreshSaveLibrary(); UpdateAutoPolicy(); }
        if (page == "resources") RefreshResourceDownloads();
        if (page == "files" && vm.Online && FilesGrid.ItemsSource == null) _ = Guard(async () => await RunJob("打开 SD 目录", async (_, ct) => { await LoadFilesAsync(ct); return true; }));
        UpdateAutoPolicy(); LayoutDetails(); MarkNavigation(); UpdateNotice();
    }
    private void MarkNavigation()
    { foreach (var button in Descendants<Button>(Sidebar).Where(b => b.Command == vm.Commands["Navigate"])) { var selected = button.CommandParameter?.ToString() == vm.Page; button.SetResourceReference(BackgroundProperty, selected ? "RailSelected" : "Rail"); button.SetResourceReference(ForegroundProperty, selected ? "RailActiveText" : "RailMuted"); } }
    private void MarkFilters()
    { foreach (var button in Descendants<Button>(LibraryPage).Where(b => b.Command == vm.Commands["Source"])) { var selected = button.CommandParameter?.ToString() == vm.Library.Source; button.SetResourceReference(BackgroundProperty, selected ? "Selected" : "Canvas"); button.SetResourceReference(ForegroundProperty, selected ? "Accent" : "Secondary"); } }
    private static IEnumerable<T> Descendants<T>(DependencyObject node) where T : DependencyObject
    { for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) { var child = VisualTreeHelper.GetChild(node, i); if (child is T typed) yield return typed; foreach (var nested in Descendants<T>(child)) yield return nested; } }
    private void OnWindowSize(object sender, SizeChangedEventArgs e) { if (initialized) LayoutDetails(); }
    private void LayoutDetails()
    {
        if (!initialized) return; vm.Compact = ActualWidth < 1180; NavigationColumn.Width = new(vm.Compact ? 72 : 198);
        foreach (var element in new FrameworkElement[] { BrandLabel, ConsoleLabel, ConsoleStatusLabels, VersionLabel, WorkspaceLabel }) element.Visibility = vm.Compact ? Visibility.Collapsed : Visibility.Visible;
        var show = vm.DetailOpen && vm.Library.Selected != null && vm.Page == "library";
        DetailColumn.Width = new(show ? 304 : 0); Details.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        Dispatcher.BeginInvoke(() => { ResizeGallery(Math.Max(180, LibraryPage.ActualWidth - 20)); MarkNavigation(); MarkFilters(); });
    }
    private void OnGallerySize(object sender, SizeChangedEventArgs e) { if (initialized && e.NewSize.Width > 0) ResizeGallery(Math.Max(180, e.NewSize.Width - 20)); }
    private void ResizeGallery(double width)
    {
        var viewer = Descendants<ScrollViewer>(Gallery).FirstOrDefault();
        var row = Math.Clamp((int)(viewer?.VerticalOffset ?? 0), 0, Math.Max(0, vm.Library.Rows.Count - 1));
        var anchor = vm.Library.Rows.ElementAtOrDefault(row)?.Cards.FirstOrDefault()?.Key;
        var oldColumns = vm.Library.Rows.FirstOrDefault()?.Cards.Length;
        vm.Library.Resize(width);
        if (anchor != null && oldColumns != vm.Library.Rows.FirstOrDefault()?.Cards.Length)
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => { var target = vm.Library.Rows.FirstOrDefault(r => r.Cards.Any(c => c.Key == anchor)); if (target != null) Gallery.ScrollIntoView(target); });
    }
    private void SetView(bool list)
    {
        try { SaveSettings(settings with { ListView = list }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { vm.Notify("视图设置未保存：" + ex.Message); return; }
        vm.Library.ListView = list; GridViewButton.SetResourceReference(BackgroundProperty, list ? "Canvas" : "Selected"); ListViewButton.SetResourceReference(BackgroundProperty, list ? "Selected" : "Canvas");
    }
    private static void Animate(FrameworkElement element) { if (SystemParameters.ClientAreaAnimation) element.BeginAnimation(OpacityProperty, new DoubleAnimation(.55, 1, TimeSpan.FromMilliseconds(140))); }
    private void UpdateNotice() { var repeatedTaskError = vm.Page == "diagnostics" && vm.Tasks.Items.Any(x => x.State == "需要处理" && x.Detail == vm.Notice); NoticeBox.Visibility = vm.Notice.Length > 0 && !repeatedTaskError ? Visibility.Visible : Visibility.Collapsed; NoticeOpen.Visibility = vm.NoticePath.Length > 0 ? Visibility.Visible : Visibility.Collapsed; }
    private void UpdateSettingsText()
    { LibraryLocation.Text = settings.LibraryDirectory; RetentionLabel.Text = $"当前状态：{(vm.Saves.AutoBackup ? "开启" : "关闭")} · 保留最近 {settings.Retention} 个不同版本"; SettingsSummary.Text = $"构建  {BuildInfo.ClientId}\n备份  {settings.BackupDirectory}\n导出  {settings.ExportDirectory}\n历史保留  {settings.Retention} 个不同版本\n播放器  {(settings.SysDvrClient.Length > 0 ? settings.SysDvrClient : "尚未设置")}"; }
    private void LoadLocalLists()
    {
        ClearSaveSyncResults();
        var scroll = Descendants<ScrollViewer>(Gallery).FirstOrDefault(); var offset = scroll?.VerticalOffset ?? 0;
        var anchor = vm.Library.Rows.ElementAtOrDefault((int)offset)?.Cards.FirstOrDefault()?.Key;
        var scan = new SnapshotStore(settings.BackupDirectory).Scan(); var history = scan.Snapshots.ToArray(); vm.Saves.SetData(saves, history);
        vm.SaveLibrary.SetData(saves, scan, device, vm.Online, synced);
        vm.Library.SaveTitles = saves.Select(x => x.TitleId).Concat(history.Select(x => x.Manifest.TitleId)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        vm.Library.Merge(titles, library.List()); ApplyCatalogLabels(); UpdateDetails(); RefreshCrossSaveContext(); UpdateSyncText(); UpdateSettingsText();
        if (scroll != null) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => { var row = anchor == null ? -1 : vm.Library.Rows.ToList().FindIndex(r => r.Cards.Any(c => c.Key == anchor)); scroll.ScrollToVerticalOffset(row >= 0 ? row : offset); });
    }
    private void UpdateDetails()
    {
        var selectedTitle = vm.Library.Selected?.TitleId;
        if (cheatTitle != selectedTitle) { cheatTitle = selectedTitle; vm.Cheats.Clear(); }
        vm.Saves.SetContext(selectedTitle, device); UpdateAutoPolicy(); UpdatePhase2Details(); CommandManager.InvalidateRequerySuggested();
    }
    private async void OnDetailsTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || !ReferenceEquals(e.Source, DetailsTabs) || DetailsTabs.SelectedIndex != 3) return;
        if (vm.Library.Selected?.TitleId == null || !vm.Online || client?.IsBroken != false || device?.Capabilities.Contains("files") != true) return;
        if (Volatile.Read(ref activeJobs) != 0) return;
        await Guard(CheatRefreshAsync);
    }
    private void UpdateSyncText() => vm.SyncText = synced is {} time ? $"{(vm.Online ? "已同步" : "离线记录")} · {time.ToLocalTime():MM-dd HH:mm} · {device?.Environment}" : "连接 Switch，或添加电脑游戏目录";
    private void UpdateDevice(DeviceInfo info) { vm.Device = info; vm.Model = info.Model; vm.EnvironmentText = info.Environment + (info.ApplicationMode ? "" : " · Applet 模式"); }
    private bool ApplyConnectedDevice(DeviceInfo info)
    {
        var changed = device?.DeviceId != info.DeviceId || device.Environment != info.Environment;
        device = info; UpdateDevice(info);
        if (!changed) return false;
        ClearSaveSyncResults(); InvalidateCrossSaveConnection(); ClearFileContext();
        titles = []; saves = []; vm.Storages = []; vm.Cheats.Clear(); cheatTitle = null; synced = null;
        LoadLocalLists();
        return true;
    }
    private void SetConnectionMessage(string message) { vm.Connection = message; if (message != lastConnectionMessage) { lastConnectionMessage = message; Log(message); } }
    private async Task MonitorAsync()
    {
        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                if (closingApplication) break;
                if (ManagementPaused || bindingUsbDriver || Volatile.Read(ref activeJobs) != 0) { await Task.Delay(1000, lifetime.Token); continue; }
                if (client?.IsBroken == true) await DisconnectAsync();
                if (client == null)
                {
                    var candidates = await Task.Run(UsbDiscovery.Enumerate, lifetime.Token); var message = candidates.Count == 0 ? (synced != null ? "离线记录 · 等待连接" : "等待配套程序 · 可在连接诊断检查 USB 驱动") : "检测到接口 · 正在核对";
                    foreach (var candidate in candidates)
                    {
                        DeviceClient? attempt = null;
                        try
                        {
                            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(3));
                            var info = await ConnectManagedCandidateAsync(async ct => new DeviceClient(await Task.Run(() => new WinUsbStream(candidate.Path), ct)), timeout.Token);
                            if (info == null) break;
                            attempt = client;
                            vm.Online = true; ApplyConnectedDevice(info); message = "已连接";
                            await RunJob("读取主机资料", async (_, ct) => { await RefreshDeviceAsync(ct); return true; }); ScheduleAutoBackups(); _ = RefreshCatalogInBackgroundAsync(); break;
                        }
                        catch (Exception ex)
                        { if (attempt != null) await attempt.DisposeAsync(); if (ReferenceEquals(client, attempt)) { client = null; vm.Online = false; } message = ex is Win32Exception { NativeErrorCode: 5 or 32 } ? "接口被占用 · 打开诊断" : ex.Message; }
                    }
                    if (!ManagementPaused && !closingApplication) SetConnectionMessage(message);
                }
                else
                {
                    try
                    {
                        if (!await jobsGate.WaitAsync(0, lifetime.Token)) { await Task.Delay(250, lifetime.Token); continue; }
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(4));
                        DeviceInfo info; try { info = await client.QueryAsync<DeviceInfo>("device.info", ct: timeout.Token); } finally { jobsGate.Release(); }
                        if (ManagementPaused || closingApplication) continue;
                        if (ApplyConnectedDevice(info)) await RunJob("切换系统环境", async (_, ct) => { await RefreshDeviceAsync(ct); return true; });
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or JsonException or InvalidOperationException) { Log("连接中断：" + ex.Message); await DisconnectAsync(); }
                }
                await Task.Delay(1500, lifetime.Token);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { break; }
            catch (Exception ex) { Log("连接检查：" + ex.Message); try { await Task.Delay(3000, lifetime.Token); } catch (OperationCanceledException) { break; } }
        }
    }
    private async Task<DeviceInfo?> ConnectManagedCandidateAsync(Func<CancellationToken, Task<DeviceClient>> open, CancellationToken ct)
    {
        await jobsGate.WaitAsync(ct);
        DeviceClient? attempt = null;
        try
        {
            if (ManagementPaused || closingApplication) return null;
            attempt = await open(ct);
            var info = await attempt.HandshakeAsync(ct);
            if (ManagementPaused || closingApplication) return null;
            client = attempt; attempt = null; return info;
        }
        finally
        {
            try { if (attempt != null) await attempt.DisposeAsync(); }
            finally { jobsGate.Release(); }
        }
    }
    private async Task DisconnectAsync()
    { var old = client; client = null; vm.Online = false; ClearSaveSyncResults(); InvalidateCrossSaveConnection(); ClearFileContext(); if (old != null) await old.DisposeAsync(); SetConnectionMessage(synced != null ? "离线记录 · 等待连接" : "等待配套程序"); RefreshSaveLibrary(); RefreshCrossSaveContext(); UpdateSyncText(); UpdateAutoPolicy(); }
    private (DeviceClient Client, DeviceInfo Device) RequireDevice(string? capability = null)
    {
        if (ManagementPaused || closingApplication) throw new IOException(ManagementPauseMessage);
        if (!vm.Online || client == null || client.IsBroken || device == null) throw new IOException("请连接 Switch 并打开配套程序；当前显示的是离线记录");
        if (capability != null && !device.Capabilities.Contains(capability))
        {
            var message = !device.ApplicationMode
                ? "当前是 Applet 模式（相册启动），不支持 " + capability + "。请退出配套程序，在主界面按住 R 打开任意游戏进入 hbmenu，再启动 SwitchDesk。"
                : capability switch
                {
                    "cheats.write.v1" => "当前配套程序版本不支持金手指写入；请部署带 cheats.write.v1 的新版 NRO。",
                    "cheats.text.v1" => "标准 .txt 金手指需要带 cheats.text.v1 的新版配套程序，请先更新 NRO。",
                    "install.plan.v1" or "package.dependencies.v1" or "operations.status" => "更新安装需要新版 NRO（install.plan.v1）；请更新配套程序。",
                    _ => "当前配套程序版本不支持此操作：" + capability + "；请更新 NRO。"
                };
            throw new DeviceException("Unavailable", message);
        }
        return (client, device);
    }
    private async Task RefreshDeviceAsync(CancellationToken ct)
    {
        var (peer, info) = RequireDevice(); var storage = await peer.QueryAsync<StorageInfo[]>("storages.list", ct: ct); var games = await peer.QueryAsync<GameTitle[]>("titles.list", ct: ct);
        var list = info.Capabilities.Contains("saves") ? await peer.QueryAsync<SaveInfo[]>("saves.list", ct: ct) : []; var time = DateTimeOffset.UtcNow;
        await cache.SaveAsync(new(info, storage, games, list, time), ct); await Dispatcher.InvokeAsync(() => { vm.Storages = storage; titles = games; saves = list; synced = time; LoadLocalLists(); });
    }
    private async Task<T> RunJob<T>(string name, Func<Job, CancellationToken, Task<T>> action, Func<Task>? retry = null)
    {
        var job = new Job { Name = name, Retry = retry }; if (Dispatcher.CheckAccess()) vm.Tasks.Add(job); else await Dispatcher.InvokeAsync(() => vm.Tasks.Add(job)).Task.ConfigureAwait(false);
        Interlocked.Increment(ref activeJobs); using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, job.Cancellation.Token);
        _ = Dispatcher.BeginInvoke(RefreshCrossSaveAvailability);
        try
        {
            await jobsGate.WaitAsync(linked.Token).ConfigureAwait(false);
            try { _ = Dispatcher.BeginInvoke(() => { job.State = "准备"; job.Detail = "准备中"; }); Log("开始：" + name);
                var result = await Task.Run(() => action(job, linked.Token), linked.Token).ConfigureAwait(false);
                _ = Dispatcher.BeginInvoke(() => { job.State = "完成"; job.Percent = 100; job.Indeterminate = false; job.CanCancel = false; job.Detail = "已完成"; }); Log("完成：" + name); return result;
            } finally { jobsGate.Release(); }
        }
        catch (OperationCanceledException) { _ = Dispatcher.BeginInvoke(() => { job.State = "已取消"; job.CanCancel = false; job.Detail = "可重新执行；保留已完成结果"; }); Log("取消：" + name); throw; }
        catch (Exception ex) { _ = Dispatcher.BeginInvoke(() => { job.State = "需要处理"; job.CanCancel = false; job.Detail = ex.Message; }); Log($"失败：{name} · {ex.Message}"); throw; }
        finally { Interlocked.Decrement(ref activeJobs); _ = Dispatcher.BeginInvoke(() => { vm.Tasks.Refresh(); RefreshCrossSaveAvailability(); CommandManager.InvalidateRequerySuggested(); }); }
    }
    private IProgress<TransferProgress> Progress(Job job)
    {
        var watch = Stopwatch.StartNew(); long previous = 0; string stage = "";
        return new DirectProgress(p => { void Update() { if (stage != p.Stage || p.Completed < previous) { watch.Restart(); stage = p.Stage; } previous = p.Completed; job.Report(p); job.Rate = p.Total > 0 && watch.Elapsed.TotalSeconds > .1 ? Display.Size((long)(p.Completed / watch.Elapsed.TotalSeconds)) + "/s" : ""; }
            if (p.Stage.Contains("写回存档", StringComparison.Ordinal)) Dispatcher.Invoke(Update); else _ = Dispatcher.BeginInvoke(Update); });
    }
    private sealed class DirectProgress(Action<TransferProgress> callback) : IProgress<TransferProgress> { public void Report(TransferProgress value) => callback(value); }
    private async Task Guard(Func<Task> action)
    { try { await action(); } catch (OperationCanceledException) { vm.Notify("任务已取消，可在重连后重新执行"); } catch (Exception ex) { Log(ex.Message); if (ex.Data["SwitchDesk.RecoveryPath"] is string recovery) vm.Notify(ex.Message + " · 恢复前快照已保留", Path.GetDirectoryName(recovery)!); else vm.Notify(ex.Message); } }
    private void Log(string text)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {text}";
        try { lock (logLock) { Directory.CreateDirectory(Path.Combine(settingsStore.Root, "Logs")); File.AppendAllText(Path.Combine(settingsStore.Root, "Logs", DateTime.Today.ToString("yyyyMMdd") + ".log"), line + Environment.NewLine); } }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { line += " · 本机日志未能写入，保留会话内记录"; }
        _ = Dispatcher.BeginInvoke(() => vm.RecordLog(line, text));
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closeReady) return;
        e.Cancel = true;
        if (closingApplication) return;
        if (bindingUsbDriver) { vm.Notify("请先完成或取消 Windows 驱动绑定，再关闭 SwitchDesk"); return; }
        if (dbiMode) { dbiWindow?.Activate(); vm.Notify("请先结束外部 DBI 后端会话并返回，再关闭 SwitchDesk"); return; }
        if (Volatile.Read(ref activeJobs) != 0) { Navigate("diagnostics"); vm.Notify("请等待任务完成，或在连接诊断中取消任务；存档写回期间不可取消"); return; }
        closingApplication = true; Root.IsEnabled = false;
        try
        {
            await StopStreamAsync(true); lifetime.Cancel();
            var previous = client; client = null; if (previous != null) await previous.DisposeAsync();
            var ftp = ftpClient; ftpClient = null; if (ftp != null) await ftp.DisposeAsync();
            library.Dispose(); phase2.Dispose(); closeReady = true;
            _ = Dispatcher.BeginInvoke(Close, System.Windows.Threading.DispatcherPriority.Background);
        }
        catch (Exception ex) { closingApplication = false; Root.IsEnabled = true; Log("退出清理失败：" + ex.Message); vm.Notify("投屏清理未完成，请停止投屏后再次退出"); }
    }
}
