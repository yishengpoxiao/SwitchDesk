using System.IO;
using System.Text.Json;
using System.Windows.Input;
using SwitchDesk.Core;

namespace SwitchDesk.App;

public partial class MainWindow
{
    private bool dbiMode;
    private DbiInstallWindow? dbiWindow;
    private bool CanStartDbi() => !dbiMode && !streaming && !closingApplication && !batchRunning && !reviewing && Volatile.Read(ref activeJobs) == 0;
    private bool CanLaunchConsoleDbi() => CanStartDbi() && vm.Online && client?.IsBroken == false && device?.Capabilities.Contains("dbi.launch.v1") == true;
    private string ManagementPauseMessage => dbiMode
        ? "DBI 会话进行中，请先关闭外部 DBI 后端窗口并返回 SwitchDesk，再执行主机管理操作"
        : closingApplication ? "软件正在退出，请等待当前会话结束" : "请先停止 USB 投屏，再执行主机管理操作";

    private Task DbiInstallAsync(bool launchOnConsole) => RunWithDbiModeAsync(launchStatus =>
    {
        dbiWindow = new(settings.DbiBackendPath, Path.Combine(settingsStore.Root, "Tools", "DBI"), path =>
        {
            var updated = settings with { DbiBackendPath = path };
            settingsStore.Save(updated); settings = updated;
        }, ResolveDbiSource) { Owner = this };
        if (launchStatus != null) dbiWindow.SetConsoleLaunchStatus(launchStatus);
        try { dbiWindow.ShowDialog(); }
        finally { dbiWindow = null; }
        return Task.CompletedTask;
    }, launchOnConsole);

    private async Task RunWithDbiModeAsync(Func<string?, Task> backendDialog, bool launchOnConsole)
    {
        if (!CanStartDbi()) throw new IOException("请先结束当前传输或投屏，再通过 DBI 安装");
        if (launchOnConsole && !CanLaunchConsoleDbi()) throw new IOException("请连接支持打开 DBI 的新版 SwitchDesk 配套，或在主机手动打开 DBI");
        var selectedPeer = client;
        string? launchStatus = null;
        dbiMode = true; var rootWasEnabled = Root.IsEnabled; Root.IsEnabled = false;
        CommandManager.InvalidateRequerySuggested(); RefreshCrossSaveAvailability();
        try
        {
            // Stop future USB discovery first, then wait for an in-flight query to release its handle.
            await jobsGate.WaitAsync(lifetime.Token);
            try
            {
                if (Volatile.Read(ref activeJobs) != 0) throw new IOException("后台任务正在结束，请稍后再通过 DBI 安装");
                if (launchOnConsole)
                {
                    if (selectedPeer != null && ReferenceEquals(client, selectedPeer) && !selectedPeer.IsBroken && vm.Online && device?.Capabilities.Contains("dbi.launch.v1") == true)
                        launchStatus = await ScheduleConsoleDbiAsync(selectedPeer);
                    else launchStatus = "主机连接已变化，请在 Switch 上手动打开 DBI。";
                    Log(launchStatus);
                }
                await DisconnectAsync();
            }
            finally { jobsGate.Release(); }
            vm.StreamGuide = false;
            SetConnectionMessage("DBI 模式 · SwitchDesk 管理连接已暂停");
            await backendDialog(launchStatus);
        }
        finally
        {
            dbiMode = false; Root.IsEnabled = rootWasEnabled;
            SetConnectionMessage(synced != null ? "离线记录 · 等待配套程序" : "等待配套程序");
            RefreshCrossSaveAvailability(); CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task<string> ScheduleConsoleDbiAsync(DeviceClient peer)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));
        try
        {
            var reply = await peer.QueryAsync<JsonElement>("dbi.launch", ct: timeout.Token);
            if (reply.ValueKind != JsonValueKind.Object || !reply.TryGetProperty("scheduled", out var scheduled) || scheduled.ValueKind != JsonValueKind.True ||
                !reply.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(path.GetString()))
                throw new InvalidDataException("配套返回的启动记录不完整");
            return $"主机配套已安排打开 {path.GetString()}。看到 DBI 后按 Y 进入后端模式。";
        }
        catch (DeviceException ex) { return "主机配套未能安排打开 DBI：" + ex.Message + "。可在 Switch 上手动打开 DBI。"; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or InvalidOperationException or KeyNotFoundException or FormatException)
        { return "未能确认主机是否已切换到 DBI，请查看 Switch 画面；如仍在配套界面，可手动打开 DBI。" + Environment.NewLine + ex.Message; }
    }

    private DbiBackendSource ResolveDbiSource(string path)
    {
        path = Path.GetFullPath(path);
        var first = MatchingDownloadReceipts(path, KnownDownloadReceipts()).FirstOrDefault();
        return new(path, first?.Size, first?.Sha256);
    }

    private ResourceDownload[] KnownDownloadReceipts() => phase2.List<ResourceDownload>("resource-downloads")
        .Concat(vm.Resources.Downloads.Select(row => row.Value)).Distinct().ToArray();

    private static ResourceDownload[] MatchingDownloadReceipts(string path, ResourceDownload[] known)
    {
        var receipts = known.Where(receipt => MatchesDownloadPath(receipt.Path, path)).ToArray();
        if (receipts.Length == 0) return receipts;
        if (receipts.Any(receipt => receipt.Size <= 0 || !Fingerprints.Valid(receipt.Sha256)))
            throw new InvalidDataException("该文件的下载记录缺少有效长度或 SHA-256，请重新核对下载记录");
        var first = receipts[0];
        if (receipts.Any(receipt => receipt.Size != first.Size || !receipt.Sha256.Equals(first.Sha256, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("该文件存在冲突的下载记录，请重新核对来源与文件");
        return receipts;
    }

    private static bool MatchesDownloadPath(string? candidate, string path)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        try { return Path.GetFullPath(candidate).Equals(path, StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return false; }
    }
}
