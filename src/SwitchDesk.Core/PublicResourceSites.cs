using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace SwitchDesk.Core;

public sealed record PublicResourceSite(string Id, string Name, string HomeUrl, bool SupportsSearch);
public sealed record PublicResourceArticle(string Title, string PageUrl, string Source, ResourceKind Kind, string? TitleId = null)
{
    public DateTimeOffset? PublishedAt { get; init; }
}
public enum PublicResourceSearchStatus { Available, NoResults, RequiresBrowser, Unavailable }
public sealed record PublicResourceSearchResult(PublicResourceSite Site, PublicResourceArticle[] Articles,
    PublicResourceSearchStatus Status, string Message, DateTimeOffset CheckedAt, string SearchUrl);

/// <summary>
/// Reads public article metadata only. A page title or filename is never proof of
/// downloadable content, a verified game identity, or compatibility with a console.
/// </summary>
public static class PublicResourceSites
{
    public const int MaximumPageBytes = 4 * 1024 * 1024;
    private static readonly PublicResourceSite Gamer = new("gamer520", "Gamer520", "https://www.gamer520.com/", true);
    private static readonly PublicResourceSite Nst = new("nsthwj", "NS 头号玩家", "https://nsthwj.com/", true);
    public static IReadOnlyList<PublicResourceSite> All { get; } = Array.AsReadOnly(new[] { Gamer, Nst });
    private static readonly TimeSpan RegexBudget = TimeSpan.FromMilliseconds(300);
    private static Regex Pattern(string value) => new(value, RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, RegexBudget);
    private static readonly Regex HiddenContent = Pattern(@"<!--.*?-->|<(?:script|style)\b[^>]*>.*?</(?:script|style)\s*>");
    private static readonly Regex Articles = Pattern(@"<article\b[^>]*>.*?</article\s*>");
    private static readonly Regex Heading = Pattern(@"<h2\b[^>]*>(?<body>.*?)</h2\s*>");
    private static readonly Regex Anchors = Pattern(@"<a\b(?<attributes>[^>]*)>(?<text>.*?)</a\s*>");
    private static readonly Regex Href = Pattern("(?:^|\\s)href\\s*=\\s*(?:\"(?<value>[^\"]*)\"|'(?<value>[^']*)'|(?<value>[^\\s>]+))");
    private static readonly Regex StripTags = Pattern(@"<[^>]*>");
    private static readonly Regex Whitespace = Pattern(@"\s+");
    private static readonly Regex NumericArticlePath = Pattern(@"^/[0-9]{1,16}\.html$");
    private static readonly Regex TitleId = Pattern(@"(?:Title\s*ID|\bTID|游戏\s*ID)\s*[:：=]?\s*(?<id>[0-9a-f]{16})(?![0-9a-f])");
    private static readonly Regex TimeElement = Pattern(@"<time\b[^>]*\bdatetime\s*=\s*(?:""(?<value>[^""]*)""|'(?<value>[^']*)')[^>]*>");

    public static PublicResourceSite Get(string id) => All.FirstOrDefault(s => s.Id == id)
        ?? throw new InvalidDataException("没有登记此资源网站");

    public static string SearchUrl(string siteId, string query, ResourceKind? kind, string? resolvedSearchUrl = null)
    {
        var site = Get(siteId); ValidateQuery(query, kind);
        if (site.Id == "nsthwj")
        {
            var origin = new Uri("https://nsthwj.cn/");
            if (Uri.TryCreate(resolvedSearchUrl, UriKind.Absolute, out var resolved) && NstResourceSearch.IsSiteOrigin(resolved))
                origin = new Uri(resolved.GetLeftPart(UriPartial.Authority) + "/");
            var text = query.Trim();
            if (kind == ResourceKind.Cheat) text += (text.Length > 0 ? " " : "") + "金手指";
            if (kind == ResourceKind.Homebrew) text += (text.Length > 0 ? " " : "") + "Homebrew";
            return text.Length == 0 ? origin.AbsoluteUri : origin.AbsoluteUri + "?q=" + Uri.EscapeDataString(text);
        }
        // Confirmed public form: GET /, input name="s". No private API or login endpoint.
        return string.IsNullOrWhiteSpace(query) ? site.HomeUrl + (kind == ResourceKind.Cheat ? "jinshouzhi" : "gameswitch") :
            site.HomeUrl + "?s=" + Uri.EscapeDataString(query.Trim());
    }

