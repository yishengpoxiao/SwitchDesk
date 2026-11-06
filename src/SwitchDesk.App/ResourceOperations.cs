using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SwitchDesk.Core;

namespace SwitchDesk.App;

public partial class MainWindow
{
    private readonly Dictionary<string, (ContentSource Source, ContentEntry Entry)> ownedResourceEntries = new(StringComparer.Ordinal);
    private static readonly System.Net.CookieContainer ResourceCookies = new();
    private CancellationTokenSource? resourceSearchCancellation;
    private string ResourceDirectory => Path.Combine(settings.LibraryDirectory, "Downloads");

    private void InitializeResources()
    {
        bool Idle() => !closingApplication && !vm.Resources.IsBusy && Volatile.Read(ref activeJobs) == 0;
        void Bind(string command, Func<Task> action, Func<bool>? can = null) =>
            vm.Bind(command, _ => _ = Guard(action), _ => Idle() && (can?.Invoke() ?? true));
        Bind("ResourceSearch", SearchResourcesAsync);
        Bind("ResourceAddLink", AddResourceLinkAsync);
        Bind("ResourceImportIndex", ImportResourceIndexAsync);
        Bind("ResourceDownload", DownloadResourceAsync, () => vm.Resources.SelectedResult?.CanDownload == true);
        Bind("ResourceInstall", InstallResourceAsync, CanInstallResource);
        Bind("ResourceEditIdentity", EditResourceIdentityAsync, () => vm.Resources.SelectedDownload?.Entry.Kind == ResourceKind.Cheat);
        vm.Bind("ResourceOpenSite", _ => OpenResourceSite());
        Bind("ResourceSearchWeb", () => OpenResourceSearchAsync(false), () => vm.Resources.IsPublicSite);
        Bind("ResourceSearchIndex", () => OpenResourceSearchAsync(true), () => vm.Resources.IsPublicSite && !string.IsNullOrWhiteSpace(vm.Resources.SearchText));
        vm.Bind("ResourceSiteNotice", _ => Process.Start(new ProcessStartInfo("https://nsthwj.com/") { UseShellExecute = true }), _ => vm.Resources.IsNstSite);
        vm.Bind("ResourceCancelSearch", _ => resourceSearchCancellation?.Cancel(), _ => vm.Resources.IsBusy && resourceSearchCancellation != null);
        vm.Resources.PropertyChanged += (_, e) =>
        {
            if (resourceSearchCancellation == null || e.PropertyName is not (nameof(ResourcesViewModel.SelectedSource) or nameof(ResourcesViewModel.SearchText) or nameof(ResourcesViewModel.SelectedKind))) return;
            resourceSearchCancellation.Cancel();
            vm.Resources.Status = "搜索条件已改变，请重新搜索资源";
        };
        vm.Bind("ResourceOpenDirectory", _ => Reveal(ResourceDirectory));
        vm.Bind("ResourceReveal", _ => Reveal(Path.GetDirectoryName(vm.Resources.SelectedDownload!.Path)!), _ => vm.Resources.SelectedDownload != null);
        vm.Bind("ResourceRefreshDownloads", _ => RefreshResourceDownloads(), _ => Idle());
        RefreshResourceDownloads();
    }

