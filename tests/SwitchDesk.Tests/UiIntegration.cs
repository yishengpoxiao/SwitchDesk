using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SwitchDesk.App;
using SwitchDesk.Core;

namespace SwitchDesk.Tests;
internal static partial class Program
{
    // Executes the actual window commands on a dispatcher with an isolated data directory
    // and the framed protocol fixture. This never enumerates or talks to USB hardware.
    private static Task RunUiIntegrationAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var app = new SwitchDesk.App.App { IsTestHost = true }; app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            var directory = Path.Combine(root, "wpf-integration"); var window = new MainWindow(directory, true);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            T Get<T>(string field) => (T)typeof(MainWindow).GetField(field, flags)!.GetValue(window)!;
            void Set(string field, object value) => typeof(MainWindow).GetField(field, flags)!.SetValue(window, value);
            object? Call(string name, params object[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, args);
            async Task Invoke(string name, params object[] args) => await (Task)Call(name, args)!;
            async Task FlushUi() { await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout(); }
            async Task WaitForCrossFiles()
            {
                for (var attempt = 0; attempt < 8; attempt++)
                {
                    var discovery = Get<Task>("crossFileDiscoveryTask");
                    await discovery.WaitAsync(TimeSpan.FromSeconds(5)); await FlushUi();
                    if (ReferenceEquals(discovery, Get<Task>("crossFileDiscoveryTask"))) return;
                }
                throw new Exception("cross-save file discovery did not settle");
            }
            window.Show();
            _ = Dispatcher.CurrentDispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    Assert(app.Windows.OfType<MainWindow>().Count() == 1, "test startup created a normal USB-monitoring window");
                    var copyError = new TextBlock { Text = "长错误：" + new string('错', 1000), TextTrimming = TextTrimming.CharacterEllipsis, Width = 40 };
                    Assert(TextCopy.TextAt(copyError) == copyError.Text, "copy extracted a visually clipped error");
                    Assert(TextCopy.KeyboardText(new TextBox { Text = "输入框自己的选择" }) == null &&
                        TextCopy.KeyboardText(new PasswordBox()) == null && TextCopy.KeyboardText(new DataGrid()) == null,
                        "global copy intercepted an editor or DataGrid's own command");
                    var copyList = new ListBox { SelectionMode = SelectionMode.Extended };
                    copyList.Items.Add("第一条完整版本+0123456789ab"); copyList.Items.Add("第二条错误\n完整下一行");
                    copyList.SelectedItems.Add(copyList.Items[0]); copyList.SelectedItems.Add(copyList.Items[1]);
                    Assert(TextCopy.KeyboardText(copyList) == string.Join(Environment.NewLine, copyList.Items.Cast<string>()), "list copy lost a selected row or newline");
                    const string crossTitleId = "010089A0197E4000";
                    var crossSaveA = Peer.Save with { Key = "cross-user-a", TitleId = crossTitleId, GameName = "Vampire Survivors", UserName = "跨平台用户 A" };
                    var crossSaveB = crossSaveA with { Key = "cross-user-b", UserId = "USER-B", UserName = "跨平台用户 B" };
                    await using var peer = new Peer
                    {
                        Titles = [new(Peer.Save.TitleId, Peer.Save.GameName, "Fixture", "1", "已安装", [new(Peer.Save.TitleId, "Base", 0, "SdCard", 1024)]),
                            new(crossTitleId, crossSaveA.GameName, "Fixture", "1", "已安装", [new(crossTitleId, "Base", 0, "SdCard", 1024)])],
                        SaveRecords = [Peer.Save, crossSaveA, crossSaveB]
                    };
                    var client = await peer.ConnectAsync(); var vm = Get<ShellViewModel>("vm");
                    Set("client", client); Set("device", Peer.Info); vm.Online = true;
                    await Invoke("RefreshDeviceAsync", CancellationToken.None);
                    vm.Library.Selected = vm.Library.All.Single(x => x.TitleId == Peer.Save.TitleId);
                    Call("UpdateDevice", Peer.Info); vm.Connection = "协议夹具 · 非实机连接";
                    Set("streaming", true); Set("activeStreamBridge", true);
                    Assert(vm.Commands["Mkdir"].CanExecute(null), "Bridge streaming disabled USB management commands");
                    await Invoke("RefreshDeviceAsync", CancellationToken.None);
                    Set("activeStreamBridge", false);
                    Assert(!vm.Commands["Mkdir"].CanExecute(null), "USB streaming left management commands enabled");
                    await Throws<IOException>(async () =>
                    {
                        try { await Invoke("RefreshDeviceAsync", CancellationToken.None); }
                        catch (TargetInvocationException ex) when (ex.InnerException is IOException error) { throw error; }
                    });
                    Set("streaming", false);
                    var localResource = Path.Combine(directory, "resource-source"); Directory.CreateDirectory(localResource);
                    var resourceBytes = new byte[] { 1, 2, 3 };
                    await File.WriteAllBytesAsync(Path.Combine(localResource, "resource-fixture.nsp"), resourceBytes);
                    var resourceSource = new ContentSource("resource-ui", "资源下载协议夹具", ContentSourceKind.Local, localResource);
                    var resourceIndex = new ContentIndex(1, [new(Peer.Save.TitleId, Peer.Save.TitleId, "Base", 0, resourceBytes.Length, Fingerprints.Of(resourceBytes), "resource-fixture.nsp")]);
                    await File.WriteAllBytesAsync(Path.Combine(localResource, "index.json"), System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(resourceIndex, Wire.Json));
                    var state = Get<Phase2State>("phase2"); state.Put("content-sources", resourceSource.Id, resourceSource);
                    vm.Commands["Navigate"].Execute("resources");
                    vm.Resources.SelectedSource = vm.Resources.Sources.Single(s => s.Id == "content-sources");
                    await Invoke("SearchResourcesAsync"); await FlushUi();
                    Assert(vm.Page == "resources" && ((FrameworkElement)window.FindName("ResourcesPage")).IsVisible && vm.Resources.Results.Count == 1);
                    Assert(vm.Resources.SelectedResult?.Entry?.TitleId == Peer.Save.TitleId && vm.Commands["ResourceDownload"].CanExecute(null));
                    foreach (var changeType in new[] { false, true })
                    {
                        var searchGate = Get<SemaphoreSlim>("jobsGate"); await searchGate.WaitAsync();
                        Task search;
                        try
                        {
                            search = Invoke("SearchResourcesAsync");
                            Assert(vm.Resources.IsBusy);
                            if (changeType) vm.Resources.SelectedType = "游戏";
                            else vm.Resources.SearchText = "changed while searching";
                        }
                        finally { searchGate.Release(); }
                        await search;
                        Assert(!vm.Resources.IsBusy && vm.Resources.Status.Contains("搜索条件已改变", StringComparison.Ordinal), "superseded search left a permanent searching status");
                        vm.Resources.SearchText = ""; vm.Resources.SelectedType = "全部类型";
                    }
                    var badRoot = Path.Combine(directory, "resource-source-invalid"); Directory.CreateDirectory(badRoot);
                    await File.WriteAllTextAsync(Path.Combine(badRoot, "index.json"), "{\"formatVersion\":999,\"entries\":[]}");
                    var badSource = resourceSource with { Id = "invalid-source", Name = "损坏索引夹具", Root = badRoot };
                    state.Put("content-sources", badSource.Id, badSource);
                    await Invoke("SearchResourcesAsync");
                    Assert(vm.Resources.Results.Count == 1 && vm.Resources.Status.Contains("1 个来源读取失败", StringComparison.Ordinal), "one invalid source hid the healthy source's resources");
                    var previousCatalog = state.Get<SourceCatalog>("source-catalogs", resourceSource.Id)!.Value.Value;
                    await File.WriteAllTextAsync(Path.Combine(localResource, "index.json"), "{\"formatVersion\":999,\"entries\":[]}");
                    await Invoke("SearchResourcesAsync");
                    Assert(vm.Resources.Results.Count == 0 && vm.Resources.EmptyHeading == "暂时无法在应用内读取此来源", "failed sources were reported as a valid empty search");
                    Assert(state.Get<SourceCatalog>("source-catalogs", resourceSource.Id)!.Value.Value.FetchedAt == previousCatalog.FetchedAt, "failed refresh replaced the successful catalog timestamp");
                    await File.WriteAllBytesAsync(Path.Combine(localResource, "index.json"), System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(resourceIndex, Wire.Json));
                    state.Remove("content-sources", badSource.Id, state.Get<ContentSource>("content-sources", badSource.Id)!.Value.Revision);
                    await Invoke("SearchResourcesAsync");
                    await Invoke("DownloadResourceAsync"); await FlushUi();
                    var receipt = vm.Resources.SelectedDownload!.Value;
                    Assert(receipt.ExpectedContent?.ComponentId == Peer.Save.TitleId && receipt.ContentSourceId == resourceSource.Id, "owned receipt lost its component constraint");
                    Assert((await File.ReadAllBytesAsync(receipt.Path)).SequenceEqual(resourceBytes) && receipt.ExpectedVerified && peer.InstalledNames.Count == 0 && peer.WriteOperations == 0,
                        "downloading a resource contacted or wrote to the console");
                    await Invoke("DownloadResourceAsync"); Assert(vm.Resources.Downloads.Count == 1, "verified cached resource was not reusable");
                    var secondRoot = Path.Combine(directory, "resource-source-two"); Directory.CreateDirectory(secondRoot);
                    await File.WriteAllBytesAsync(Path.Combine(secondRoot, "resource-fixture.nsp"), resourceBytes);
                    await File.WriteAllBytesAsync(Path.Combine(secondRoot, "index.json"), System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(resourceIndex, Wire.Json));
                    var secondSource = resourceSource with { Id = "resource-ui-two", Name = "资源第二来源", Root = secondRoot };
                    state.Put("content-sources", secondSource.Id, secondSource);
                    await Invoke("SearchResourcesAsync");
                    vm.Resources.SelectedResult = vm.Resources.Results.Single(r => r.Entry!.SourceName == secondSource.Name);
                    await Invoke("DownloadResourceAsync");
                    Assert(vm.Resources.Downloads.Count == 2 && vm.Resources.SelectedDownload!.Path == receipt.Path &&
                        vm.Resources.SelectedDownload.Value.Entry.Id != receipt.Entry.Id, "shared cache path lost source identity");
                    vm.Resources.SelectedResult = vm.Resources.Results.Single(r => r.Entry!.Id == receipt.Entry.Id);
                    var secondSourceRecord = state.Get<ContentSource>("content-sources", secondSource.Id)!.Value;
                    state.Remove("content-sources", secondSource.Id, secondSourceRecord.Revision);
                    var manualReceipt = receipt with { Entry = receipt.Entry with { DownloadUrl = "https://fixture.invalid/resource-fixture.nsp", SourceName = "直链夹具" }, ExpectedContent = null, ContentSourceId = null };
                    vm.Resources.SetDownloads([manualReceipt]);
                    await File.WriteAllBytesAsync(receipt.Path, [9, 9, 9]);
                    await Throws<InvalidDataException>(() => Invoke("InstallResourceAsync"));
                    Assert(peer.InstalledNames.Count == 0 && peer.WriteOperations == 0, "changed local resource reached installation");
                    await Invoke("ReviewInstallAsync", (object)new[] { receipt.Path });
                    Assert(Get<ObservableCollection<InstallEntry>>("installEntries").Single().Source == null &&
                        !((Button)window.FindName("StartInstall")).IsEnabled, "picking a changed download as a local file bypassed its registered digest");
                    await File.WriteAllBytesAsync(receipt.Path, resourceBytes);
                    await Invoke("InstallResourceAsync");
                    Assert(Get<ObservableCollection<InstallEntry>>("installEntries").Single().Source != null && peer.InstalledNames.Count == 0, "resource install did not stop at review");
                    ((Border)window.FindName("InstallOverlay")).Visibility = Visibility.Collapsed;
                    vm.Resources.SetDownloads([manualReceipt with { Entry = manualReceipt.Entry with { TitleId = "0100000000000099" } }]);
                    await Invoke("InstallResourceAsync");
                    Assert(Get<ObservableCollection<InstallEntry>>("installEntries").Single().Source == null && !((Button)window.FindName("StartInstall")).IsEnabled,
                        "resource title identity mismatch enabled installation");
                    await Invoke("InspectInstallEntriesAsync");
                    Assert(Get<ObservableCollection<InstallEntry>>("installEntries").Single().Source == null && !((Button)window.FindName("StartInstall")).IsEnabled,
                        "reinspection discarded the original resource identity constraint");
                    await Invoke("ExecuteInstallAsync", false);
                    Assert(peer.InstalledNames.Count == 0, "reinspection bypass installed a mismatching resource");
                    vm.Resources.SetDownloads([manualReceipt]); await Invoke("InstallResourceAsync");
                    var resourceStamp = File.GetLastWriteTimeUtc(receipt.Path);
                    await File.WriteAllBytesAsync(receipt.Path, [9, 9, 9]); File.SetLastWriteTimeUtc(receipt.Path, resourceStamp);
                    await Invoke("ExecuteInstallAsync", false);
                    Assert(peer.InstalledNames.Count == 0, "same-size same-time modified resource bypassed the reviewed receipt");
                    await File.WriteAllBytesAsync(receipt.Path, resourceBytes);
                    var component = new TitleComponent(Peer.Save.TitleId, "Base", 65536, "SdCard", 1024)
                    { ApplicationId = Peer.Save.TitleId, RequiredSystemVersion = 0, RequiredApplicationVersion = 0 };
                    peer.PackageOverride = new("fixture", 1024, [component], Peer.Save.TitleId, "资源包夹具", true);
                    ((System.Collections.IDictionary)Get<object>("ownedResourceEntries")).Clear();
                    var reopened = System.Text.Json.JsonSerializer.Deserialize<ResourceDownload>(System.Text.Json.JsonSerializer.Serialize(receipt, Wire.Json), Wire.Json)!;
                    vm.Resources.SetDownloads([reopened]); await Invoke("InstallResourceAsync"); await Invoke("InspectInstallEntriesAsync");
                    Assert(Get<ObservableCollection<InstallEntry>>("installEntries").Single().Source == null && peer.InstalledNames.Count == 0,
                        "reopened receipt lost its original component version constraint");
                    await Invoke("ReviewInstallAsync", (object)new[] { receipt.Path });
                    Assert(Get<ObservableCollection<InstallEntry>>("installEntries").Single().Source == null &&
                        !((Button)window.FindName("StartInstall")).IsEnabled, "dragging a recorded download into the local installer bypassed its component constraint");
                    peer.PackageOverride = peer.PackageOverride with { Components = [component with { Version = 0 }, component with { ApplicationId = "0100000000000099" }] };
                    vm.Resources.SetDownloads([manualReceipt]); await Invoke("InstallResourceAsync");
                    Assert(Get<ObservableCollection<InstallEntry>>("installEntries").Single().Source == null, "mixed owners bypassed declared resource TitleID");
                    peer.PackageOverride = null;
                    ((Border)window.FindName("InstallOverlay")).Visibility = Visibility.Collapsed;
                    var registered = state.Get<ContentSource>("content-sources", resourceSource.Id)!.Value;
                    state.Remove("content-sources", resourceSource.Id, registered.Revision);
                    await Throws<IOException>(() => Invoke("DownloadResourceAsync"));
                    await ResourceRetryUiTestsAsync(window);
                    var cheatBytes = System.Text.Encoding.UTF8.GetBytes("[Fixture]\n04000000 00000000 00000001\n");
                    var cheatFile = Path.Combine(directory, "0123456789abcdef.txt"); await File.WriteAllBytesAsync(cheatFile, cheatBytes);
                    var cheatResource = new ResourceEntry("金手指身份夹具", ResourceKind.Cheat, "https://fixture.invalid/0123456789abcdef.txt", "0123456789abcdef.txt")
                    { TitleId = Peer.Save.TitleId, BuildId = "FEDCBA9876543210" };
                    var cheatReceipt = new ResourceDownload(cheatResource, cheatFile, cheatBytes.Length, Fingerprints.Of(cheatBytes), false, DateTimeOffset.UtcNow);
                    vm.Resources.SetDownloads([cheatReceipt]);
                    await Throws<InvalidDataException>(() => Invoke("InstallResourceAsync"));
                    Assert(peer.WriteOperations == 0 && peer.InstalledNames.Count == 0, "mismatching cheat BuildID reached the device");
                    vm.Resources.SetDownloads([cheatReceipt with { Entry = cheatResource with { BuildId = null } }]);
                    Assert(!vm.Commands["ResourceInstall"].CanExecute(null), "missing cheat identity enabled installation");
                    vm.Resources.SetDownloads([cheatReceipt with { Entry = cheatResource with { BuildId = "0123456789ABCDEF" } }]);
                    Set("device", Peer.Info with { Capabilities = Peer.Info.Capabilities.Where(c => c != "cheats.text.v1").ToArray() });
                    Assert(!vm.Commands["ResourceInstall"].CanExecute(null), "legacy companion enabled standard TXT writes");
                    Set("device", Peer.Info);
                    vm.Resources.SetDownloads([receipt]); vm.Online = false;
                    Assert(!vm.Commands["ResourceInstall"].CanExecute(null) && vm.Commands["ResourceDownload"].CanExecute(null), "offline resource flow exposed installation or blocked PC downloads");
                    vm.Online = true; Call("Navigate", "library");
                    var source = new ContentSource("ui-fixture", "自有来源 · 界面夹具", ContentSourceKind.Http, "https://fixture.invalid/library/");
                    var entry = new ContentEntry(Peer.Save.TitleId, "0100000000000800", "Update", 131072, 1024, Fingerprints.Of([1]), "fixture.nsp");
                    vm.UpdateOffers = [new(new(new(Peer.Save.TitleId, entry.ComponentId, "Update", entry.Version), 65536, UpdateStatus.UpdateAvailable), source, entry)]; vm.SelectedOffer = vm.UpdateOffers[0];
                    vm.UpdateSummary = "界面测试目录 · 非真实更新信息"; vm.ContentSources = [source];
                    await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout();
                    var list = Walk<ListBox>((DependencyObject)window.Content).Single(x => ReferenceEquals(x.ItemsSource, vm.UpdateOffers)); list.BringIntoView();
                    await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout();
                    Assert(Walk<CheckBox>(list).Any(c => c.IsChecked == true && c.Content?.ToString()?.Contains("131072", StringComparison.Ordinal) == true), "update review checkbox or label did not render");
                    Assert(!vm.Commands["InstallOffer"].CanExecute(null), "legacy companion enabled managed install without capability");
                    if (((Border)window.FindName("FixtureBanner")).Child is TextBlock banner) banner.Text = "界面测试数据 · 2 项协议夹具 · 非实机连接";
                    window.UpdateLayout();
                    var visual = (FrameworkElement)window.Content; var bitmap = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var image = File.Create(Path.Combine(directory, "updates-fixture.png"))) encoder.Save(image);
                    var paths = new[] { "one.nsp", "two.nsz", "three.xci" }.Select(x => Path.Combine(directory, x)).ToArray();
                    foreach (var path in paths) await File.WriteAllBytesAsync(path, [1, 2, 3]);
                    var compatibility = (CheckBox)window.FindName("InstallHashCompatibility");
                    Assert(compatibility.IsChecked == true && compatibility.Content?.ToString()?.Contains("签名与摘要", StringComparison.Ordinal) == true,
                        "manual review did not explain and default to signature and checksum compatibility");
                    var reviewTask = Invoke("ReviewInstallAsync", (object)paths);
                    Assert(!compatibility.IsEnabled, "compatibility policies could change during package inspection");
                    await reviewTask;
                    var entries = Get<ObservableCollection<InstallEntry>>("installEntries");
                    Assert(entries.Count == 3 && entries.All(e => e.Source != null) && peer.InstalledNames.Count == 0, "review must not start installation");
                    var installedWarning = new InstallWarning("NcaHashMismatch", "converted.nca", new string('1', 32), new string('2', 64));
                    var installedSignatureWarning = new InstallWarning("NcaHeaderSignatureMismatch", "converted.nca", "", "")
                    { HeaderSha256 = new string('3', 64), VerificationResult = "00000000000081F9" };
                    bool HasBothWarnings(string text) => text.Contains(installedWarning.Message, StringComparison.Ordinal) &&
                        text.Contains(installedSignatureWarning.Message, StringComparison.Ordinal);
                    peer.InstallationWarnings = [installedWarning, installedSignatureWarning];
                    peer.FailInstallation = "two.nsz"; var installTask = Invoke("ExecuteInstallAsync", false);
                    Assert(!compatibility.IsEnabled, "compatibility policies could change during a batch");
                    await installTask;
                    Assert(peer.InstalledNames.SequenceEqual(new[] { "one.nsp" }) && entries[1].State.StartsWith("失败", StringComparison.Ordinal) && entries[2].State != "完成", "failure must stop the batch");
                    Assert(entries[0].IsCompleted && entries[0].HasWarnings && entries[0].WarningCount == 2 && !entries[0].IsPending, "a completed warning item remained pending after a later failure");
                    Assert(HasBothWarnings(entries[0].WarningText), "installation warning lost its full checksum or signature details");
                    await FlushUi();
                    Assert(HasBothWarnings(vm.Tasks.Items.First(task => task.Name.StartsWith("批量安装", StringComparison.Ordinal)).StateCopyText),
                        "a later failure hid completed warnings from task history copying");
                    peer.FailInstallation = null; await Invoke("ExecuteInstallAsync", true);
                    Assert(peer.InstalledNames.SequenceEqual(new[] { "one.nsp", "three.xci" }) && entries[1].State == "已跳过", "skip resumes without reinstalling completed content");
                    await FlushUi();
                    Assert(entries[2].IsCompleted && peer.RequestedNcaPolicies.All(policy => policy == "warn") && peer.RequestedSignaturePolicies.All(policy => policy == "warn"),
                        "manual install did not request both compatible warning policies");
                    Assert(((Border)window.FindName("InstallWarningsPanel")).IsVisible && ((TextBlock)window.FindName("InstallWarningText")).Text == entries[2].WarningText,
                        "completed warning details were not retained on the visible review page");
                    Assert(HasBothWarnings(vm.Tasks.Items.First(task => task.Name.StartsWith("批量安装", StringComparison.Ordinal)).StateCopyText),
                        "completed task history lost its copyable warning details");
                    peer.InstallationWarnings = [];
                    foreach (var missingCapability in new[] { "install.signature-warning.v1", "install.hash-warning.v1" })
                    {
                        var legacy = Peer.Info with { Capabilities = Peer.Info.Capabilities.Where(c => c != missingCapability).ToArray() };
                        peer.DeviceOverride = legacy; Set("device", legacy);
                        var legacyName = missingCapability.Contains("signature", StringComparison.Ordinal) ? "legacy-signature.nsp" : "legacy-hash.nsp";
                        var legacyPath = Path.Combine(directory, legacyName); await File.WriteAllBytesAsync(legacyPath, [1, 2, 3]);
                        await Invoke("ReviewInstallAsync", (object)new[] { legacyPath });
                        var installedBefore = peer.InstalledNames.Count; var requestsBefore = peer.RequestedNcaPolicies.Count;
                        await Invoke("ExecuteInstallAsync", false);
                        Assert(peer.InstalledNames.Count == installedBefore && peer.RequestedNcaPolicies.Count == requestsBefore &&
                            ((TextBlock)window.FindName("InstallError")).Text.Contains("更新配套 NRO", StringComparison.Ordinal),
                            "legacy companion silently ignored a compatible install policy");
                        Assert(compatibility.IsEnabled, "compatibility policies were not enabled after a failed batch");
                        compatibility.IsChecked = false; await Invoke("ExecuteInstallAsync", false);
                        Assert(peer.InstalledNames.Last() == legacyName && peer.RequestedNcaPolicies.Last() == "strict" &&
                            peer.RequestedSignaturePolicies.Last() == "strict" && entries.Single().IsCompleted,
                            "strict mode no longer supported legacy companion installation");
                        compatibility.IsChecked = true;
                    }
                    peer.DeviceOverride = null; Set("device", Peer.Info);
                    ((Border)window.FindName("InstallOverlay")).Visibility = Visibility.Collapsed;
                    vm.Library.Selected = vm.Library.All.Single(x => x.TitleId == Peer.Save.TitleId);
                    var backup = (Task<Snapshot>)Call("BackupAsync", Peer.Save)!; var first = await backup;
                    var duplicate = await (Task<Snapshot>)Call("BackupAsync", Peer.Save)!; Assert(first.Path == duplicate.Path, "normal backup completes without confirmation and deduplicates");
                    Call("SetAutoPolicy", true); Assert(Get<AppSettings>("settings").AutoBackupKeys.Length == 1);
                    await FlushUi();
                    vm.Library.Selected = vm.Library.All.Single(x => x.TitleId == crossTitleId);
                    vm.Commands["CrossSaveOpen"].Execute(null); await WaitForCrossFiles();
                    Assert(vm.Page == "cross-save" && vm.CrossSave.SelectedGame?.AdapterId == "vampire-survivors" && vm.CrossSave.Users.Length == 2, "associated game did not open the independent cross-save page");
                    Assert(vm.CrossSave.SwitchFiles.Order(StringComparer.Ordinal).SequenceEqual(new[] { "main.dat", "nested/config.bin" }) && vm.CrossSave.SelectedSwitchFile == null, "cross-save must discover both real tree files before the first comparison");
                    Assert(peer.ActiveHandleCount == 0 && vm.CrossSave.CanChooseFile, "directory discovery left a handle open or the file selector disabled");