    public static string IndexSearchUrl(string siteId, string query, ResourceKind? kind, string? resolvedSearchUrl = null)
    {
        ValidateQuery(query, kind);
        var host = new Uri(SearchUrl(siteId, query, kind, resolvedSearchUrl)).IdnHost;
        var text = "site:" + host + " " + query.Trim() + (kind == ResourceKind.Cheat ? " 金手指" : kind == ResourceKind.Homebrew ? " Homebrew" : " Switch");
        return "https://www.bing.com/search?q=" + Uri.EscapeDataString(text);
    }

    public static async Task<string> ResolveSearchUrlAsync(HttpClient http, string siteId, string query, ResourceKind? kind, CancellationToken ct, string? previousSearchUrl = null)
    {
        ValidateQuery(query, kind); var site = Get(siteId);
        if (site.Id != "nsthwj") return SearchUrl(siteId, query, kind);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            var origin = await NstResourceSearch.ResolvePublishedOriginAsync(http, timeout.Token);
            return SearchUrl(siteId, query, kind, origin.AbsoluteUri);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or
            System.Text.Json.JsonException or RegexMatchTimeoutException or OperationCanceledException)
        {
            ct.ThrowIfCancellationRequested();
            // Browser and public-index fallbacks remain usable when the notice service is offline.
            return SearchUrl(siteId, query, kind, previousSearchUrl);
        }
    }

    public static async Task<PublicResourceSearchResult> SearchAsync(HttpClient http, string siteId, string query,
        ResourceKind? kind, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var site = Get(siteId); var url = SearchUrl(siteId, query, kind); var checkedAt = DateTimeOffset.UtcNow;
        if (site.Id == "nsthwj") return await NstResourceSearch.SearchAsync(http, site, query, kind, ct);
        PublicResourceSearchResult Result(PublicResourceSearchStatus state, string message, PublicResourceArticle[]? articles = null) =>
            new(site, articles ?? [], state, message, checkedAt, url);
        if (kind == ResourceKind.Homebrew)
            return Result(PublicResourceSearchStatus.RequiresBrowser, "此站未提供已适配的 Homebrew 列表；请使用资源索引或直接 NRO 地址，也可打开原站查看。");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(25));
            var remainingBytes = MaximumPageBytes;
            for (var attempt = 0; attempt < 2; attempt++)
            {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("SwitchDesk/0.3 (public article metadata)");
            request.Headers.Accept.ParseAdd("text/html");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.RequestMessage?.RequestUri is {} actual && actual.AbsoluteUri != url)
                return Result(PublicResourceSearchStatus.RequiresBrowser, "网站跳转到了其他页面，请打开原站确认；未继续读取跳转内容。");
            // The public site sometimes initializes anonymous cookies with a 403
            // and asks the browser to reload this exact URL. Preserve the normal
            // handler cookie jar and perform that one reload, without executing JS.
            if (attempt == 0 && response.StatusCode == HttpStatusCode.Forbidden && HasGamerSessionCookie(response, new Uri(url)))
            {
                var initialization = await ReadGamerHtmlAsync(response, remainingBytes, timeout.Token);
                remainingBytes -= initialization.Bytes;
                if (IsGamerCookieReload(initialization.Html, new Uri(url))) continue;
            }
            if ((int)response.StatusCode is >= 300 and < 400 || response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                return Result(PublicResourceSearchStatus.RequiresBrowser, $"网站返回 HTTP {(int)response.StatusCode}，请在浏览器打开原站查看。");
            if (!response.IsSuccessStatusCode)
                return Result(PublicResourceSearchStatus.Unavailable, $"公开页面暂不可用（HTTP {(int)response.StatusCode}），可稍后重试或打开原站。");
            var page = await ReadGamerHtmlAsync(response, remainingBytes, timeout.Token);
            var html = page.Html;
            if (!site.SupportsSearch)
                return Result(PublicResourceSearchStatus.RequiresBrowser,
                    html.Contains("JavaScript", StringComparison.OrdinalIgnoreCase) || html.Contains("notice-web", StringComparison.OrdinalIgnoreCase)
                        ? "此站当前返回 JavaScript 公告页，尚无可读取的公开资源列表；请打开原站查看。"
                        : "此站尚未提供已适配的公开搜索接口；请打开原站查看公告与资源入口。");
            var articles = ParseGamer520(html, kind);
            if (articles.Length > 0)
                return Result(PublicResourceSearchStatus.Available, $"读取到 {articles.Length} 条公开资料；原站页面与网盘入口需在浏览器确认。", articles);
            if (html.Contains("captcha", StringComparison.OrdinalIgnoreCase) || html.Contains("challenge-platform", StringComparison.OrdinalIgnoreCase) ||
                html.Contains("Just a moment", StringComparison.OrdinalIgnoreCase))
                return Result(PublicResourceSearchStatus.RequiresBrowser, "网站要求浏览器验证，请打开原站完成操作后查看资源。");
            if (!Articles.IsMatch(html) && !html.Contains("no-results", StringComparison.OrdinalIgnoreCase))
                return Result(PublicResourceSearchStatus.RequiresBrowser, "页面未提供可识别的资源列表，不能判断是否没有资源；可用浏览器站内搜索或搜索公开索引。");
            return Result(PublicResourceSearchStatus.NoResults, "本页没有匹配的 Switch 游戏或金手指条目；可换用中文名、英文短名，或搜索公开索引。网站标题可能与文件名不同。");
            }
            return Result(PublicResourceSearchStatus.RequiresBrowser, "网站仍要求浏览器访问，请在浏览器站内搜索。");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return Result(PublicResourceSearchStatus.Unavailable, "查询超过 25 秒，已停止等待；可重试、用浏览器站内搜索或搜索公开索引。"); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or OperationCanceledException or RegexMatchTimeoutException)
        {
            return Result(PublicResourceSearchStatus.Unavailable, "公开页面读取失败：" + ex.Message);
        }
    }

    private static bool HasGamerSessionCookie(HttpResponseMessage response, Uri origin)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values)) return false;
        var cookies = new CookieContainer(); var examined = 0;
        foreach (var value in values)
        {
            if (++examined > 16 || value.Length > 4096) return false;
            var separator = value.IndexOf(';');
            var pair = value.AsSpan(0, separator >= 0 ? separator : value.Length);
            var equals = pair.IndexOf('=');
            if (equals <= 0 || equals == pair.Length - 1) return false;
            foreach (var character in pair[..equals])
                if (!char.IsAsciiLetterOrDigit(character) && !"!#$%&'*+-.^_`|~".Contains(character)) return false;
            try { cookies.SetCookies(origin, value); }
            catch (CookieException) { return false; }
        }
        return cookies.GetCookies(origin).Any(cookie => cookie.Value.Trim('"').Length != 0);
    }

    private static bool IsGamerCookieReload(string html, Uri requested)
    {
        // Interactive verification remains a browser operation. The observed
        // cookie initialization can include the site's passive Cloudflare JSD tag.
        if (Encoding.UTF8.GetByteCount(html) > 64 * 1024 || html.Contains("captcha", StringComparison.OrdinalIgnoreCase)
            || html.Contains("Just a moment", StringComparison.OrdinalIgnoreCase) || html.Contains("cf-turnstile", StringComparison.OrdinalIgnoreCase)) return false;
        var reloads = Pattern(@"\bwindow\s*\.\s*location\s*\.\s*href\s*=\s*(?<quote>[""'])(?<url>[^""'\r\n]+)\k<quote>\s*;").Matches(html);
        if (reloads.Count != 1 || !Uri.TryCreate(requested, reloads[0].Groups["url"].Value, out var target)) return false;
        return IsGamerOrigin(target) && target.AbsoluteUri.Equals(requested.AbsoluteUri, StringComparison.Ordinal);
    }

    private static async Task<(string Html, int Bytes)> ReadGamerHtmlAsync(HttpResponseMessage response, int budget, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > budget) throw new InvalidDataException("网站页面超过 4 MiB 读取预算");
        if (response.Content.Headers.ContentType?.MediaType is {} media && !media.Equals("text/html", StringComparison.OrdinalIgnoreCase)
            && !media.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("网站返回的内容不是公开 HTML 资源目录");
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream(); var buffer = new byte[65536]; int count;
        while ((count = await StreamReads.WithInactivityTimeoutAsync(input, buffer, ct, TimeSpan.FromSeconds(10))) != 0)
        {
            if (output.Length + count > budget) throw new InvalidDataException("网站页面超过 4 MiB 读取预算");
            output.Write(buffer, 0, count);
        }
        if (response.Content.Headers.ContentLength is {} declared && output.Length != declared)
            throw new InvalidDataException("网站页面传输长度与响应声明不符");
        return (Encoding.UTF8.GetString(output.ToArray()), checked((int)output.Length));
    }

    public static PublicResourceArticle[] ParseGamer520(string html, ResourceKind? kind)
    {
        ValidateQuery("", kind);
        if (kind == ResourceKind.Homebrew) return [];
        if (Encoding.UTF8.GetByteCount(html) > MaximumPageBytes) throw new InvalidDataException("网站页面超过 4 MiB 解析预算");
        var visible = HiddenContent.Replace(html, ""); var results = new List<PublicResourceArticle>();
        var seen = new HashSet<string>(StringComparer.Ordinal); var examined = 0;
        foreach (Match article in Articles.Matches(visible))
        {
            if (results.Count >= 100 || ++examined > 1000) break;
            var block = article.Value;
            var category = HasCategory(block, "jinshouzhi") ? ResourceKind.Cheat : HasCategory(block, "gameswitch") ? ResourceKind.Game : (ResourceKind?)null;
            if (category == null || kind != null && category != kind) continue;
            // An entry can carry multiple tags. A PC/simulator bundle is not a Switch package.
            if (category == ResourceKind.Game && (HasCategory(block, "pcplay") || HasCategory(block, "moniqi"))) continue;
            var heading = Heading.Match(block); if (!heading.Success) continue;
            foreach (Match anchor in Anchors.Matches(heading.Groups["body"].Value))
            {
                var href = Href.Match(anchor.Groups["attributes"].Value); if (!href.Success) continue;
                if (!TryArticleUrl(WebUtility.HtmlDecode(href.Groups["value"].Value), out var page)) continue;
                var title = Plain(anchor.Groups["text"].Value);
                if (title.Length == 0 || title.Length > 600 || !seen.Add(page)) continue;
                var identity = TitleId.Match(Plain(block));
                var time = TimeElement.Match(block); DateTimeOffset? published = null;
                if (time.Success && DateTimeOffset.TryParse(WebUtility.HtmlDecode(time.Groups["value"].Value), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var parsed)) published = parsed;
                results.Add(new(title, page, Gamer.Name, category.Value, identity.Success ? identity.Groups["id"].Value.ToUpperInvariant() : null)
                { PublishedAt = published });
                break;
            }
        }
        return [.. results];
    }

    private static bool HasCategory(string html, string category)
    {
        // Recognize the site's explicit category link rather than matching title text.
        foreach (Match anchor in Anchors.Matches(html))
        {
            var href = Href.Match(anchor.Groups["attributes"].Value);
            if (!href.Success || !Uri.TryCreate(new Uri(Gamer.HomeUrl), WebUtility.HtmlDecode(href.Groups["value"].Value), out var url)) continue;
            if (IsGamerOrigin(url) && url.AbsolutePath.TrimEnd('/').Equals("/" + category, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool TryArticleUrl(string href, out string page)
    {
        page = "";
        if (!Uri.TryCreate(new Uri(Gamer.HomeUrl), href, out var url) || !IsGamerOrigin(url) || !NumericArticlePath.IsMatch(url.AbsolutePath) ||
            url.Query.Length != 0 || url.Fragment.Length != 0) return false;
        page = new Uri(new Uri(Gamer.HomeUrl), url.AbsolutePath).AbsoluteUri; return true;
    }

    private static bool IsGamerOrigin(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        (uri.IdnHost.Equals("www.gamer520.com", StringComparison.OrdinalIgnoreCase) || uri.IdnHost.Equals("gamer520.com", StringComparison.OrdinalIgnoreCase));
    private static string Plain(string html) => string.Concat(Whitespace.Replace(WebUtility.HtmlDecode(StripTags.Replace(html, " ")), " ").Where(c => !char.IsControl(c))).Trim();
    private static void ValidateQuery(string query, ResourceKind? kind)
    {
        if ((kind.HasValue && !Enum.IsDefined(kind.Value)) || query == null || query.Length > 200 || query.Any(char.IsControl))
            throw new InvalidDataException("资源类别或搜索关键词无效（最多 200 字，不能包含控制字符）");
    }
}
