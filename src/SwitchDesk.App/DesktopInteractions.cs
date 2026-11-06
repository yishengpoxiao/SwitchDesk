using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SwitchDesk.Core;

namespace SwitchDesk.App;
public partial class MainWindow
{
    private sealed record SdFileContext(DeviceClient Peer, string DeviceId, string Environment);
    private SdFileContext? fileContext, uploadContext;

    private SdFileContext CaptureFileContext(RemoteEntry? selected = null)
    {
        var (peer, info) = RequireDevice("files");
        var context = new SdFileContext(peer, info.DeviceId, info.Environment);
        if (selected != null && (fileContext != context || !FilesGrid.Items.Contains(selected)))
            throw new IOException("SD 文件列表已失效，请重新打开目录");
        return context;
    }
    private DeviceClient DemandFileContext(SdFileContext? context)
    {
        var (peer, info) = RequireDevice("files");
        if (context == null || !ReferenceEquals(peer, context.Peer) || info.DeviceId != context.DeviceId || info.Environment != context.Environment)
            throw new IOException("主机连接或系统环境已变化，请重新打开目录并确认操作");
        return peer;
    }
    private void ClearFileContext()
    {
        fileContext = null; uploadContext = null; FilesGrid.ItemsSource = null;
        uploadEntries.Clear(); UploadOverlay.Visibility = Visibility.Collapsed;
        RemotePath.Text = "/"; UpdateBreadcrumbs("/"); FileTarget.Text = "拖入此处 → SD 卡 /";
    }
    private async void OnCardLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: GameCard card } || card.Icon != null || card.Console == null || device == null) return;
        var info = device; var path = cache.IconPath(info, card.TitleId!); if (!loadingIcons.Add(path)) return;
        try
        {
            byte[]? bytes = null;
            if (File.Exists(path)) bytes = await File.ReadAllBytesAsync(path, lifetime.Token);
            else
            {
                while (vm.Online && device?.DeviceId == info.DeviceId && device.Environment == info.Environment && ((FrameworkElement)sender).DataContext == card && ((FrameworkElement)sender).IsVisible)
                {
                    if (Volatile.Read(ref activeJobs) == 0 && await jobsGate.WaitAsync(0, lifetime.Token))
                    {
                        try
                        {
                            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(3));
                            var (peer, _) = RequireDevice(); bytes = (await peer.CallAsync("titles.icon", new { titleId = card.TitleId }, timeout.Token)).Binary;
                            if (bytes is { Length: > 0 }) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllBytesAsync(path + ".partial", bytes, lifetime.Token); File.Move(path + ".partial", path, true); }
                        }
                        finally { jobsGate.Release(); }
                        break;
                    }
                    await Task.Delay(180, lifetime.Token);
                }
            }
            if (bytes is { Length: > 0 }) card.Icon = await Task.Run(() => DecodeIcon(bytes));
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or NotSupportedException or ArgumentException) { Log("图标暂不可用：" + ex.Message); }
        finally { loadingIcons.Remove(path); }
    }
    private static BitmapImage DecodeIcon(byte[] bytes)
    { using var data = new MemoryStream(bytes); var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.DecodePixelWidth = 256; image.StreamSource = data; image.EndInit(); image.Freeze(); return image; }
    private async void OnWindowKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (vm.Page == "saves") SavesPage.FocusSearch();
            else if (vm.Page == "resources") ResourcesPage.FocusSearch();
            else { Navigate("library"); SearchBox.Focus(); SearchBox.SelectAll(); }
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Alt && e.SystemKey is >= Key.D1 and <= Key.D7)
        { var page = vm.Navigation[e.SystemKey - Key.D1].Id; if (page == "cross-save" && vm.CrossSave.CanSelect) OpenCrossSaveHome(); else Navigate(page); e.Handled = true; }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            // Close the visible overlay before changing the page behind it.
            if (vm.StreamGuide) { await Guard(() => StopStreamAsync(true)); return; }
            if (InstallOverlay.IsVisible) { if (!batchRunning && !reviewing) InstallOverlay.Visibility = Visibility.Collapsed; return; }
            if (UploadOverlay.IsVisible) { UploadOverlay.Visibility = Visibility.Collapsed; return; }
            if (vm.Page == "cross-save" && vm.CrossSave.HasSelection) { if (vm.CrossSave.CanSelect) OpenCrossSaveHome(); return; }
            if (vm.Page == "saves" && vm.SaveLibrary.SelectedGame != null) { vm.SaveLibrary.SelectedGame = null; return; }
            vm.DetailOpen = false;
        }
    }
    private void OnGalleryKey(object sender, KeyEventArgs e)
    {
        if (vm.Library.Filtered.Count == 0) return; var index = vm.Library.Selected == null ? 0 : vm.Library.Filtered.IndexOf(vm.Library.Selected); var columns = vm.Library.Rows.FirstOrDefault()?.Cards.Length ?? 1;
        var delta = e.Key switch { Key.Left => -1, Key.Right => 1, Key.Up => -columns, Key.Down => columns, _ => 0 };
        if (delta != 0 || e.Key == Key.Enter) { index = Math.Clamp(index + delta, 0, vm.Library.Filtered.Count - 1); vm.Library.Selected = vm.Library.Filtered[index]; vm.DetailOpen = true; Gallery.ScrollIntoView(vm.Library.Rows[index / columns]); e.Handled = true; }
    }
    private void OnCommandSelection(object sender, SelectionChangedEventArgs e) => CommandManager.InvalidateRequerySuggested();
    private void OnGameSelection(object sender, SelectionChangedEventArgs e) { if (GamesGrid.SelectedItem is GameCard card) vm.Library.Selected = card; }
    private async Task LoadFilesAsync(CancellationToken ct)
    {
        var path = await Dispatcher.InvokeAsync(() => PathSafety.Remote(RemotePath.Text)); var context = CaptureFileContext();
        var entries = await context.Peer.QueryAsync<RemoteEntry[]>("fs.list", new { path }, ct);
        await Dispatcher.InvokeAsync(() => { DemandFileContext(context); RemotePath.Text = path; UpdateBreadcrumbs(path); FileTarget.Text = "拖入此处 → SD 卡 " + path; FilesGrid.ItemsSource = entries.OrderByDescending(x => x.IsDirectory).ThenBy(x => x.Name).ToArray(); fileContext = context; });
    }
    private void UpdateBreadcrumbs(string path)
    {
        Breadcrumbs.Children.Clear();
        void Add(string text, string target)
        {
            var button = new Button { Content = text, Style = (Style)FindResource("Quiet"), Padding = new(8, 4, 8, 4), MinHeight = 30, ToolTip = target };
            button.Click += async (_, _) => { RemotePath.Text = target; await OpenFolderAsync(); }; Breadcrumbs.Children.Add(button);
        }
        Add("SD 卡", "/"); var current = "";
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries)) { current += "/" + part; Add("› " + part, current); }
    }
    private async void OnPathKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) await OpenFolderAsync(); }
    private Task OpenFolderAsync() => Guard(async () => await RunJob("打开 SD 目录", async (_, ct) => { await LoadFilesAsync(ct); return true; }));
    private async void OnParentFolder(object sender, RoutedEventArgs e) { var path = RemotePath.Text.TrimEnd('/'); var index = path.LastIndexOf('/'); RemotePath.Text = index <= 0 ? "/" : path[..index]; await OpenFolderAsync(); }
    private async void OnOpenRemote(object sender, MouseButtonEventArgs e) { if (FilesGrid.SelectedItem is RemoteEntry { IsDirectory: true } entry) await Guard(async () => { CaptureFileContext(entry); RemotePath.Text = entry.Path; await OpenFolderAsync(); }); }
    private void OnFileDragOver(object sender, DragEventArgs e) { OnInstallDragOver(sender, e); FileTarget.Text = "释放后复制到 → SD 卡 " + RemotePath.Text; }
    private async Task PickUploadAsync() { var pick = new OpenFileDialog { Multiselect = true }; if (pick.ShowDialog(this) == true) await ReviewUploadAsync(pick.FileNames); }
    private async void OnUploadDrop(object sender, DragEventArgs e) { if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) await Guard(() => ReviewUploadAsync(paths)); }
    private Task ReviewUploadAsync(string[] paths)
    {
        var context = CaptureFileContext(); var directory = PathSafety.Remote(RemotePath.Text); foreach (var path in paths) PathSafety.DemandWritable(directory.TrimEnd('/') + "/" + Path.GetFileName(path));
        uploadContext = context;
        uploadDirectory = directory; uploadEntries.Clear(); var existing = (FilesGrid.ItemsSource as IEnumerable<RemoteEntry> ?? []).Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths) uploadEntries.Add(new(path, Path.GetFileName(path), existing.Contains(Path.GetFileName(path))));
        UploadContext.Text = "目标 → SD 卡 " + directory + " · 同名文件请修改名称或跳过"; UploadOverlay.Visibility = Visibility.Visible; return Task.CompletedTask;
    }
    private async void OnCommitUpload(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var context = uploadContext; var directory = uploadDirectory; DemandFileContext(context);
        UploadGrid.CommitEdit(); UploadGrid.CommitEdit(); var entries = uploadEntries.Where(x => !x.Skip).ToArray(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries) { if (entry.TargetName.Contains('/') || entry.TargetName.Contains('\\')) throw new IOException("目标名称不能包含路径分隔符"); var target = PathSafety.Remote(directory.TrimEnd('/') + "/" + PathSafety.Relative(entry.TargetName)); PathSafety.DemandWritable(target); if (!names.Add(target)) throw new IOException("清单中存在同名目标，请改名或跳过"); }
        await RunJob("复制到 SD 卡 · " + entries.Length + " 项", async (job, ct) =>
        {
            var peer = DemandFileContext(context); var current = await peer.QueryAsync<RemoteEntry[]>("fs.list", new { path = directory }, ct);
            if (entries.Any(e => current.Any(x => x.Name.Equals(e.TargetName, StringComparison.OrdinalIgnoreCase)))) throw new IOException("目标目录存在同名文件，请在清单内改名或跳过");
            foreach (var entry in entries) await Transfers.UploadTreeAsync(peer, entry.Local, directory.TrimEnd('/') + "/" + entry.TargetName, Progress(job), ct);
            await LoadFilesAsync(ct); return true;
        }); UploadOverlay.Visibility = Visibility.Collapsed; vm.Notify($"复制完成 · {entries.Length} 项");
    });
    private void OnCloseUpload(object sender, RoutedEventArgs e) => UploadOverlay.Visibility = Visibility.Collapsed;
    private async Task DownloadAsync()
    {
        var entry = FilesGrid.SelectedItem as RemoteEntry ?? throw new IOException("请选择文件或文件夹"); var context = CaptureFileContext(entry); var pick = new OpenFolderDialog { Title = "选择电脑目标目录" }; if (pick.ShowDialog(this) != true) return;
        var destination = PathSafety.Child(pick.FolderName, entry.Name);
        await RunJob("下载 · " + entry.Name, async (job, ct) => { var peer = DemandFileContext(context); if (entry.IsDirectory) await Transfers.DownloadTreeAsync(peer, entry.Path, destination, Progress(job), ct); else await Transfers.DownloadAsync(peer, entry.Path, destination, Progress(job), ct); return true; }); vm.Notify("下载完成", pick.FolderName);
    }
    private async Task MkdirAsync()
    {
        var context = CaptureFileContext(); var directory = PathSafety.Remote(RemotePath.Text);
        var name = Dialogs.Text(this, "新建文件夹", "文件夹名称"); if (name == null) return; var path = PathSafety.Remote(directory.TrimEnd('/') + "/" + PathSafety.Relative(name)); PathSafety.DemandWritable(path);
        await RunJob("新建文件夹", async (_, ct) => { var peer = DemandFileContext(context); await peer.CallAsync("fs.mkdir", new { path }, ct); await LoadFilesAsync(ct); return true; });
    }
    private async Task RenameAsync()
    {
        var entry = FilesGrid.SelectedItem as RemoteEntry ?? throw new IOException("请选择文件"); var context = CaptureFileContext(entry); PathSafety.DemandWritable(entry.Path, protectDescendants: true); var name = Dialogs.Text(this, "重命名", "新名称", entry.Name); if (name == null) return;
        var target = PathSafety.Remote(entry.Path[..entry.Path.LastIndexOf('/')] + "/" + PathSafety.Relative(name)); PathSafety.DemandWritable(target, protectDescendants: true);
        await RunJob("重命名", async (_, ct) => { var peer = DemandFileContext(context); await peer.CallAsync("fs.rename", new { path = entry.Path, target }, ct); await LoadFilesAsync(ct); return true; });
    }
    private async Task DeleteAsync()
    {
        var entry = FilesGrid.SelectedItem as RemoteEntry ?? throw new IOException("请选择文件"); var context = CaptureFileContext(entry); PathSafety.DemandWritable(entry.Path, protectDescendants: true); if (!Dialogs.Confirm(this, "永久删除 SD 文件", entry.Path + "\n\n此操作无法撤销。文件夹必须为空。", "永久删除")) return;
        await RunJob("删除 SD 文件", async (_, ct) => { var peer = DemandFileContext(context); await peer.CallAsync("fs.delete", new { path = entry.Path }, ct); await LoadFilesAsync(ct); return true; });
    }
    private void BeginDrag(FrameworkElement element, object? item, MouseButtonEventArgs e) { dragOrigin = e.GetPosition(element); dragItem = item; dragElement = element; }
    private bool DragThreshold(MouseEventArgs e)
    { if (dragElement == null || e.LeftButton != MouseButtonState.Pressed) return false; var p = e.GetPosition(dragElement); return Math.Abs(p.X - dragOrigin.X) >= SystemParameters.MinimumHorizontalDragDistance || Math.Abs(p.Y - dragOrigin.Y) >= SystemParameters.MinimumVerticalDragDistance; }
    private void OnSaveMouseDown(object sender, MouseButtonEventArgs e) => BeginDrag((FrameworkElement)sender, vm.Saves.Selected?.Live, e);
    private void OnSaveMouseMove(object sender, MouseEventArgs e)
    {
        if (dragItem is not SaveInfo save || !DragThreshold(e)) return; dragItem = null;
        try { RequireDevice("saves"); var task = BackupAsync(save).ContinueWith(t => t.GetAwaiter().GetResult().Path, TaskScheduler.Default); VirtualFileDrag.Start(() => task, PathSafety.SafeName(save.GameName + "_" + save.UserName) + ".zip"); e.Handled = true; } catch (Exception ex) { vm.Notify(ex.Message); }
    }
    private static T? ItemAt<T>(object source) where T : class
    { var current = source as DependencyObject; while (current != null) { if (current is FrameworkElement { DataContext: T item }) return item; current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current); } return null; }
    private void OnHistoryMouseDown(object sender, MouseButtonEventArgs e) => BeginDrag(SnapshotsGrid, ItemAt<Snapshot>(e.OriginalSource), e);
    private void OnHistoryMouseMove(object sender, MouseEventArgs e)
    {
        if (dragItem is not Snapshot snapshot || !DragThreshold(e)) return; dragItem = null;
        try { VirtualFileDrag.Start(() => Task.FromResult(snapshot.Path), PathSafety.SafeName(snapshot.Name) + ".zip", snapshot.Manifest); e.Handled = true; } catch (Exception ex) { vm.Notify(ex.Message); }
    }
    private void OnFileMouseDown(object sender, MouseButtonEventArgs e) => BeginDrag(FilesGrid, ItemAt<RemoteEntry>(e.OriginalSource), e);
    private void OnFileMouseMove(object sender, MouseEventArgs e)
    {
        if (dragItem is not RemoteEntry { IsDirectory: false } entry || !DragThreshold(e)) return; dragItem = null;
        try { var context = CaptureFileContext(entry); var directory = Path.Combine(settingsStore.Root, "DragCache", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory); var target = Path.Combine(directory, PathSafety.SafeName(entry.Name));
          var task = RunJob("拖出 · " + entry.Name, async (job, ct) => { var peer = DemandFileContext(context); await Transfers.DownloadAsync(peer, entry.Path, target, Progress(job), ct); return target; }); VirtualFileDrag.Start(() => task, PathSafety.SafeName(entry.Name)); e.Handled = true; }
        catch (Exception ex) { vm.Notify(ex.Message); }
    }
    private void OnThemeChoice(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || ThemeChoice.SelectedValue is not string theme || theme == settings.Theme) return;
        var next = settings with { Theme = theme };
        try { SaveSettings(next); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ThemeChoice.SelectedValue = settings.Theme;
            vm.Notify("外观设置未保存：" + ex.Message);
            return;
        }
        ThemeManager.Apply(theme); MarkNavigation(); MarkFilters();
    }
    private void SaveSettings(AppSettings next)
    {
        settingsStore.Save(next);
        settings = next;
    }
    private void OnSettings(object sender, RoutedEventArgs e)
    {
        var value = Dialogs.Settings(this, settings); if (value == null) return;
        try { settingsStore.Save(value); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { vm.Notify(ex.Message); return; }
        settings = value;
        FtpHost.Text = settings.FtpHost; FtpPort.Text = settings.FtpPort.ToString(); FtpUser.Text = settings.FtpUser; FtpPassword.Password = settings.FtpPassword;
        LoadLocalLists();
    }
    private void OnRevealBackup(object sender, RoutedEventArgs e) => Reveal(settings.BackupDirectory);
    private void OnRevealLogs(object sender, RoutedEventArgs e) => Reveal(Path.Combine(settingsStore.Root, "Logs"));
    private static void Reveal(string path) { if (path.Length == 0) return; Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = false }); }
    private string DiagnosticsText() => DiagnosticText.Build(device, vm.Connection, vm.Online, vm.Logs, InstallError.Text) +
        Environment.NewLine + "USB 驱动检查：" + DriverReport.Text;
    private void CopyDiagnosticValue(string? text, string success)
    {
        if (TextCopy.TryWrite(text, Clipboard.SetText, out var error)) vm.Notify(success);
        else vm.Notify(error.Length == 0 ? "当前没有可复制的内容" : error);
    }
    private void OnCopyDiagnostics(object sender, RoutedEventArgs e) => CopyDiagnosticValue(DiagnosticsText(), "诊断信息已复制");
    private void OnCopyCompanion(object sender, RoutedEventArgs e) => CopyDiagnosticValue(device?.Companion, "Switch 配套版本已复制");
    private void OnSysDvrHelp(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("https://github.com/exelix11/SysDVR/wiki") { UseShellExecute = true });
    private void OnProjectHome(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("https://github.com/yishengpoxiao/SwitchDesk") { UseShellExecute = true });
}
