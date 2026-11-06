using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using Microsoft.Win32;
using SwitchDesk.Core;

namespace SwitchDesk.App;

public interface IDbiBackendRun : IAsyncDisposable
{
    void Start();
    Task WaitForExitAsync(CancellationToken ct);
}

public interface IDbiBackendHost : IDisposable
{
    Task<string> ResolveAsync(string path, string cache, IProgress<TransferProgress>? progress, CancellationToken ct);
    Task<string> DownloadOfficialAsync(string cache, IProgress<TransferProgress>? progress, CancellationToken ct);
    Task<IDbiBackendRun> PrepareAsync(string backend, IEnumerable<DbiBackendSource> sources, CancellationToken ct);
}

internal sealed class OfficialDbiBackendHost : IDbiBackendHost
{
    private readonly DbiBackendService service = new();
    public Task<string> ResolveAsync(string path, string cache, IProgress<TransferProgress>? progress, CancellationToken ct) => service.ResolveAsync(path, cache, progress, ct);
    public Task<string> DownloadOfficialAsync(string cache, IProgress<TransferProgress>? progress, CancellationToken ct) => service.DownloadOfficialAsync(cache, progress, ct);
    public async Task<IDbiBackendRun> PrepareAsync(string backend, IEnumerable<DbiBackendSource> sources, CancellationToken ct) => new Run(await service.PrepareAsync(backend, sources, ct));
    public void Dispose() => service.Dispose();
    private sealed class Run(DbiBackendSession session) : IDbiBackendRun
    {
        public void Start() => session.Start();
        public Task WaitForExitAsync(CancellationToken ct) => session.WaitForExitAsync(ct);
        public ValueTask DisposeAsync() => session.DisposeAsync();
    }
}

public sealed record DbiPackageRow(DbiBackendSource Source)
{
    public string Path => Source.Path;
    public string Name => System.IO.Path.GetFileName(Path);
    public string Verification => Source.ExpectedSha256 == null ? "本地文件 · 启动时保持只读，直到后端退出" : "下载记录 · 启动前核对长度与 SHA-256";
}

public partial class DbiInstallWindow : Window
{
    private readonly string cacheDirectory;
    private readonly IDbiBackendHost host;
    private readonly Action<string> saveBackendPath;
    private readonly Func<string, DbiBackendSource> resolveSource;
    private readonly ObservableCollection<DbiPackageRow> packages = [];
    private CancellationTokenSource? operationCancellation;
    private IDbiBackendRun? session;
    private bool busy, closeAfterOperation;

    public DbiInstallWindow(string backendPath, string cacheDirectory, Action<string> saveBackendPath,
        Func<string, DbiBackendSource>? resolveSource = null, IDbiBackendHost? host = null)
    {
        this.cacheDirectory = cacheDirectory; this.saveBackendPath = saveBackendPath;
        this.resolveSource = resolveSource ?? (path => new(path)); this.host = host ?? new OfficialDbiBackendHost();
        InitializeComponent(); BackendPath.Text = backendPath; PackageList.ItemsSource = packages;
        SourceInitialized += (_, _) => ThemeManager.ApplyCaption(this);
        Closed += (_, _) => this.host.Dispose();
    }

    public bool SessionActive => session != null;
    internal void SetConsoleLaunchStatus(string text) => ConsoleLaunchStatus.Text = text;
    public bool CloseBlocked => busy || SessionActive;