                    // Hold both responses so the old user's result cannot be hidden by
                    // an immediately completed replacement request.
                    var staleEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var currentEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var releaseStale = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var releaseCurrent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var treeAttempt = 0;
                    peer.BeforeHandleTree = async () =>
                    {
                        var attempt = Interlocked.Increment(ref treeAttempt);
                        if (attempt == 1) { staleEntered.TrySetResult(); await releaseStale.Task; }
                        else if (attempt == 2) { currentEntered.TrySetResult(); await releaseCurrent.Task; }
                    };
                    try
                    {
                        vm.CrossSave.SelectedUser = vm.CrossSave.Users.Single(x => x.UserId == crossSaveB.UserId);
                        var staleDiscovery = Get<Task>("crossFileDiscoveryTask"); await staleEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        vm.CrossSave.SelectedUser = vm.CrossSave.Users.Single(x => x.UserId == crossSaveA.UserId);
                        releaseStale.TrySetResult(); await staleDiscovery.WaitAsync(TimeSpan.FromSeconds(5));
                        await currentEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        Assert(vm.CrossSave.SelectedUser?.UserId == crossSaveA.UserId && vm.CrossSave.SwitchFiles.Length == 0 && vm.CrossSave.FilesLoading, "a stale user's tree filled the replacement user's selector");
                        releaseCurrent.TrySetResult(); await WaitForCrossFiles();
                    }
                    finally { releaseStale.TrySetResult(); releaseCurrent.TrySetResult(); peer.BeforeHandleTree = null; }
                    Assert(peer.ActiveHandleCount == 0 && !client.IsBroken, "changing cross-save users broke the connection or leaked a handle");
                    var pcSlot = Path.Combine(directory, "user1.dat"); var pcBytes = KnightSaveAdapter.EncodePc(KnightJson(4)); await File.WriteAllBytesAsync(pcSlot, pcBytes);
                    var candidate = new PcSaveCandidate("vampire-survivors", pcSlot, "Windows fixture", "1", "isolated unsupported test slot"); vm.CrossSave.Candidates = [candidate]; vm.CrossSave.SelectedPc = candidate; vm.CrossSave.SelectedSwitchFile = "main.dat";
                    Assert(vm.Commands["CrossSaveRead"].CanExecute(null), "discovered file selection did not enable comparison");
                    var originalRemote = peer.Files.ToDictionary(x => x.Key, x => x.Value.ToArray()); var compareOpens = peer.ReadOpens;
                    await Invoke("ReadCrossSaveAsync"); await FlushUi();
                    Assert(vm.CrossSave.Comparison.Contains("首次配对", StringComparison.Ordinal) && !vm.Commands["CrossSaveToSwitch"].CanExecute(null) && !vm.Commands["CrossSaveToPc"].CanExecute(null), "unadmitted cross-save must remain backup-only");
                    Assert(peer.ReadOpens == compareOpens + 1 && peer.ActiveHandleCount == 0, "one comparison must back up once and close the save handle");
                    Assert(peer.RestoreCommits == 0 && peer.WriteOperations == 0 && peer.Files.All(x => x.Value.SequenceEqual(originalRemote[x.Key])) && (await File.ReadAllBytesAsync(pcSlot)).SequenceEqual(pcBytes), "comparison changed an original save");

