using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SwitchDesk.Core;

/// <summary>Public article search using the anonymous protocol published by the site's own frontend.</summary>
internal static class NstResourceSearch
{
    private const string NoticeUrl = "https://nsthwj.com/thwj/navigation/notice";
    private static readonly string VisitorId = Guid.NewGuid().ToString();
    private static readonly Regex BrandHost = new(@"^(?:www\.)?nsthwj\.(?:com\.)?[a-z]{2,63}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex NoticeLinks = new(@"https://[^\s\\<>""']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    internal static bool IsSiteOrigin(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0
        && BrandHost.IsMatch(uri.IdnHost) && !new[] { "local", "localhost", "internal", "test", "invalid", "example", "onion" }.Contains(uri.IdnHost.Split('.')[^1], StringComparer.OrdinalIgnoreCase);

    private sealed class BrowserRequired(string message) : IOException(message);

    internal static async Task<PublicResourceSearchResult> SearchAsync(HttpClient http, PublicResourceSite site, string query, ResourceKind? kind, CancellationToken ct)
    {
        var checkedAt = DateTimeOffset.UtcNow;
        var browserUrl = site.HomeUrl;
        PublicResourceSearchResult Result(PublicResourceSearchStatus status, string message, PublicResourceArticle[]? articles = null) =>
            new(site, articles ?? [], status, message, checkedAt, browserUrl);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            var origin = await ResolvePublishedOriginAsync(http, timeout.Token);
            browserUrl = PublicResourceSites.SearchUrl(site.Id, query, kind, origin.AbsoluteUri);
            if (kind is ResourceKind.Cheat or ResourceKind.Homebrew)
                return Result(PublicResourceSearchStatus.RequiresBrowser, "当前公开搜索按游戏分类；金手指和 Homebrew 请用浏览器站内搜索，在对应文章中核对。");

            // Establish the normal anonymous session. Never import browser cookies or account credentials.
            using (var status = await PostJsonAsync(http, new Uri(origin, "/thwj/traffic/session/status"), null, timeout.Token))
            {
                DemandSuccess(status.RootElement);
                if (!status.RootElement.TryGetProperty("data", out var active) || active.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new BrowserRequired("网站匿名访问状态格式已变化，请在浏览器完成正常访问。");
                if (active.ValueKind != JsonValueKind.True)
                    await IssueSessionAsync(http, origin, timeout.Token);
            }
            var api = new Uri(origin, "/thwj/game/query?pageNum=1&pageSize=12&type=switch&queryName=" + Uri.EscapeDataString(query.Trim()));
            using var response = await GetJsonAsync(http, api, true, timeout.Token);
            browserUrl = PublicResourceSites.SearchUrl(site.Id, query, kind, response.FinalUri.AbsoluteUri);
            var root = response.Document.RootElement;
            DemandSuccess(root);
            var articles = ParseArticles(root, response.FinalUri);
            return Result(articles.Length > 0 ? PublicResourceSearchStatus.Available : PublicResourceSearchStatus.NoResults,
                $"{response.FinalUri.IdnHost} · " + (articles.Length > 0 ? $"读取到 {articles.Length} 条 Switch 游戏文章；下载入口需在原站确认。" : "没有匹配的 Switch 游戏文章，可换用中文名或英文短名。"), articles);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (BrowserRequired ex) { return Result(PublicResourceSearchStatus.RequiresBrowser, ex.Message); }
        catch (JsonException) { return Result(PublicResourceSearchStatus.RequiresBrowser, "网站公告或搜索响应格式已变化，请查看官方公告或在浏览器站内搜索。"); }
        catch (OperationCanceledException) { return Result(PublicResourceSearchStatus.Unavailable, "查询超过 25 秒，已停止等待；可重试或在浏览器站内搜索。"); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or RegexMatchTimeoutException)
        { return Result(PublicResourceSearchStatus.Unavailable, "资源查询未完成：" + ex.Message + " · 可在浏览器站内搜索。"); }
    }

    internal static async Task<Uri> ResolvePublishedOriginAsync(HttpClient http, CancellationToken ct)
    {
        using var notice = await GetJsonAsync(http, new Uri(NoticeUrl), false, ct);
        var root = notice.Document.RootElement; DemandSuccess(root);
        if (!root.TryGetProperty("data", out var announcement) || announcement.ValueKind != JsonValueKind.Object
            || !announcement.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String)
            throw new BrowserRequired("官方公告没有提供可确认的新站入口，请在浏览器查看公告。");
        var origins = OfficialOrigins(Text(content));
        if (origins.Length == 0) throw new BrowserRequired("官方公告没有提供可确认的 NS 头号玩家域名，请在浏览器查看公告。");
        IOException? last = null;
        foreach (var candidate in origins)
        {
            try { return await ResolveOriginAsync(http, candidate, ct); }
            catch (BrowserRequired ex) when (!ex.Message.Contains("429", StringComparison.Ordinal)) { last = ex; }
            catch (HttpRequestException ex) { last = new IOException("官方入口暂时无法连接", ex); }
        }
        throw new BrowserRequired("官方公告中的入口暂不可用，请在浏览器查看公告。" + last?.Message);
    }

    internal static Uri[] OfficialOrigins(string content)
    {
        if (content.Length > 32768) throw new InvalidDataException("官方公告超出读取预算");
        return NoticeLinks.Matches(content).Select(m => m.Value.TrimEnd('.', ',', ';', '。', '，', '；'))
            .Select(value => Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null)
            .Where(uri => uri != null && IsSiteOrigin(uri) && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0)
            .Select(uri => new Uri(uri!.GetLeftPart(UriPartial.Authority) + "/")).Distinct().Take(3).ToArray();
    }

    private static async Task IssueSessionAsync(HttpClient http, Uri origin, CancellationToken ct)
    {
        using var challenge = await PostJsonAsync(http, new Uri(origin, "/thwj/traffic/session/challenge"), null, ct);
        DemandSuccess(challenge.RootElement);
        if (!challenge.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("challenge", out var value)
            || value.ValueKind != JsonValueKind.String
            || !data.TryGetProperty("difficultyBits", out var difficulty) || difficulty.ValueKind != JsonValueKind.Number
            || !difficulty.TryGetInt32(out var bits) || bits is < 0 or > 20)
            throw new BrowserRequired("网站访问验证格式或计算要求已变化，请在浏览器完成正常访问。");
        var text = Text(value);
        if (text.Length is < 1 or > 512) throw new BrowserRequired("网站访问验证格式已变化，请在浏览器完成正常访问。");
        var nonce = await Task.Run(() => SolveChallenge(text, bits, ct), ct);
        using var issue = await PostJsonAsync(http, new Uri(origin, "/thwj/traffic/session/issue"), new { challenge = text, nonce }, ct);
        DemandSuccess(issue.RootElement);
    }

    internal static string SolveChallenge(string challenge, int bits, CancellationToken ct)
    {
        if (bits is < 0 or > 20 || challenge.Length is < 1 or > 512) throw new BrowserRequired("网站访问验证要求已变化，请在浏览器完成访问。");
        var watch = Stopwatch.StartNew();
        var prefix = Encoding.UTF8.GetBytes(challenge + ":");
        var input = new byte[prefix.Length + 16]; prefix.CopyTo(input, 0);
        Span<byte> hash = stackalloc byte[32];
        for (var nonce = 0; nonce < 10_000_000; nonce++)
        {
            if ((nonce & 1023) == 0)
            {
                ct.ThrowIfCancellationRequested();
                if (watch.Elapsed > TimeSpan.FromSeconds(2)) break;
            }
            if (!System.Buffers.Text.Utf8Formatter.TryFormat(nonce, input.AsSpan(prefix.Length), out var digits)) break;
            SHA256.HashData(input.AsSpan(0, prefix.Length + digits), hash);
            var bytes = bits / 8; var remaining = bits % 8;
            var match = true;
            for (var i = 0; i < bytes; i++) if (hash[i] != 0) { match = false; break; }
            if (match && (remaining == 0 || (hash[bytes] & (0xFF << (8 - remaining))) == 0)) return nonce.ToString(CultureInfo.InvariantCulture);
        }
        throw new BrowserRequired("网站访问验证超过 2 秒计算预算，已停止；请在浏览器完成正常访问。");
    }

    private static PublicResourceArticle[] ParseArticles(JsonElement root, Uri origin)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("pageData", out var page) || page.ValueKind != JsonValueKind.Object
            || !page.TryGetProperty("data", out var rows) || rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 100)
            throw new InvalidDataException("网站搜索列表格式已变化");
        var articles = new List<PublicResourceArticle>(); var seen = new HashSet<long>();
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("id", out var idValue) || idValue.ValueKind != JsonValueKind.Number
                || !idValue.TryGetInt64(out var id) || id <= 0 || id > 9_007_199_254_740_991 || !seen.Add(id)
                || !row.TryGetProperty("name", out var title) || title.ValueKind != JsonValueKind.String) continue;
            // The public endpoint is explicitly scoped with type=switch. Older records
            // return gameType:null; a conflicting explicit platform is never accepted.
            if (row.TryGetProperty("gameType", out var platform) && platform.ValueKind != JsonValueKind.Null
                && (platform.ValueKind != JsonValueKind.String || Text(platform) != "switch")) continue;
            var name = Text(title).Trim();
            if (name.Length is < 1 or > 600 || name.Any(char.IsControl)) continue;
            var url = new Uri(origin.GetLeftPart(UriPartial.Authority) + "/game/switch/" + id.ToString(CultureInfo.InvariantCulture));
            DateTimeOffset? published = null;
            if (row.TryGetProperty("updateTime", out var time) && time.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(Text(time), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)) published = parsed;
            articles.Add(new(name, url.AbsoluteUri, "NS 头号玩家", ResourceKind.Game) { PublishedAt = published });
        }
        if (rows.GetArrayLength() > 0 && articles.Count == 0) throw new InvalidDataException("搜索响应没有可识别的 Switch 游戏条目");
        return articles.ToArray();
    }

    private static void DemandSuccess(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
            throw new InvalidDataException("网站未返回成功的公开查询结果");
    }

    private static string Text(JsonElement value)
    {
        try { return value.GetString() ?? throw new InvalidDataException("网站 JSON 文本为空"); }
        catch (InvalidOperationException ex) { throw new InvalidDataException("网站 JSON 文本编码无效", ex); }
    }

    private static HttpRequestMessage Request(HttpMethod method, Uri uri, bool visitor)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.UserAgent.ParseAdd("SwitchDesk/0.3 (public article metadata)");
        request.Headers.Accept.ParseAdd("application/json");
        if (visitor) request.Headers.Add("X-Visitor-Id", VisitorId);
        return request;
    }

    private sealed record JsonResponse(JsonDocument Document, Uri FinalUri) : IDisposable { public void Dispose() => Document.Dispose(); }
    private static async Task<Uri> ResolveOriginAsync(HttpClient http, Uri uri, CancellationToken ct)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var redirects = 0; redirects <= 3; redirects++)
        {
            if (!IsSiteOrigin(uri) || !visited.Add(uri.AbsoluteUri)) throw new BrowserRequired("网站入口跳转不受支持或形成循环，请查看官方公告。");
            using var request = Request(HttpMethod.Get, uri, false);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.RequestMessage?.RequestUri is {} actual && actual.AbsoluteUri != uri.AbsoluteUri)
                throw new BrowserRequired("网站发生未确认的自动跳转，请查看官方公告。");
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is {} location)
            { uri = new Uri(uri, location); continue; }
            if (!response.IsSuccessStatusCode) throw new BrowserRequired($"网站入口返回 HTTP {(int)response.StatusCode}，请在浏览器查看官方公告。");
            return new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");
        }
        throw new BrowserRequired("网站入口跳转次数过多，请在浏览器查看官方公告。");
    }
    private static async Task<JsonResponse> GetJsonAsync(HttpClient http, Uri uri, bool visitor, CancellationToken ct)
    {
        var searchQuery = uri.AbsolutePath == "/thwj/game/query" ? uri.Query : null;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var redirects = 0; redirects <= 3; redirects++)
        {
            if (!IsSiteOrigin(uri) || !visited.Add(uri.AbsoluteUri)) throw new BrowserRequired("网站跳转地址不受支持或形成循环，请查看官方公告。");
            using var request = Request(HttpMethod.Get, uri, visitor);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.RequestMessage?.RequestUri is {} actual && actual.AbsoluteUri != uri.AbsoluteUri)
                throw new BrowserRequired("网站发生未确认的自动跳转，请查看官方公告。");
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is {} location)
            {
                var next = new Uri(uri, location);
                // Only brand domains reached through the official announcement or their own redirect are accepted.
                if (!IsSiteOrigin(next)) throw new BrowserRequired("网站跳转到了未确认的外站，请查看官方公告。");
                if (searchQuery != null)
                {
                    if (next.AbsolutePath != "/thwj/game/query") throw new BrowserRequired("网站搜索接口发生跳转，请在浏览器站内搜索。");
                    next = new UriBuilder(next) { Query = searchQuery.TrimStart('?') }.Uri;
                }
                if (next.IdnHost != uri.IdnHost) visitor = false;
                uri = next; continue;
            }
            return new(await ReadJsonAsync(response, ct), uri);
        }
        throw new BrowserRequired("网站跳转次数过多，请在浏览器查看官方公告。");
    }

    private static async Task<JsonDocument> PostJsonAsync(HttpClient http, Uri uri, object? body, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Post, uri, true);
        request.Content = new StringContent(body == null ? "" : JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.RequestMessage?.RequestUri is {} actual && actual.AbsoluteUri != uri.AbsoluteUri)
            throw new BrowserRequired("匿名访问会话发生跳转，请在浏览器重新访问原站。");
        return await ReadJsonAsync(response, ct);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if ((int)response.StatusCode is >= 300 and < 400 or 401 or 403 or 428 or 429)
            throw new BrowserRequired($"网站返回 HTTP {(int)response.StatusCode}，匿名访问受限；已停止重试，可在浏览器站内搜索。");
        if (!response.IsSuccessStatusCode) throw new IOException($"网站返回 HTTP {(int)response.StatusCode}");
        if (response.Content.Headers.ContentLength > PublicResourceSites.MaximumPageBytes) throw new InvalidDataException("网站响应超过 4 MiB");
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream(); var buffer = new byte[65536]; int count;
        while ((count = await StreamReads.WithInactivityTimeoutAsync(input, buffer, ct, TimeSpan.FromSeconds(10))) != 0)
        {
            if (output.Length + count > PublicResourceSites.MaximumPageBytes) throw new InvalidDataException("网站响应超过 4 MiB");
            output.Write(buffer, 0, count);
        }
        if (response.Content.Headers.ContentLength is {} declared && output.Length != declared) throw new InvalidDataException("网站响应传输不完整");
        return JsonDocument.Parse(output.ToArray(), new JsonDocumentOptions { MaxDepth = 24 });
    }
}
