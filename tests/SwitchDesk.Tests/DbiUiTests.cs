using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using SwitchDesk.App;
using SwitchDesk.Core;

namespace SwitchDesk.Tests;

internal static partial class Program
{
    private static async Task RunDbiUiChecksAsync(MainWindow window, string directory)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        T Get<T>(string field) => (T)typeof(MainWindow).GetField(field, flags)!.GetValue(window)!;
        void Set(string field, object value) => typeof(MainWindow).GetField(field, flags)!.SetValue(window, value);
        object? Call(string method, params object[] args) => typeof(MainWindow).GetMethod(method, flags)!.Invoke(window, args);
        Task Handoff(Func<string?, Task> dialog, bool launch) => (Task)Call("RunWithDbiModeAsync", dialog, launch)!;
        Task InDbiMode(Func<Task> action) => Handoff(_ => action(), false);
        var vm = Get<ShellViewModel>("vm");
        Set("streaming", true); Assert(!vm.Commands["DbiInstall"].CanExecute(null), "DBI could start during streaming"); Set("streaming", false);
        Set("activeJobs", 1); Assert(!vm.Commands["DbiInstall"].CanExecute(null), "DBI could start during a managed transfer"); Set("activeJobs", 0);

        var backendPath = Path.Combine(directory, "dbibackend-fixture.exe");
        // A valid selection can still fail while persisting settings (locked disk,
        // full disk or access denied). Exercise the same continuation as the picker.
        foreach (var saveFailure in new Exception[] { new IOException("fixture settings locked"), new UnauthorizedAccessException("fixture settings denied") })
        {
            var failedSaveHost = new FakeDbiUiHost(backendPath);
            var failSave = true;
            string? storedBackend = null;
            var selectionDialog = new DbiInstallWindow("previous-backend.exe", Path.Combine(directory, "DBI-selection-cache"), path =>
            {
                if (failSave) throw saveFailure;
                storedBackend = path;
            }, host: failedSaveHost) { Owner = window, ShowActivated = false, ShowInTaskbar = false, Left = -16000, Top = -16000 };
            selectionDialog.Show();
            Task Select() => (Task)typeof(DbiInstallWindow).GetMethod("SelectBackendAsync", flags)!.Invoke(selectionDialog, [backendPath])!;
            await Select();
            Assert(!selectionDialog.CloseBlocked && !selectionDialog.SessionActive &&
                ((TextBlock)selectionDialog.FindName("StatusText")).Text.Contains(saveFailure.Message, StringComparison.Ordinal),
                "backend settings failure escaped the picker continuation or blocked the dialog");
            Assert(((TextBox)selectionDialog.FindName("BackendPath")).Text == "previous-backend.exe" && storedBackend == null,
                "backend selection appeared saved after settings persistence failed");
            failSave = false; await Select();
            Assert(storedBackend == backendPath && ((TextBox)selectionDialog.FindName("BackendPath")).Text == backendPath,
                "backend selection could not be retried after a settings failure");
            Assert(failedSaveHost.ResolveCalls == 0 && failedSaveHost.DownloadCalls == 0 && failedSaveHost.PrepareCalls == 0,
                "selecting a backend implicitly started or downloaded it");
            selectionDialog.Close();
            Assert(failedSaveHost.Disposed, "backend picker test left its service open");
        }
        var packagePath = Path.Combine(directory, "dbi-fixture.xci"); await File.WriteAllBytesAsync(packagePath, [1, 2, 3]);
        var resourcePath = Path.Combine(directory, "dbi-downloaded.nsp"); await File.WriteAllBytesAsync(resourcePath, [4, 5, 6]);
        var receipt = new ResourceDownload(new("下载记录夹具", ResourceKind.Game, "https://fixture.invalid/dbi.nsp", "dbi.nsp"), resourcePath, 3, Fingerprints.Of([4, 5, 6]), true, DateTimeOffset.UtcNow);
        var state = Get<Phase2State>("phase2"); state.Put("resource-downloads", receipt.Entry.Id, receipt);
        state.Put("resource-downloads", "dbi-unrelated-bad-path", receipt with { Path = "bad\0path" });
        var resourceSource = (DbiBackendSource)Call("ResolveDbiSource", resourcePath)!;
        Assert(resourceSource.ExpectedLength == receipt.Size && resourceSource.ExpectedSha256 == receipt.Sha256, "DBI handoff lost the recorded source digest or length");
        foreach (var invalid in new[] { receipt with { Size = 0 }, receipt with { Sha256 = null! } })
        {
            state.Put("resource-downloads", "dbi-invalid-matching-receipt", invalid);
            try { Call("ResolveDbiSource", resourcePath); throw new Exception("DBI accepted an invalid matching download receipt"); }
            catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException) { }
            var stored = state.Get<ResourceDownload>("resource-downloads", "dbi-invalid-matching-receipt") ?? throw new Exception("missing fixture receipt");
            state.Remove("resource-downloads", "dbi-invalid-matching-receipt", stored.Revision);
        }

        await (Task)Call("DisconnectAsync")!;
        await using var attemptPeer = new Peer();
        var attemptClient = await attemptPeer.ConnectAsync();
        var openStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<CancellationToken, Task<DeviceClient>> open = async ct => { openStarted.TrySetResult(); await releaseOpen.Task.WaitAsync(ct); return attemptClient; };
        var attempt = (Task<DeviceInfo?>)Call("ConnectManagedCandidateAsync", open, CancellationToken.None)!;
        await openStarted.Task;
        var backendEntered = false;
        var handoff = InDbiMode(async () =>
        {
            backendEntered = true;
            Assert(attemptClient.IsBroken, "DBI opened before the in-flight management connection was disposed");
            Assert(Get<bool>("dbiMode") && Get<DeviceClient?>("client") == null && !vm.Online,
                "DBI handoff kept the management USB connection");
            Assert(!vm.Commands["DbiInstall"].CanExecute(null) && !vm.Commands["Stream"].CanExecute(null) && !vm.Commands["Mkdir"].CanExecute(null),
                "DBI mode allowed a competing management or streaming session");
            try { Call("RequireDevice", "files"); throw new Exception("DBI mode allowed USB management"); }
            catch (TargetInvocationException ex) when (ex.InnerException is IOException error)
            { Assert(error.Message.Contains("DBI", StringComparison.Ordinal), "DBI pause incorrectly told the user to stop streaming"); }

            var fake = new FakeDbiUiHost(backendPath); string? savedPath = null;
            var dialog = new DbiInstallWindow("", Path.Combine(directory, "DBI-cache"), path => savedPath = path,
                path => (DbiBackendSource)Call("ResolveDbiSource", path)!, fake)
            { Owner = window, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -16000, Top = -16000 };
            dialog.Show(); dialog.AddFiles([packagePath, resourcePath]);
            Assert(fake.ResolveCalls == 0 && fake.DownloadCalls == 0 && fake.PrepareCalls == 0, "opening DBI or selecting files downloaded or started a backend");
            await dialog.DownloadAsync();
            Assert(fake.DownloadCalls == 1 && fake.PrepareCalls == 0 && savedPath == backendPath, "explicit backend download started an install or lost the selected path");
            var running = dialog.StartAsync(); await fake.Run.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert(fake.Sources.Length == 2 && fake.Sources.Single(source => source.Path == resourcePath).ExpectedSha256 == receipt.Sha256,
                "DBI UI did not pass the registered download constraints");
            Assert(dialog.SessionActive && dialog.CloseBlocked && !((Button)dialog.FindName("StartBackend")).IsEnabled, "a running backend could be launched twice");
            dialog.Close(); Assert(dialog.IsVisible && !fake.Run.Disposed, "closing the DBI window released an active backend session");
            var closing = new CancelEventArgs(); Call("OnClosing", window, closing);
            Assert(closing.Cancel && !Get<bool>("closingApplication"), "the main app closed while the backend still owned source files");
            Assert(Get<bool>("dbiMode"), "closing the dialog resumed background USB discovery before backend exit");
            fake.Run.Exit.TrySetResult(); await running;
            Assert(fake.Run.Disposed && !dialog.CloseBlocked && !dialog.SessionActive, "backend exit did not release its source-file session");
            Assert(((TextBlock)dialog.FindName("StatusText")).Text == "DBI 会话已结束，请在主机核对安装结果。", "backend exit was reported as a successful installation");
            fake.Run = new() { ExitError = new IOException("fixture backend failed") };
            var failed = dialog.StartAsync(); await fake.Run.Started.Task; fake.Run.Exit.TrySetResult(); await failed;
            Assert(fake.Run.Disposed && !dialog.CloseBlocked && ((TextBlock)dialog.FindName("StatusText")).Text.Contains("fixture backend failed", StringComparison.Ordinal),
                "abnormal backend exit did not release its session or report the failure");
            fake.DownloadError = new HttpRequestException("fixture network failed"); await dialog.DownloadAsync();
            Assert(!dialog.CloseBlocked && ((TextBlock)dialog.FindName("StatusText")).Text.Contains("fixture network failed", StringComparison.Ordinal),
                "backend download failure escaped the inline error flow");
            fake.DownloadError = new InvalidDataException("fixture invalid backend digest"); await dialog.DownloadAsync();
            Assert(!dialog.CloseBlocked && ((TextBlock)dialog.FindName("StatusText")).Text.Contains("fixture invalid backend digest", StringComparison.Ordinal),
                "backend validation error escaped the inline error flow");
            fake.PrepareError = new InvalidDataException("fixture package changed"); await dialog.StartAsync();
            Assert(!dialog.SessionActive && !dialog.CloseBlocked && ((TextBlock)dialog.FindName("StatusText")).Text.Contains("fixture package changed", StringComparison.Ordinal),
                "package validation error left a running session or escaped the inline error flow");
            dialog.Close(); Assert(fake.Disposed, "closed DBI dialog did not dispose its download service");
        });
        Assert(Get<bool>("dbiMode") && !backendEntered && !attempt.IsCompleted, "DBI did not wait for the in-flight management handshake");
        releaseOpen.TrySetResult(); await handoff;
        Assert(await attempt == null, "an in-flight handshake reconnected management after DBI mode began");
        Assert(!Get<bool>("dbiMode") && ((FrameworkElement)window.FindName("Root")).IsEnabled && vm.Commands["DbiInstall"].CanExecute(null),
            "DBI handoff did not restore the offline entry or main window");

        await Throws<IOException>(() => InDbiMode(() => throw new IOException("fixture dialog failed")));
        Assert(!Get<bool>("dbiMode") && vm.Commands["DbiInstall"].CanExecute(null), "DBI dialog failure permanently paused management");
        var launchButton = (Button)window.FindName("HeaderDbiLaunch");
        var launchCommand = vm.Commands["DbiLaunch"];
        Assert(launchButton.Command == launchCommand && !launchCommand.CanExecute(null), "offline console launcher was enabled or unbound");
        foreach (var mode in new[] { "legacy", "manual", "scheduled", "missing", "lost" })
        {
            var info = Peer.Info with { Capabilities = mode == "legacy" ? Peer.Info.Capabilities : [.. Peer.Info.Capabilities, "dbi.launch.v1"] };
            await using var peer = new Peer { DbiLaunchMode = mode };
            var connection = await peer.ConnectAsync();
            Set("client", connection); Set("device", info); vm.Online = true;
            Assert(launchCommand.CanExecute(null) == (mode != "legacy"), "console launch capability gate mismatch");
            var launch = mode is "scheduled" or "missing" or "lost";
            if (mode == "legacy") await Throws<IOException>(() => Handoff(_ => Task.CompletedTask, true));
            await Handoff(status =>
            {
                Assert(connection.IsBroken && !vm.Online && Get<DeviceClient?>("client") == null, "backend dialog opened before management USB released");
                var expected = mode switch { "scheduled" => "已安排打开 /switch/DBI/DBI.nro", "missing" => "DbiNotFound", "lost" => "未能确认", _ => null };
                Assert(expected == null ? status == null : status?.Contains(expected, StringComparison.Ordinal) == true, "incorrect console launch status: " + status);
                return Task.CompletedTask;
            }, launch);
            Assert(peer.DbiLaunchRequests == (launch ? 1 : 0) && peer.InstalledNames.Count == 0 && peer.WriteOperations == 0,
                "launch was sent implicitly, retried or performed an installation");
        }
    }

    private sealed class FakeDbiUiHost(string backendPath) : IDbiBackendHost
    {
        public int ResolveCalls, DownloadCalls, PrepareCalls;
        public bool Disposed;
        public Exception? DownloadError, PrepareError;
        public DbiBackendSource[] Sources = [];
        public FakeDbiUiRun Run { get; set; } = new();
        public Task<string> ResolveAsync(string path, string cache, IProgress<TransferProgress>? progress, CancellationToken ct)
        { ResolveCalls++; return Task.FromResult(backendPath); }
        public Task<string> DownloadOfficialAsync(string cache, IProgress<TransferProgress>? progress, CancellationToken ct)
        { DownloadCalls++; return DownloadError == null ? Task.FromResult(backendPath) : Task.FromException<string>(DownloadError); }
        public Task<IDbiBackendRun> PrepareAsync(string backend, IEnumerable<DbiBackendSource> sources, CancellationToken ct)
        { PrepareCalls++; Sources = sources.ToArray(); return PrepareError == null ? Task.FromResult<IDbiBackendRun>(Run) : Task.FromException<IDbiBackendRun>(PrepareError); }
        public void Dispose() => Disposed = true;
    }
    private sealed class FakeDbiUiRun : IDbiBackendRun
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Exit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed;
        public Exception? ExitError;
        public void Start() => Started.TrySetResult();
        public async Task WaitForExitAsync(CancellationToken ct) { await Exit.Task.WaitAsync(ct); if (ExitError != null) throw ExitError; }
        public async ValueTask DisposeAsync() { await Exit.Task; Disposed = true; }
    }
}
