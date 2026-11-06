using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using SwitchDesk.Core;

namespace SwitchDesk.App;

public sealed record ResourceSourceChoice(string Id, string Label);

/// <summary>A public article is a browsing result; only an explicit resource entry can be downloaded.</summary>
public sealed class ResourceResultRow
{
    public ResourceResultRow(PublicResourceArticle article) => Article = article;
    public ResourceResultRow(ResourceEntry entry) => Entry = entry;

    public PublicResourceArticle? Article { get; }
    public ResourceEntry? Entry { get; }
    public ResourceKind Kind => Entry?.Kind ?? Article!.Kind;
    public string Title => Entry?.Name ?? Article!.Title;
    public string? PageUrl => Article?.PageUrl ?? Entry?.SourcePage;
    public bool CanDownload => Entry != null;
    public string Id => Entry?.Id ?? Article!.PageUrl;
    public string Source => Entry is {} entry ? SourceLabel(entry) : Article!.Source;
    public string Metadata => KindLabel(Kind) + " · " + Source +
        ((Entry?.TitleId ?? Article?.TitleId) is {} title ? " · " + title : "") +
        (Entry?.BuildId is {} build ? " · Build " + build : "");
    public string Description => Entry is {} entry ? entry.Description : "公开文章，可前往原站查看完整资源说明。";
    public string Availability => Entry is {} entry
        ? "可下载到电脑" + (entry.Size is {} bytes ? " · " + Display.Size(bytes) : " · 大小由下载响应提供") +
          (entry.Sha256 == null ? " · 来源未提供摘要" : " · 来源已提供 SHA-256")
        : "原站文章 · 需在原站取得下载链接后添加";

    internal static string KindLabel(ResourceKind kind) => kind switch
    {
        ResourceKind.Game => "游戏",
        ResourceKind.Homebrew => "Homebrew / 插件",
        ResourceKind.Cheat => "金手指",
        _ => "资源"
    };

    internal static string SourceLabel(ResourceEntry entry) => !string.IsNullOrWhiteSpace(entry.SourceName)
        ? entry.SourceName : Uri.TryCreate(entry.DownloadUrl, UriKind.Absolute, out var url) ? url.Host : "手动添加";
}

public sealed class ResourceDownloadRow(ResourceDownload value)
{
    public ResourceDownload Value { get; } = value;
    public ResourceEntry Entry => Value.Entry;
    public string Title => Entry.Name;
    public bool Exists => File.Exists(Value.Path);
    public string FileName => System.IO.Path.GetFileName(Value.Path);
    public string Path => Value.Path;
    public string Metadata => ResourceResultRow.KindLabel(Entry.Kind) + " · " + Display.Size(Value.Size) + " · " + ResourceResultRow.SourceLabel(Entry) +
        (Entry.TitleId is {} title ? " · " + title : "") + (Entry.BuildId is {} build ? " · Build " + build : "");
    public string Status => !Exists ? "文件已移动或删除" :
        (Value.ExpectedVerified ? "下载时匹配来源摘要" : "来源未提供摘要，安装前请核对来源") +
        " · " + Value.DownloadedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}

public sealed class ResourcesViewModel : Observable
{
    private ResourceResultRow[] all = [];
    private string searchText = "", selectedType = "全部类型", status = "选择来源并搜索，或添加直链与索引";
    private ResourceSourceChoice selectedSource;
    private ResourceResultRow? selectedResult;
    private ResourceDownloadRow? selectedDownload;
    private bool isBusy, searched, sourceAvailable = true;
    private int selectedTab;
    public string? ResolvedSiteSearchUrl { get; private set; }
    public bool IsPublicSite => SelectedSource.Id is "gamer520" or "nsthwj";
    public bool IsNstSite => SelectedSource.Id == "nsthwj";