                    var store = new SnapshotStore(Get<AppSettings>("settings").BackupDirectory);
                    var historicalGames = new List<Snapshot>();
                    for (var i = 1; i <= 5; i++)
                    {
                        var historical = Peer.Save with { Key = "history-" + i, TitleId = $"01000000000100{i:X2}", GameName = "仅备份游戏 " + i };
                        historicalGames.Add(await store.BackupAsync(client, Peer.Info, historical, null, default));
                    }
                    var otherDevice = await store.BackupAsync(client, Peer.Info with { DeviceId = "OTHER-DEVICE" }, Peer.Save, null, default);
                    vm.Library.Selected = vm.Library.All.Single(x => x.TitleId == Peer.Save.TitleId);
                    var key = vm.Library.Selected.Key; vm.Library.Search = "测试"; vm.Library.Source = "电脑";
                    Call("Navigate", "saves"); await FlushUi();
                    var savesPage = (FrameworkElement)window.FindName("SavesPage");
                    Assert(savesPage.IsVisible && !((FrameworkElement)window.FindName("LibraryPage")).IsVisible && vm.Library.Filtered.Count == 0, "save navigation reused the filtered game library");
                    Assert(vm.SaveLibrary.Games.Count == 7 && historicalGames.All(s => vm.SaveLibrary.Games.Any(g => g.TitleId == s.Manifest.TitleId && g.BackupCount == 1 && g.Status == "仅电脑备份")), "existing backup-only games disappeared behind library filters");
                    Assert(historicalGames.All(s => !vm.Library.All.Any(g => g.TitleId == s.Manifest.TitleId)), "save fixture unexpectedly supplied installation files");
                    vm.SaveLibrary.OpenGameCommand.Execute(vm.SaveLibrary.Games.Single(g => g.TitleId == Peer.Save.TitleId));
                    Assert(vm.Commands["RestoreVersion"].CanExecute(first) && !vm.Commands["RestoreVersion"].CanExecute(otherDevice), "restore failed to enforce device identity");
                    vm.SaveLibrary.SelectedSource = vm.SaveLibrary.Sources.Single(s => s.Identity.DeviceId == "OTHER-DEVICE");
                    Assert(vm.SaveLibrary.Versions.Single().Snapshot.Path == otherDevice.Path && !vm.Commands["RestoreVersion"].CanExecute(otherDevice), "foreign-device history became a live restore target");
                    vm.SaveLibrary.BackCommand.Execute(null); vm.SaveLibrary.Search = "仅备份游戏"; vm.SaveLibrary.SourceFilter = "电脑备份";
                    Assert(vm.SaveLibrary.Games.Count == 5, "save page's own filters did not retain the five backup-only games");
                    var timelineSnapshot = historicalGames[2];
                    vm.SaveLibrary.OpenGameCommand.Execute(vm.SaveLibrary.Games.Single(g => g.TitleId == timelineSnapshot.Manifest.TitleId));
                    vm.SaveLibrary.SelectedSnapshot = vm.SaveLibrary.Versions.Single().Snapshot; await FlushUi();
                    var timeline = Walk<ListBox>(savesPage).Single(x => ReferenceEquals(x.ItemsSource, vm.SaveLibrary.Versions));
                    Assert(timeline.IsVisible && timeline.Items.Count == 1 && vm.SaveLibrary.Detail.History.Single().Path == timelineSnapshot.Path, "opening a backup-only game did not render its timeline");
                    ((Border)window.FindName("InstallOverlay")).Visibility = Visibility.Collapsed;
                    vm.StreamGuide = true; await FlushUi();
                    var escape = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                        PresentationSource.FromVisual(window)!, 0, System.Windows.Input.Key.Escape)
                    { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
                    Call("OnWindowKey", window, escape); await FlushUi();
                    Assert(escape.Handled && !vm.StreamGuide && vm.SaveLibrary.SelectedGame?.TitleId == timelineSnapshot.Manifest.TitleId,
                        $"Escape routing: key={escape.Key}, handled={escape.Handled}, preview={vm.StreamGuide}, title={vm.SaveLibrary.SelectedGame?.TitleId}, expected={timelineSnapshot.Manifest.TitleId}, notice={vm.Notice}");
                    Assert(!vm.Commands["Backup"].CanExecute(null) && !vm.Commands["RestoreVersion"].CanExecute(timelineSnapshot), "history-only timeline enabled a main-console write");
                    Set("device", Peer.Info with { Capabilities = Peer.Info.Capabilities.Concat(["saves.inspect", "restore.atomic.v1"]).Distinct().ToArray() });
                    await Invoke("SaveSyncCheckAsync"); await FlushUi();
                    var syncRow = vm.SaveLibrary.SyncRows.Single(r => r.Plan.Save.Key == Peer.Save.Key);
                    Assert(vm.SaveLibrary.SyncChecked && vm.Commands["SaveSyncRowRestore"].CanExecute(syncRow), "current conflict row did not enable an explicit restore");
                    await Invoke("RefreshDeviceAsync", CancellationToken.None); await FlushUi();
                    Assert(!vm.SaveLibrary.SyncChecked && !vm.Commands["SaveSyncRowRestore"].CanExecute(syncRow), "online inventory refresh retained a stale sync plan");
                    await Invoke("SaveSyncCheckAsync"); await FlushUi();
                    syncRow = vm.SaveLibrary.SyncRows.Single(r => r.Plan.Save.Key == Peer.Save.Key);
                    Set("device", Get<DeviceInfo>("device") with { DeviceId = "DIFFERENT-CONSOLE" });
                    Assert(!vm.Commands["SaveSyncRowRestore"].CanExecute(syncRow) && !vm.Commands["SaveSyncRowBackup"].CanExecute(syncRow), "old sync rows enabled actions for a different console");
                    Set("device", Peer.Info);
                    await Invoke("DisconnectAsync"); await WaitForCrossFiles();
                    Assert(!vm.SaveLibrary.SyncChecked && vm.SaveLibrary.SyncRows.Count == 0, "disconnect retained stale sync rows");
                    Assert(!vm.Online && vm.Library.Selected?.Key == key && vm.Library.Search == "测试" && vm.Library.Source == "电脑" && vm.Page == "saves", "offline library/page context changed");
                    Assert(vm.SaveLibrary.TimelineOpen && vm.SaveLibrary.Games.Count == 5 && vm.SaveLibrary.SelectedSnapshot?.Path == timelineSnapshot.Path && vm.SaveLibrary.Versions.Single().Snapshot.Path == timelineSnapshot.Path, "offline save timeline or independent filters changed");
                    Assert(!vm.Commands["Backup"].CanExecute(null) && !vm.Commands["RestoreVersion"].CanExecute(timelineSnapshot) && vm.CrossSave.SwitchFiles.Length == 0 && !vm.Commands["CrossSaveRead"].CanExecute(null), "offline state exposed a stale live save");
                    await using var reconnect = new Peer { Titles = peer.Titles, SaveRecords = peer.SaveRecords }; var newClient = await reconnect.ConnectAsync(); Set("client", newClient); vm.Online = true;
                    await Invoke("RefreshDeviceAsync", CancellationToken.None);
                    Assert(!vm.Commands["SaveSyncRowBackup"].CanExecute(syncRow) && !vm.Commands["SaveSyncRowRestore"].CanExecute(syncRow), "reconnect revived a previous connection's sync row");
                    Assert(vm.Library.Selected?.Key == key && vm.Page == "saves" && vm.Library.Search == "测试" && vm.Library.Source == "电脑", "reconnect library context changed");
                    Assert(vm.SaveLibrary.SelectedGame?.TitleId == timelineSnapshot.Manifest.TitleId && vm.SaveLibrary.SelectedSnapshot?.Path == timelineSnapshot.Path && vm.SaveLibrary.Games.Count == 5, "reconnect lost save timeline selection or filters");
                    reconnect.FreeBytes = 1; await Invoke("ReviewInstallAsync", (object)paths); var start = (Button)window.FindName("StartInstall"); Assert(!start.IsEnabled);
                    await Invoke("ExecuteInstallAsync", false); Assert(reconnect.InstalledNames.Count == 0, "space preflight allowed a write");
                    await RunDbiUiChecksAsync(window, directory);
                    Assert(reconnect.InstalledNames.Count == 0 && reconnect.WriteOperations == 0, "DBI UI tests executed a managed install or device write");
                    await Invoke("DisconnectAsync");
                    await RunRestoreCompletionUiChecksAsync(directory);
                    await RunSdFileContextUiChecksAsync(directory);
                    completion.TrySetResult();
                }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally { window.Close(); app.Shutdown(); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(45));
    }
    private static IEnumerable<T> Walk<T>(DependencyObject root) where T : DependencyObject
    { for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); if (child is T match) yield return match; foreach (var item in Walk<T>(child)) yield return item; } }
}
