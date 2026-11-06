using System.Net;
using System.IO;
using System.IO.Compression;
using System.Net.Sockets;
using System.Reflection;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SwitchDesk.App;
using SwitchDesk.Core;

namespace SwitchDesk.Tests;

internal static partial class Program
{
    private static HttpResponseMessage SiteJson(HttpRequestMessage request, object body) => new(HttpStatusCode.OK)
    { RequestMessage = request, Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    private static object Notice(string domains = "https://nsthwj.cn") => new { success = true, data = new { content = domains } };
    private static object SiteRows() => new { success = true, data = new { pageData = new { data = new object[]
    {
        new { id = 11, name = "Switch fixture", gameType = "switch", updateTime = "2026-11-04" },
        new { id = 12, name = "Legacy fixture", gameType = (string?)null, updateTime = "2026-11-04" },
        new { id = 13, name = "PC fixture", gameType = "pc" },
        new { id = 11, name = "Duplicate fixture", gameType = "switch" }
    } } } };

    private static async Task ResourceSiteSearchTestsAsync()
    {
        await Test("NST searches the published migrated domain with a normal bounded anonymous session", async () =>
        {
            var visitor = ""; var issued = false; var calls = 0; var query = "PICO PARK & #中文";
            using var http = new HttpClient(new FixtureHttp(request =>
            {
                calls++; var uri = request.RequestUri!;
                if (uri.IdnHost == "nsthwj.com") { Assert(!request.Headers.Contains("X-Visitor-Id")); return SiteJson(request, Notice("https://advert.invalid https://nsthwj.vip")); }
                if (uri.AbsolutePath == "/")
                {
                    Assert(!request.Headers.Contains("X-Visitor-Id"));
                    if (uri.IdnHost == "nsthwj.vip") return new(HttpStatusCode.MovedPermanently) { RequestMessage = request, Headers = { Location = new Uri("https://www.nsthwj.vip/") } };
                    return new(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent("public page") };
                }
                Assert(uri.IdnHost == "www.nsthwj.vip" && !request.Headers.Contains("Authorization"));
                var current = request.Headers.GetValues("X-Visitor-Id").Single();
                if (visitor.Length == 0) visitor = current; else Assert(visitor == current);
                if (uri.AbsolutePath.EndsWith("/status", StringComparison.Ordinal)) return SiteJson(request, new { success = true, data = false });
                if (uri.AbsolutePath.EndsWith("/challenge", StringComparison.Ordinal)) return SiteJson(request, new { success = true, data = new { challenge = "synthetic-search", difficultyBits = 8 } });
                if (uri.AbsolutePath.EndsWith("/issue", StringComparison.Ordinal))
                {
                    using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                    var hash = SHA256.HashData(Encoding.UTF8.GetBytes("synthetic-search:" + body.RootElement.GetProperty("nonce").GetString()));
                    Assert(hash[0] == 0); issued = true; return SiteJson(request, new { success = true, data = true });
                }
                Assert(issued && uri.AbsolutePath == "/thwj/game/query" && uri.Query.Contains("queryName=" + Uri.EscapeDataString(query), StringComparison.Ordinal));
                return SiteJson(request, SiteRows());
            }));
            var result = await PublicResourceSites.SearchAsync(http, "nsthwj", query, ResourceKind.Game, default);
            Assert(result.Status == PublicResourceSearchStatus.Available && result.Articles.Length == 2 && calls == 7, result.Message);
            Assert(result.SearchUrl == "https://www.nsthwj.vip/?q=" + Uri.EscapeDataString(query));
            Assert(result.Articles.All(a => a.PageUrl.StartsWith("https://www.nsthwj.vip/game/switch/", StringComparison.Ordinal)));
        });

        await Test("NST official alternate domains are tried after a failed published primary", async () =>
        {
            var primary = 0; var backup = 0;
            using var http = new HttpClient(new FixtureHttp(request =>
            {
                var uri = request.RequestUri!;
                if (uri.IdnHost == "nsthwj.com") return SiteJson(request, Notice("https://nsthwj.cn https://nsthwj.vip"));
                if (uri.IdnHost == "nsthwj.cn") { primary++; return new(HttpStatusCode.ServiceUnavailable) { RequestMessage = request }; }
                backup++;
                if (uri.AbsolutePath == "/") return new(HttpStatusCode.OK) { RequestMessage = request };
                if (uri.AbsolutePath.EndsWith("/status", StringComparison.Ordinal)) return SiteJson(request, new { success = true, data = true });
                return SiteJson(request, SiteRows());
            }));
            var result = await PublicResourceSites.SearchAsync(http, "nsthwj", "fixture", null, default);
            Assert(result.Status == PublicResourceSearchStatus.Available && primary == 1 && backup == 3 && result.SearchUrl.StartsWith("https://nsthwj.vip/", StringComparison.Ordinal));
        });

        await Test("NST redirects reject foreign hosts and loops without contacting them", async () =>
        {
            foreach (var target in new[] { "https://nsthwj.cn/", "https://ads.invalid/", "http://nsthwj.vip/", "https://nsthwj.cn.attacker.invalid/", "https://127.0.0.1/", "https://user@nsthwj.vip/" })
            {
                var calls = 0;
                using var http = new HttpClient(new FixtureHttp(request =>
                {
                    calls++;
                    if (calls == 1) return SiteJson(request, Notice());
                    Assert(calls == 2 && request.RequestUri!.IdnHost == "nsthwj.cn");
                    return new(HttpStatusCode.Found) { RequestMessage = request, Headers = { Location = new Uri(target) } };
                }));
                var result = await PublicResourceSites.SearchAsync(http, "nsthwj", "fixture", null, default);
                Assert(calls == 2 && result.Status == PublicResourceSearchStatus.RequiresBrowser && result.Articles.Length == 0);
            }
        });

        await Test("NST announcements do not turn advertisements or local addresses into resource hosts", async () =>
        {
            var calls = 0;
            using var http = new HttpClient(new FixtureHttp(request =>
            { calls++; return SiteJson(request, Notice("https://ads.invalid https://nsthwj.local https://nsthwj.cn:8443 https://user@nsthwj.vip/ https://nsthwj.cn.evil.invalid/")); }));
            var result = await PublicResourceSites.SearchAsync(http, "nsthwj", "fixture", null, default);
            Assert(calls == 1 && result.Status == PublicResourceSearchStatus.RequiresBrowser);
        });

        await Test("NST malformed JSON shapes are explicit failures and never issue a session or publish empty success", async () =>
        {
            var invalid = new[] { "null", "[]", "true", "{\"success\":true,\"data\":[]}", "{\"success\":true,\"data\":\"wrong\"}", "{\"success\":true,\"data\":{\"content\":\"\\uD800\"}}" };
            foreach (var shape in invalid)
            {
                var calls = 0;
                using var http = new HttpClient(new FixtureHttp(request =>
                { calls++; return new(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(shape, Encoding.UTF8, "application/json") }; }));
                var result = await PublicResourceSites.SearchAsync(http, "nsthwj", "fixture", null, default);
                Assert(calls == 1 && result.Status is PublicResourceSearchStatus.RequiresBrowser or PublicResourceSearchStatus.Unavailable && result.Articles.Length == 0);
            }
            foreach (var shape in new[] { "[]", "null", "{\"success\":true,\"data\":{\"pageData\":[]}}", "{\"success\":true,\"data\":{\"pageData\":{\"data\":[null,[],{\"id\":\"1\",\"name\":\"wrong\"}]}}}" })
            {
                using var http = new HttpClient(new FixtureHttp(request =>
                {
                    var uri = request.RequestUri!;
                    if (uri.IdnHost == "nsthwj.com") return SiteJson(request, Notice());
                    if (uri.AbsolutePath == "/") return new(HttpStatusCode.OK) { RequestMessage = request };
                    if (uri.AbsolutePath.EndsWith("/status", StringComparison.Ordinal)) return SiteJson(request, new { success = true, data = true });
                    return new(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(shape, Encoding.UTF8, "application/json") };
                }));
                var result = await PublicResourceSites.SearchAsync(http, "nsthwj", "fixture", null, default);
                Assert(result.Status == PublicResourceSearchStatus.Unavailable && result.Articles.Length == 0);
            }
        });

        await Test("NST session restrictions stop retries and challenge limits prevent excessive computation", async () =>
        {
            foreach (var restricted in new[] { 428, 429 })
            {
                var sessionCalls = 0;
                using var http = new HttpClient(new FixtureHttp(request =>
                {
                    var uri = request.RequestUri!;
                    if (uri.IdnHost == "nsthwj.com") return SiteJson(request, Notice());
                    if (uri.AbsolutePath == "/") return new(HttpStatusCode.OK) { RequestMessage = request };
                    sessionCalls++; return new((HttpStatusCode)restricted) { RequestMessage = request };
                }));
                var result = await PublicResourceSites.SearchAsync(http, "nsthwj", "fixture", null, default);
                Assert(sessionCalls == 1 && result.Status == PublicResourceSearchStatus.RequiresBrowser && result.Message.Contains(restricted.ToString(), StringComparison.Ordinal));
            }
            foreach (var bits in new[] { -1, 21 }) Expect<IOException>(() => NstResourceSearch.SolveChallenge("synthetic", bits, default));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Expect<OperationCanceledException>(() => NstResourceSearch.SolveChallenge("synthetic", 10, cancelled.Token));
        });

        await Test("NST malformed challenge fields stop before issuing a session or searching", async () =>
        {
            foreach (var shape in new[] { "[]", "{\"challenge\":\"fixture\",\"difficultyBits\":\"8\"}", "{\"challenge\":5,\"difficultyBits\":8}", "{\"challenge\":\"fixture\",\"difficultyBits\":21}" })
            {
                var challengeCalls = 0;
                using var http = new HttpClient(new FixtureHttp(request =>
                {
                    var uri = request.RequestUri!;
                    if (uri.IdnHost == "nsthwj.com") return SiteJson(request, Notice());
                    if (uri.AbsolutePath == "/") return new(HttpStatusCode.OK) { RequestMessage = request };
                    if (uri.AbsolutePath.EndsWith("/status", StringComparison.Ordinal)) return SiteJson(request, new { success = true, data = false });
                    Assert(uri.AbsolutePath.EndsWith("/challenge", StringComparison.Ordinal)); challengeCalls++;
                    return new(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent("{\"success\":true,\"data\":" + shape + "}", Encoding.UTF8, "application/json") };
                }));
                var result = await PublicResourceSites.SearchAsync(http, "nsthwj", "fixture", null, default);
                Assert(challengeCalls == 1 && result.Status == PublicResourceSearchStatus.RequiresBrowser);
            }
        });

        await Test("NST malformed anonymous session status never starts a challenge", async () =>
        {
            foreach (var shape in new[] { "null", "[]", "\"false\"", "{}" })
            {
                var statusCalls = 0;
                using var http = new HttpClient(new FixtureHttp(request =>
                {
                    var uri = request.RequestUri!;
                    if (uri.IdnHost == "nsthwj.com") return SiteJson(request, Notice());
                    if (uri.AbsolutePath == "/") return new(HttpStatusCode.OK) { RequestMessage = request };
                    Assert(uri.AbsolutePath.EndsWith("/status", StringComparison.Ordinal)); statusCalls++;
                    return new(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent("{\"success\":true,\"data\":" + shape + "}", Encoding.UTF8, "application/json") };
                }));
                var result = await PublicResourceSites.SearchAsync(http, "nsthwj", "fixture", null, default);
                Assert(statusCalls == 1 && result.Status == PublicResourceSearchStatus.RequiresBrowser);
            }
        });

        await Test("public website search cancellation interrupts outstanding network requests", async () =>
        {
            foreach (var site in new[] { "nsthwj", "gamer520" })
            {
                using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
                using var http = new HttpClient(new WaitingResourceSite());
                await Throws<OperationCanceledException>(() => PublicResourceSites.SearchAsync(http, site, "fixture", null, cancellation.Token));
            }
        });

        await Test("public search URLs preserve keywords and verified migration for browser and index search", () => Sync(() =>
        {
            var query = "new query & 中文";
            var model = new ResourcesViewModel(); model.SelectedSource = model.Sources.Single(s => s.Id == "nsthwj");
            model.SetPublicSearchResult(new(PublicResourceSites.Get("nsthwj"), [], PublicResourceSearchStatus.NoResults, "fixture", DateTimeOffset.UtcNow, "https://nsthwj.vip/?q=old"));
            var url = PublicResourceSites.SearchUrl("nsthwj", query, ResourceKind.Game, model.ResolvedSiteSearchUrl);
            Assert(url == "https://nsthwj.vip/?q=" + Uri.EscapeDataString(query));
            var index = new Uri(PublicResourceSites.IndexSearchUrl("nsthwj", query, ResourceKind.Game, model.ResolvedSiteSearchUrl));
            Assert(index.IdnHost == "www.bing.com" && Uri.UnescapeDataString(index.Query).Contains("site:nsthwj.vip " + query, StringComparison.Ordinal));
            Assert(PublicResourceSites.SearchUrl("gamer520", query, ResourceKind.Homebrew).Contains(Uri.EscapeDataString(query), StringComparison.Ordinal));
            model.SelectedSource = model.Sources[0]; Assert(model.ResolvedSiteSearchUrl == null && !model.IsNstSite && model.IsPublicSite);
        }));

        await Test("offline notice still allows browser fallbacks on the last confirmed host and honors cancellation", async () =>
        {
            var model = new ResourcesViewModel();
            model.SelectedSource = model.Sources.Single(s => s.Id == "nsthwj");
            var site = PublicResourceSites.Get("nsthwj");
            model.SetPublicSearchResult(new(site, [], PublicResourceSearchStatus.NoResults, "fixture", DateTimeOffset.UtcNow, "https://nsthwj.vip/?q=old"));
            model.SetPublicSearchResult(new(site, [], PublicResourceSearchStatus.Unavailable, "notice offline", DateTimeOffset.UtcNow, site.HomeUrl));
            using var http = new HttpClient(new FixtureHttp(request => new(HttpStatusCode.ServiceUnavailable) { RequestMessage = request }));
            var fallback = await PublicResourceSites.ResolveSearchUrlAsync(http, site.Id, "PICO PARK", ResourceKind.Game, default, model.ResolvedSiteSearchUrl);
            Assert(fallback == "https://nsthwj.vip/?q=PICO%20PARK" &&
                Uri.UnescapeDataString(PublicResourceSites.IndexSearchUrl(site.Id, "PICO PARK", ResourceKind.Game, fallback)).Contains("site:nsthwj.vip PICO PARK", StringComparison.Ordinal));
            var unknown = await PublicResourceSites.ResolveSearchUrlAsync(http, site.Id, "PICO", null, default, "https://foreign.invalid/");
            Assert(unknown == "https://nsthwj.cn/?q=PICO");
            await Throws<OperationCanceledException>(() => PublicResourceSites.ResolveSearchUrlAsync(http, site.Id, "PICO", null, new CancellationToken(true), fallback));
        });

        await Test("unrecognized public HTML is a browser-required response rather than a false empty search", async () =>
        {
            using var http = new HttpClient(new FixtureHttp(request => new(HttpStatusCode.OK)
            { RequestMessage = request, Content = new StringContent("<html><body>new frontend</body></html>", Encoding.UTF8, "text/html") }));
            var result = await PublicResourceSites.SearchAsync(http, "gamer520", "fixture", ResourceKind.Game, default);
            Assert(result.Status == PublicResourceSearchStatus.RequiresBrowser && result.Articles.Length == 0);
        });

        await Test("the application resource HTTP factory decodes gzip responses over real loopback HTTP", async () =>
        {
            const string expected = "{\"resource\":\"gzip fixture\"}";
            using var compressed = new MemoryStream();
            using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, true)) gzip.Write(Encoding.UTF8.GetBytes(expected));
            var bytes = compressed.ToArray();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var address = (IPEndPoint)listener.LocalEndpoint;
            var serve = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(cancellation.Token);
                await using var wire = peer.GetStream(); var request = new StringBuilder(); var buffer = new byte[1];
                while (request.Length < 32768 && !request.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                { Assert(await wire.ReadAsync(buffer, cancellation.Token) == 1); request.Append((char)buffer[0]); }
                Assert(request.ToString().Contains("gzip", StringComparison.OrdinalIgnoreCase));
                var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Encoding: gzip\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
                await wire.WriteAsync(header, cancellation.Token); await wire.WriteAsync(bytes, cancellation.Token);
            }, cancellation.Token);
            var factory = typeof(MainWindow).GetMethod("ResourceHttp", BindingFlags.Static | BindingFlags.NonPublic)!;
            using var http = (HttpClient)factory.Invoke(null, null)!;
            var text = await http.GetStringAsync($"http://127.0.0.1:{address.Port}/resource", cancellation.Token);
            Assert(text == expected); await serve;
        });
    }

    private sealed class WaitingResourceSite : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { await Task.Delay(Timeout.Infinite, cancellationToken); throw new Exception("cancelled query unexpectedly resumed"); }
    }
}