    public ResourceSourceChoice[] Sources { get; } =
    [
        new("gamer520", "Gamer520"),
        new("nsthwj", "NS 头号玩家"),
        new("content-sources", "自有游戏来源"),
        new("homebrew", "Homebrew 索引"),
        new("imported", "已导入资源")
    ];
    public string[] Types { get; } = ["全部类型", "游戏", "Homebrew / 插件", "金手指"];
    public ObservableCollection<ResourceResultRow> Results { get; } = [];
    public ObservableCollection<ResourceDownloadRow> Downloads { get; } = [];
    public string SearchText { get => searchText; set => Set(ref searchText, value); }
    public ResourceSourceChoice SelectedSource
    {
        get => selectedSource;
        set
        {
            if (value == null || !Set(ref selectedSource, value)) return;
            all = []; searched = false; sourceAvailable = true; SelectedTab = 0; Filter();
            ResolvedSiteSearchUrl = null; Changed(nameof(IsPublicSite)); Changed(nameof(IsNstSite));
            Status = "来源已切换，请点击搜索资源";
            CommandManager.InvalidateRequerySuggested();
        }
    }
    public string SelectedType
    {
        get => selectedType;
        set
        {
            if (!Types.Contains(value, StringComparer.Ordinal) || !Set(ref selectedType, value)) return;
            Filter(); Changed(nameof(SelectedKind)); CommandManager.InvalidateRequerySuggested();
        }
    }
    public ResourceKind? SelectedKind => SelectedType switch
    {
        "游戏" => ResourceKind.Game,
        "Homebrew / 插件" => ResourceKind.Homebrew,
        "金手指" => ResourceKind.Cheat,
        _ => null
    };
    public ResourceResultRow? SelectedResult
    {
        get => selectedResult;
        set { if (Set(ref selectedResult, value)) { Changed(nameof(SelectionHint)); CommandManager.InvalidateRequerySuggested(); } }
    }
    public ResourceDownloadRow? SelectedDownload
    {
        get => selectedDownload;
        set { if (Set(ref selectedDownload, value)) { Changed(nameof(DownloadSelectionHint)); CommandManager.InvalidateRequerySuggested(); } }
    }
    public string Status { get => status; set => Set(ref status, value); }
    public bool IsBusy { get => isBusy; set { if (Set(ref isBusy, value)) CommandManager.InvalidateRequerySuggested(); } }
    public int SelectedTab { get => selectedTab; set => Set(ref selectedTab, value); }
    public bool Empty => Results.Count == 0;
    public bool DownloadsEmpty => Downloads.Count == 0;
    public string DownloadCountText => $"{Downloads.Count} 个下载记录 · {Downloads.Count(row => row.Exists)} 份文件可用";
    public string EmptyHeading => !sourceAvailable ? "暂时无法在应用内读取此来源" : searched ? "没有找到匹配的资源" : "从游戏或工具名称开始";
    public string EmptyDescription => !sourceAvailable
        ? "请查看上方状态并打开原站；这不表示网站没有资源，也可添加实际直链或导入索引。"
        : searched
        ? "可换个关键词或类型，也可以查看原站、添加直链或导入 JSON 索引。"
        : "公开文章可在原站查看；直链与索引中的文件可下载到电脑，不需要连接 Switch。";
    public string SelectionHint => SelectedResult is not {} result ? "选择一个结果，查看原站或下载文件。" :
        result.CanDownload ? "下载完成后，可在“已下载”中核对并选择安装。" : "此项是原站文章，需先取得实际下载链接；支持下载的文件不会自动安装。";
    public string DownloadSelectionHint => SelectedDownload is not {} row ? "选择已下载文件，再核对并安装；也可打开文件所在目录。" :
        !row.Exists ? "文件已移动或删除，请重新下载或打开目录查找。" : row.Entry.Kind switch
        {
            ResourceKind.Game => "游戏文件先进入现有安装预检清单，核对后再开始安装。",
            ResourceKind.Cheat => "金手指需要匹配游戏 TitleID 与构建 ID；缺少时先点“补充金手指标识”，核对版本后再安装。",
            _ => "Homebrew / 插件确认后写入 /switch/；系统模块与覆盖层安装需使用对应项目说明。"
        };

    public ResourcesViewModel() => selectedSource = Sources[0];

    public void SetPublicSearchResult(PublicResourceSearchResult result)
    {
        if (result.Site.Id != SelectedSource.Id) return;
        // A failed announcement lookup only knows the old notice site. Keep the
        // last confirmed search host for the browser fallback.
        if (result.SearchUrl != result.Site.HomeUrl) ResolvedSiteSearchUrl = result.SearchUrl;
        SetResults(result.Articles.Select(a => new ResourceResultRow(a)), result.Status is PublicResourceSearchStatus.Available or PublicResourceSearchStatus.NoResults);
        Status = result.Message;
    }

    public void SetResults(IEnumerable<ResourceResultRow> rows, bool sourceAvailable = true)
    {
        all = rows.ToArray(); searched = true; this.sourceAvailable = sourceAvailable; SelectedTab = 0;
        Filter();
    }

    public void SetDownloads(IEnumerable<ResourceDownload> downloads)
    {
        var selectedPath = SelectedDownload?.Path;
        var selectedId = SelectedDownload?.Entry.Id;
        Downloads.Clear();
        foreach (var download in downloads.OrderByDescending(d => d.DownloadedAt)) Downloads.Add(new(download));
        SelectedDownload = Downloads.FirstOrDefault(d => d.Entry.Id == selectedId && d.Path == selectedPath) ??
            Downloads.FirstOrDefault(d => d.Entry.Id == selectedId) ?? Downloads.FirstOrDefault();
        Changed(nameof(DownloadsEmpty)); Changed(nameof(DownloadCountText)); Changed(nameof(DownloadSelectionHint));
    }

    private void Filter()
    {
        var selectedId = SelectedResult?.Id;
        Results.Clear();
        foreach (var row in all.Where(r => SelectedKind == null || r.Kind == SelectedKind)) Results.Add(row);
        SelectedResult = Results.FirstOrDefault(r => r.Id == selectedId) ?? Results.FirstOrDefault();
        Changed(nameof(Empty)); Changed(nameof(EmptyHeading)); Changed(nameof(EmptyDescription));
    }
}