    public void AddFiles(IEnumerable<string> paths)
    {
        if (CloseBlocked) throw new IOException("请先结束当前 DBI 后端会话");
        var additions = paths.Select(System.IO.Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => !packages.Any(row => row.Path.Equals(path, StringComparison.OrdinalIgnoreCase))).ToArray();
        foreach (var path in additions)
        {
            if (!new[] { ".nsp", ".nsz", ".xci", ".xcz" }.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant()))
                throw new InvalidDataException("DBI 安装入口仅接受 NSP、NSZ、XCI、XCZ 文件");
            if (!File.Exists(path)) throw new FileNotFoundException("安装包不存在", path);
        }
        var rows = additions.Select(path => new DbiPackageRow(resolveSource(path))).ToArray();
        foreach (var row in rows) packages.Add(row);
        UpdateControls();
    }

    public async Task DownloadAsync()
    {
        if (CloseBlocked) return;
        await RunOperationAsync(async ct =>
        {
            StatusText.Text = "正在下载并校验官方 DBI 后端…";
            var path = await host.DownloadOfficialAsync(cacheDirectory, DownloadProgress(), ct);
            BackendPath.Text = path; saveBackendPath(path); StatusText.Text = "官方后端已校验；选择安装包后再启动。";
        });
    }

    public async Task StartAsync()
    {
        if (CloseBlocked || packages.Count == 0) return;
        await RunOperationAsync(async ct =>
        {
            StatusText.Text = "正在准备后端并核对所选文件…";
            var path = await host.ResolveAsync(BackendPath.Text.Trim(), cacheDirectory, DownloadProgress(), ct);
            BackendPath.Text = path; saveBackendPath(path);
            var sources = packages.Select(row => resolveSource(row.Path)).ToArray();
            session = await host.PrepareAsync(path, sources, ct);
            try
            {
                ct.ThrowIfCancellationRequested(); session.Start();
                OperationProgress.Visibility = Visibility.Collapsed;
                StatusText.Text = "DBI 后端已启动；请在 Switch 的 DBI 中选择文件和安装位置。结束时请关闭外部后端窗口。";
                await session.WaitForExitAsync(CancellationToken.None);
                StatusText.Text = "DBI 会话已结束，请在主机核对安装结果。";
            }
            finally
            {
                // Disposal waits for the external process before releasing its source-file locks.
                await session.DisposeAsync(); session = null;
            }
        });
    }

    private async Task RunOperationAsync(Func<CancellationToken, Task> action)
    {
        busy = true; operationCancellation = new(); UpdateControls();
        try { await action(operationCancellation.Token); }
        catch (OperationCanceledException) { StatusText.Text = "已取消准备，未确认任何安装结果。"; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or HttpRequestException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        { StatusText.Text = ex.Message + "；安装结果请在主机核对。"; }
        finally
        {
            operationCancellation.Dispose(); operationCancellation = null; busy = false; UpdateControls();
            if (closeAfterOperation && !SessionActive) Close();
        }
    }

    private IProgress<TransferProgress> DownloadProgress() => new Progress<TransferProgress>(progress =>
    { if (!SessionActive && busy) StatusText.Text = progress.Stage + (progress.Total > 0 ? $" · {progress.Percent:0}%" : ""); });

    private void UpdateControls()
    {
        var available = !CloseBlocked;
        BackendPath.IsEnabled = ChooseBackend.IsEnabled = DownloadBackend.IsEnabled = AddPackages.IsEnabled = ClearPackages.IsEnabled = available;
        StartBackend.IsEnabled = available && packages.Count > 0;
        OperationProgress.Visibility = busy && !SessionActive ? Visibility.Visible : Visibility.Collapsed;
        FileSummary.Text = packages.Count == 0 ? "尚未选择安装包" : $"已选择 {packages.Count} 个文件";
    }

    private async Task SelectBackendAsync(string path)
    {
        if (CloseBlocked) return;
        await RunOperationAsync(_ =>
        {
            saveBackendPath(path);
            BackendPath.Text = path;
            StatusText.Text = "后端路径已保存；选择安装包后再启动。";
            return Task.CompletedTask;
        });
    }

    private async void OnChooseBackend(object sender, RoutedEventArgs e)
    {
        var pick = new OpenFileDialog { Filter = "DBI 后端|dbibackend.exe|Windows 程序|*.exe" };
        if (pick.ShowDialog(this) == true) await SelectBackendAsync(pick.FileName);
    }
    private void OnAddPackages(object sender, RoutedEventArgs e)
    {
        var pick = new OpenFileDialog { Filter = "Switch 安装包|*.nsp;*.nsz;*.xci;*.xcz", Multiselect = true };
        if (pick.ShowDialog(this) == true)
            try { AddFiles(pick.FileNames); } catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException) { StatusText.Text = ex.Message; }
    }
    private void OnClearPackages(object sender, RoutedEventArgs e) { if (!CloseBlocked) { packages.Clear(); UpdateControls(); } }
    private async void OnDownloadBackend(object sender, RoutedEventArgs e) => await DownloadAsync();
    private async void OnStartBackend(object sender, RoutedEventArgs e) => await StartAsync();
    private void OnReturn(object sender, RoutedEventArgs e) => Close();
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (SessionActive) { e.Cancel = true; StatusText.Text = "请先关闭外部 DBI 后端窗口；后端结束并释放文件后才能返回。"; }
        else if (busy) { e.Cancel = true; closeAfterOperation = true; operationCancellation?.Cancel(); StatusText.Text = "正在取消准备，请稍候…"; }
    }
}