    private void RefreshResourceDownloads() => vm.Resources.SetDownloads(phase2.List<ResourceDownload>("resource-downloads"));
    private static HttpClient ResourceHttp() => new(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(15), CookieContainer = ResourceCookies,
        AutomaticDecompression = System.Net.DecompressionMethods.All })
        { Timeout = TimeSpan.FromMinutes(10) };

    private async Task SearchResourcesAsync()
    {
        var source = vm.Resources.SelectedSource.Id; var query = vm.Resources.SearchText.Trim(); var kind = vm.Resources.SelectedKind;
        if (source == "homebrew" && string.IsNullOrWhiteSpace(settings.HomebrewIndex))
        {
            var index = Dialogs.Text(this, "Homebrew 资源索引", "提供直链 NRO 的 JSON 索引地址", "https://");
            if (string.IsNullOrWhiteSpace(index)) return;
            var next = settings with { HomebrewIndex = index.Trim() }; settingsStore.Save(next); settings = next;
        }
        vm.Resources.IsBusy = true; vm.Resources.Status = "正在查询资源…";
        using var searchCancellation = new CancellationTokenSource(); resourceSearchCancellation = searchCancellation;
        try
        {
            var result = await RunJob("查找资源", async (_, ct) =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, searchCancellation.Token); ct = linked.Token;
                using var http = ResourceHttp();
                if (source is "gamer520" or "nsthwj")
                {
                    var search = await PublicResourceSites.SearchAsync(http, source, query, kind, ct);
                    return (Rows: search.Articles.Select(a => new ResourceResultRow(a)).ToArray(), Status: search.Message,
                        Available: search.Status is PublicResourceSearchStatus.Available or PublicResourceSearchStatus.NoResults, Public: search,
                        Origins: (Dictionary<string, (ContentSource Source, ContentEntry Entry)>?)null);
                }
                ResourceEntry[] entries; string[] failures = [];
                Dictionary<string, (ContentSource Source, ContentEntry Entry)>? origins = null;
                if (source == "content-sources") (entries, failures, origins) = await ReadOwnedResourcesAsync(http, ct);
                else if (source == "homebrew")
                {
                    var apps = await HomebrewStore.FetchIndexAsync(http, settings.HomebrewIndex, ct);
                    entries = apps.Select(a => new ResourceEntry(a.Name, ResourceKind.Homebrew, a.DownloadUrl, a.FileName)
                    { Sha256 = a.Sha256, SourceName = "Homebrew 索引", SourcePage = settings.HomebrewIndex, Description = a.Description + " · " + a.Version }).ToArray();
                }
                else entries = phase2.List<ResourceEntry>("resource-entries");
                var rows = entries.Where(e => query.Length == 0 || e.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                    e.TitleId?.Contains(query, StringComparison.OrdinalIgnoreCase) == true || e.Description.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                    .Select(e => new ResourceResultRow(e)).ToArray();
                return (Rows: rows, Status: $"已读取 {entries.Length} 项，匹配 {rows.Length} 项 · 下载后另行确认安装" +
                    (failures.Length == 0 ? "" : $" · {failures.Length} 个来源读取失败：{string.Join("、", failures)}；请检查来源配置或连接诊断"),
                    Available: entries.Length > 0 || failures.Length == 0, Public: (PublicResourceSearchResult?)null, Origins: origins);
            });
            if (source == vm.Resources.SelectedSource.Id && query == vm.Resources.SearchText.Trim() && kind == vm.Resources.SelectedKind)
            {
                if (result.Origins != null)
                {
                    ownedResourceEntries.Clear();
                    foreach (var origin in result.Origins) ownedResourceEntries.Add(origin.Key, origin.Value);
                }
                if (result.Public != null) vm.Resources.SetPublicSearchResult(result.Public);
                else { vm.Resources.SetResults(result.Rows, result.Available); vm.Resources.Status = result.Status; }
            }
        }
        catch (OperationCanceledException) when (searchCancellation.IsCancellationRequested)
        {
            if (source == vm.Resources.SelectedSource.Id && query == vm.Resources.SearchText.Trim() && kind == vm.Resources.SelectedKind)
            { vm.Resources.SetResults([], false); vm.Resources.Status = "搜索已取消；可重新搜索或在浏览器查看。"; }
        }
        catch (Exception ex)
        {
            if (source == vm.Resources.SelectedSource.Id && query == vm.Resources.SearchText.Trim() && kind == vm.Resources.SelectedKind)
            { vm.Resources.SetResults([], false); vm.Resources.Status = "查询未完成：" + ex.Message + " · 可查看原站或添加直链"; }
            throw;
        }
        finally { resourceSearchCancellation = null; vm.Resources.IsBusy = false; }
    }

    private async Task<(ResourceEntry[] Entries, string[] Failures, Dictionary<string, (ContentSource Source, ContentEntry Entry)> Origins)> ReadOwnedResourcesAsync(HttpClient http, CancellationToken ct)
    {
        var output = new List<ResourceEntry>(); var failures = new List<string>();
        var origins = new Dictionary<string, (ContentSource Source, ContentEntry Entry)>(StringComparer.Ordinal);
        foreach (var source in phase2.List<ContentSource>("content-sources"))
        {
            try
            {
                var index = await ContentSources.FetchAsync(http, source, new WindowsContentCredentials(), ct);
                phase2.Put("source-catalogs", source.Id, new SourceCatalog(source.Id, source.Name, DateTimeOffset.UtcNow, index));
                foreach (var entry in index.Entries)
                {
                    var file = ContentSources.Resolve(source, entry.Location);
                    var url = source.Kind == ContentSourceKind.Http ? file : new Uri(file).AbsoluteUri;
                    var item = new ResourceEntry(Path.GetFileName(source.Kind == ContentSourceKind.Http ? new Uri(file).AbsolutePath : file), ResourceKind.Game, url,
                        Path.GetFileName(source.Kind == ContentSourceKind.Http ? new Uri(file).AbsolutePath : file))
                    { Sha256 = entry.Sha256, Size = entry.Size, TitleId = entry.ApplicationId, SourceName = source.Name, Description = $"{entry.Kind} · {entry.ComponentId} · 组件版本 {entry.Version}" };
                    origins[item.Id] = (source, entry); output.Add(item);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or HttpRequestException or UnauthorizedAccessException || ex is OperationCanceledException && !ct.IsCancellationRequested)
            {
                failures.Add(source.Name); Log("资源来源读取失败：" + source.Name + " · " + ex.Message);
            }
        }
        return (output.ToArray(), failures.ToArray(), origins);
    }

    private void OpenResourceSite()
    {
        var page = vm.Resources.SelectedResult?.PageUrl;
        if (page == null && vm.Resources.SelectedSource.Id is "gamer520" or "nsthwj")
            page = PublicResourceSites.SearchUrl(vm.Resources.SelectedSource.Id, vm.Resources.SearchText.Trim(), vm.Resources.SelectedKind, vm.Resources.ResolvedSiteSearchUrl);
        if (page == null) { vm.Notify("所选条目没有来源页面；可选择下载文件或添加索引"); return; }
        if (!Uri.TryCreate(page, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || uri.UserInfo.Length != 0)
            throw new InvalidDataException("来源页面地址无效");
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    private async Task OpenResourceSearchAsync(bool index)
    {
        var model = vm.Resources;
        var source = model.SelectedSource.Id; var query = model.SearchText; var kind = model.SelectedKind;
        var previousSearchUrl = model.ResolvedSiteSearchUrl;
        var resolved = await RunJob("核对网站入口", async (_, ct) =>
        {
            using var http = ResourceHttp();
            return await PublicResourceSites.ResolveSearchUrlAsync(http, source, query, kind, ct, previousSearchUrl);
        });
        if (source != model.SelectedSource.Id || query != model.SearchText || kind != model.SelectedKind) return;
        var url = index ? PublicResourceSites.IndexSearchUrl(source, query, kind, resolved) : resolved;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private async Task ImportResourceIndexAsync()
    {
        var location = Dialogs.Text(this, "导入资源索引", "JSON 文件的绝对路径或 HTTP(S) 地址（格式见资源下载说明）");
        if (string.IsNullOrWhiteSpace(location)) return;
        var entries = await RunJob("导入资源索引", async (_, ct) =>
        {
            if (Uri.TryCreate(location.Trim(), UriKind.Absolute, out var url) && url.Scheme is "http" or "https")
            { using var http = ResourceHttp(); return await ResourceIndex.FetchAsync(http, url.AbsoluteUri, ct); }
            var path = Path.GetFullPath(location.Trim()); LocalSafety.DemandRegularFile(path);
            if (new FileInfo(path).Length > ResourceIndex.MaximumIndexBytes) throw new InvalidDataException("资源索引超过 4 MiB");
            return ResourceIndex.Parse(await File.ReadAllBytesAsync(path, ct));
        });
        foreach (var entry in entries) phase2.Put("resource-entries", entry.Id, entry);
        vm.Resources.SelectedSource = vm.Resources.Sources.Single(s => s.Id == "imported"); vm.Resources.SearchText = "";
        vm.Resources.SetResults(entries.Select(e => new ResourceResultRow(e))); vm.Resources.Status = $"已导入 {entries.Length} 项资源，选择文件后下载到电脑";
    }

    private Task AddResourceLinkAsync()
    {
        var dialog = new Window { Owner = this, Title = "添加资源直链", Width = 600, SizeToContent = SizeToContent.Height,
            MaxHeight = Math.Max(400, SystemParameters.WorkArea.Height - 64), ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Style = (Style)FindResource(typeof(Window)) };
        var panel = new StackPanel { Margin = new(24) }; dialog.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        TextBox Input(string label) { panel.Children.Add(new TextBlock { Text = label, Margin = new(0, 10, 0, 6) }); var field = new TextBox(); System.Windows.Automation.AutomationProperties.SetName(field, label); panel.Children.Add(field); return field; }
        panel.Children.Add(new TextBlock { Text = "文件先下载到电脑，之后再选择安装。支持游戏包、NRO 与金手指文本。", TextWrapping = TextWrapping.Wrap });
        var name = Input("资源名称"); var url = Input("文件 HTTP(S) 直链");
        var kind = new ComboBox { ItemsSource = new[] { "游戏", "Homebrew / 插件", "金手指" }, SelectedIndex = (int)(vm.Resources.SelectedKind ?? ResourceKind.Game), Margin = new(0, 12, 0, 0) }; panel.Children.Add(kind);
        var sha = Input("来源 SHA-256（可选）"); var size = Input("来源声明字节数（可选）");
        var title = Input("游戏 TitleID（可选，安装金手指时必填）"); var build = Input("金手指 BuildID（可选，安装金手指时必填）");
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 0) }; error.SetResourceReference(ForegroundProperty, "Error"); panel.Children.Add(error);
        ResourceEntry? result = null;
        var add = new Button { Content = "添加到资源列表", Style = (Style)FindResource("Primary"), Margin = new(0, 16, 0, 0) }; panel.Children.Add(add);
        add.Click += (_, _) =>
        {
            try
            {
                long? length = size.Text.Trim().Length == 0 ? null : long.TryParse(size.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : throw new InvalidDataException("字节数应为正整数");
                result = ResourceIndex.Validate(new(name.Text.Trim(), (ResourceKind)kind.SelectedIndex, url.Text.Trim(), "")
                { Sha256 = string.IsNullOrWhiteSpace(sha.Text) ? null : sha.Text.Trim(), Size = length,
                    TitleId = string.IsNullOrWhiteSpace(title.Text) ? null : title.Text.Trim(), BuildId = string.IsNullOrWhiteSpace(build.Text) ? null : build.Text.Trim(), SourceName = "手动直链" });
                dialog.DialogResult = true;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException) { error.Text = ex.Message; }
        };
        if (dialog.ShowDialog() == true && result != null)
        {
            phase2.Put("resource-entries", result.Id, result); vm.Resources.SelectedSource = vm.Resources.Sources.Single(s => s.Id == "imported");
            vm.Resources.SearchText = ""; vm.Resources.SelectedType = "全部类型"; vm.Resources.SetResults([new(result)]); vm.Resources.Status = "直链已添加，选择下载后文件才会保存到电脑";
        }
        return Task.CompletedTask;
    }

    private async Task DownloadResourceAsync()
    {
        var entry = vm.Resources.SelectedResult?.Entry ?? throw new IOException("此项是来源文章，请先在原站取得文件直链");
        var directory = ResourceDirectory;
        var downloaded = await RunJob("下载资源 · " + entry.Name, async (job, ct) =>
        {
            ResourceDownload receipt;
            string? retryDirectory = null;
            if (ownedResourceEntries.TryGetValue(entry.Id, out var origin) &&
                phase2.Get<ContentSource>("content-sources", origin.Source.Id)?.Value != origin.Source)
                throw new IOException("此内容来源已移除或修改，请重新搜索资源");
            var cached = phase2.Get<ResourceDownload>("resource-downloads", entry.Id)?.Value;
            if (cached?.ContentSourceId is {} sourceId && phase2.Get<ContentSource>("content-sources", sourceId) == null)
                throw new IOException("此内容来源已移除，请重新搜索资源；已下载文件仍保留在电脑");
            if (cached is {} existing && File.Exists(existing.Path) &&
                (entry.Size == null || existing.Size == entry.Size) && (entry.Sha256 == null || Fingerprints.Equal(entry.Sha256, existing.Sha256)))
            {
                try { await ResourceDownloads.VerifyAsync(existing, ct); }
                catch (InvalidDataException)
                {
                    // Keep the changed file and its receipt until a replacement finishes.
                    // A separate directory also preserves other receipts sharing this path.
                    retryDirectory = Path.Combine(directory, "retry-" + Guid.NewGuid().ToString("N"));
                    Log("已下载资源校验失败，保留原文件并重新下载：" + entry.Name);
                }
                if (retryDirectory == null)
                {
                    if (ownedResourceEntries.TryGetValue(entry.Id, out var cachedOrigin))
                    {
                        existing = existing with { Entry = entry, ExpectedContent = cachedOrigin.Entry, ContentSourceId = cachedOrigin.Source.Id };
                        phase2.Put("resource-downloads", entry.Id, existing);
                    }
                    return existing;
                }
            }
            // A revised index can change the expected size or digest. Its old file
            // must not block the new entry or be overwritten merely by name.
            if (retryDirectory == null && (cached != null && File.Exists(cached.Path) ||
                !ownedResourceEntries.ContainsKey(entry.Id) &&
                (File.Exists(PathSafety.Child(directory, entry.FileName)) || Directory.Exists(PathSafety.Child(directory, entry.FileName)))))
                retryDirectory = Path.Combine(directory, "retry-" + Guid.NewGuid().ToString("N"));
            var targetDirectory = retryDirectory ?? directory;
            string? retryPartial = null;
            try
            {
                if (ownedResourceEntries.TryGetValue(entry.Id, out var owned))
                {
                    if (phase2.Get<ContentSource>("content-sources", owned.Source.Id)?.Value != owned.Source)
                        throw new IOException("此内容来源已移除或修改，请重新搜索资源");
                    if (retryDirectory != null)
                    {
                        var location = ContentSources.Resolve(owned.Source, owned.Entry.Location);
                        var extension = Path.GetExtension(owned.Source.Kind == ContentSourceKind.Http ? new Uri(location).AbsolutePath : location).ToLowerInvariant();
                        retryPartial = Path.Combine(retryDirectory, owned.Entry.Sha256.ToUpperInvariant() + extension + ".part");
                    }
                    using var downloads = new ContentDownloads(new WindowsContentCredentials());
                    var path = await downloads.FetchAsync(owned.Source, owned.Entry, targetDirectory, Progress(job), ct);
                    receipt = new(entry, path, owned.Entry.Size, owned.Entry.Sha256, true, DateTimeOffset.UtcNow)
                    { ExpectedContent = owned.Entry, ContentSourceId = owned.Source.Id };
                }
                else { using var downloads = new ResourceDownloads(); receipt = await downloads.DownloadAsync(entry, targetDirectory, Progress(job), ct); }
                phase2.Put("resource-downloads", entry.Id, receipt);
                if (entry.Kind == ResourceKind.Game) library.Upsert(new(receipt.Path, entry.Name, null, Path.GetExtension(receipt.Path)[1..].ToUpperInvariant(), "待识别", receipt.Size, receipt.Sha256, "已下载，待主机识别"));
                return receipt;
            }
            finally
            {
                // Generic direct links remove their own staging files. An owned-content
                // retry has no persisted resume directory, so remove only its known stages.
                if (retryDirectory != null)
                {
                    try
                    {
                        if (retryPartial != null)
                            foreach (var path in new[] { retryPartial, retryPartial + ".resume.json", retryPartial + ".resume.json.new" })
                                if (File.Exists(path)) { LocalSafety.DemandRegularFile(path); File.Delete(path); }
                        if (Directory.Exists(retryDirectory) && !Directory.EnumerateFileSystemEntries(retryDirectory).Any()) Directory.Delete(retryDirectory);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                    { Log("重试下载暂存清理未完成：" + ex.Message); }
                }
            }
        });
        RefreshResourceDownloads(); vm.Resources.SelectedDownload = vm.Resources.Downloads.Single(r => r.Value.Entry.Id == downloaded.Entry.Id); vm.Resources.SelectedTab = 1;
        vm.Resources.Status = "下载完成 · 尚未安装"; vm.Notify("资源已下载到电脑，核对后可选择安装", directory);
    }

    private bool CanInstallResource()
    {
        if (vm.Resources.SelectedDownload is not { Exists: true } row || !vm.Online || client?.IsBroken != false || ManagementPaused) return false;
        return row.Entry.Kind switch
        {
            ResourceKind.Game => device?.Capabilities.Contains("inspect") == true && device.Capabilities.Contains("install"),
            ResourceKind.Homebrew => device?.Capabilities.Contains("files") == true,
            ResourceKind.Cheat => device?.Capabilities.Contains("cheats.text.v1") == true && ContentCatalog.ValidId(row.Entry.TitleId) && ContentCatalog.ValidId(row.Entry.BuildId),
            _ => false
        };
    }

    private Task EditResourceIdentityAsync()
    {
        var receipt = vm.Resources.SelectedDownload?.Value ?? throw new IOException("请选择已下载金手指");
        if (receipt.Entry.Kind != ResourceKind.Cheat) return Task.CompletedTask;
        var titleId = Dialogs.Text(this, "金手指目标游戏", "游戏 TitleID（16 位十六进制，请核对实际主机）", receipt.Entry.TitleId ?? "");
        if (titleId == null) return Task.CompletedTask;
        var buildId = Dialogs.Text(this, "金手指目标版本", "与文件匹配的 BuildID（16 位十六进制）", receipt.Entry.BuildId ?? Path.GetFileNameWithoutExtension(receipt.Entry.FileName));
        if (buildId == null) return Task.CompletedTask;
        if (!ContentCatalog.ValidId(titleId.Trim()) || !ContentCatalog.ValidId(buildId.Trim())) throw new InvalidDataException("TitleID 与 BuildID 都必须是 16 位十六进制");
        var updated = receipt with { Entry = receipt.Entry with { TitleId = titleId.Trim().ToUpperInvariant(), BuildId = buildId.Trim().ToUpperInvariant() } };
        phase2.Put("resource-downloads", updated.Entry.Id, updated);
        if (updated.Entry.Id != receipt.Entry.Id && phase2.Get<ResourceDownload>("resource-downloads", receipt.Entry.Id) is {} old)
            phase2.Remove("resource-downloads", receipt.Entry.Id, old.Revision);
        RefreshResourceDownloads(); vm.Resources.SelectedDownload = vm.Resources.Downloads.First(r => r.Value.Entry.Id == updated.Entry.Id);
        vm.Resources.Status = "已补充目标游戏与构建标识；安装前仍需你核对实际版本";
        return Task.CompletedTask;
    }

    private async Task InstallResourceAsync()
    {
        var receipt = vm.Resources.SelectedDownload?.Value ?? throw new IOException("请选择已下载资源"); var entry = receipt.Entry;
        await RunJob("核对本地资源", async (_, ct) => { await ResourceDownloads.VerifyAsync(receipt, ct); return true; });
        if (entry.Kind == ResourceKind.Game)
        {
            var expected = receipt.ExpectedContent ?? (ownedResourceEntries.TryGetValue(entry.Id, out var owned) ? owned.Entry : null);
            await ReviewDownloadedInstallAsync(receipt, expected);
            return;
        }
        var capability = entry.Kind == ResourceKind.Cheat ? "cheats.text.v1" : "files";
        var (connection, identity) = RequireDevice(capability);
        if (entry.Kind == ResourceKind.Cheat && (!ContentCatalog.ValidId(entry.TitleId) || !ContentCatalog.ValidId(entry.BuildId) || !titles.Any(g => g.TitleId.Equals(entry.TitleId, StringComparison.OrdinalIgnoreCase))))
            throw new IOException("金手指需要明确的 TitleID 与 BuildID，且当前主机必须已读取到该游戏；仅名称相同不能确认版本匹配");
        var fileBuild = Path.GetFileNameWithoutExtension(entry.FileName);
        if (entry.Kind == ResourceKind.Cheat && ContentCatalog.ValidId(fileBuild) && !fileBuild.Equals(entry.BuildId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("金手指文件名中的 BuildID 与填写的目标构建不一致，请选择匹配版本的文件");
        var target = entry.Kind == ResourceKind.Cheat ? CheatManager.CheatPath(entry.TitleId!, entry.BuildId!) : HomebrewStore.InstallPath(entry.Name, entry.FileName);
        var detail = $"资源  {entry.Name}\n来源  {ResourceResultRow.SourceLabel(entry)}\n主机  {identity.Model} · {identity.Environment}\n目标  {target}\n" +
            (entry.Kind == ResourceKind.Cheat ? $"\n请核对当前游戏的 BuildID 为 {entry.BuildId}。主机尚未返回可自动核对的 BuildID；文件匹配不代表金手指效果已验收。\n" : "") +
            (receipt.ExpectedVerified ? "\n下载时已匹配来源摘要；现在会再次校验本地文件。" : "\n来源未提供预期摘要，请核对来源；现在会检查下载后文件是否被改动。");
        if (!Dialogs.Confirm(this, "安装下载资源", detail, "确认写入 SD 卡")) return;
        await RunJob("安装资源 · " + entry.Name, async (job, ct) =>
        {
            var (peer, current) = RequireDevice(capability);
            if (!ReferenceEquals(peer, connection) || current.DeviceId != identity.DeviceId || current.Environment != identity.Environment) throw new IOException("主机连接或环境已变化，请重新确认");
            await ResourceDownloads.VerifyAsync(receipt, ct);
            if (receipt.Size > ResourceIndex.MaximumBytes(entry.Kind)) throw new InvalidDataException("资源超过安装预算");
            var bytes = await File.ReadAllBytesAsync(receipt.Path, ct);
            if (!Fingerprints.Equal(Convert.ToHexString(SHA256.HashData(bytes)), receipt.Sha256)) throw new InvalidDataException("资源在安装前发生变化");
            if (entry.Kind == ResourceKind.Cheat)
                await CheatManager.InstallAsync(peer, entry.TitleId!, entry.BuildId!, bytes, Progress(job), ct);
            else
            {
                if (bytes.Length < 0x80 || !bytes.AsSpan(0x10, 4).SequenceEqual("NRO0"u8)) throw new InvalidDataException("下载文件不是可识别的 NRO，未写入主机");
                await HomebrewStore.InstallAsync(peer, new(entry.Name, "", "", entry.Description, entry.DownloadUrl, entry.FileName), bytes, receipt.Sha256, Progress(job), ct);
            }
            return true;
        });
        vm.Notify("资源已写入 SD 卡 · 游戏内或插件运行效果仍需检查");
    }
}
