using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using SwitchDesk.Core;

namespace SwitchDesk.Tests;

internal static partial class Program
{
    private const string GamerFixtureArticle = "<article><a href='/gameswitch'>Switch</a><h2><a href='/123.html'>Fixture game</a></h2></article>";
    private static HttpResponseMessage GamerReload(HttpRequestMessage request, string? target = null, string? cookie = "anonymous=fixture; Path=/; HttpOnly")
    {
        var response = new HttpResponseMessage(HttpStatusCode.Forbidden) { RequestMessage = request,
            Content = new StringContent("<html><title></title></html><script>window.location.href = \"" +
                (target ?? request.RequestUri!.PathAndQuery) + "\";</script><script src='/cdn-cgi/challenge-platform/scripts/jsd/main.js'></script>", Encoding.UTF8, "text/html") };
        if (cookie != null) response.Headers.TryAddWithoutValidation("Set-Cookie", cookie);
        return response;
    }

    private static async Task GamerCookieSearchTestsAsync()
    {
        await Test("Gamer anonymous cookie initialization reloads only the exact requested URL once", async () =>
        {
            var calls = 0; var query = "PICO PARK & 中文"; var cookieJar = new CookieContainer();
            using var http = new HttpClient(new FixtureHttp(request =>
            {
                calls++;
                Assert(request.RequestUri!.AbsoluteUri == PublicResourceSites.SearchUrl("gamer520", query, ResourceKind.Game));
                Assert(!request.Headers.Contains("Authorization") && !request.Headers.Contains("X-Visitor-Id"));
                if (calls == 1)
                {
                    // Models the standard handler cookie jar. No cookie value is
                    // copied into app settings or another origin.
                    var response = GamerReload(request);
                    cookieJar.SetCookies(request.RequestUri, response.Headers.GetValues("Set-Cookie").Single());
                    return response;
                }
                Assert(cookieJar.GetCookies(request.RequestUri).Count == 1);
                return new(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(GamerFixtureArticle, Encoding.UTF8, "text/html") };
            }));
            var result = await PublicResourceSites.SearchAsync(http, "gamer520", query, ResourceKind.Game, default);
            Assert(calls == 2 && result.Status == PublicResourceSearchStatus.Available && result.Articles.Length == 1);
            Assert(result.Articles[0].PageUrl == "https://www.gamer520.com/123.html");
        });

        await Test("Gamer forbidden responses without an applicable live cookie do not retry", async () =>
        {
            foreach (var cookie in new string?[] { null, "broken", "empty=", "empty=\"\"; Path=/", "bad name=fixture; Path=/", "anonymous=fixture; Domain=other.invalid; Path=/", "anonymous=fixture; Max-Age=0; Path=/" })
            {
                var calls = 0;
                using var http = new HttpClient(new FixtureHttp(request => { calls++; return GamerReload(request, cookie: cookie); }));
                var result = await PublicResourceSites.SearchAsync(http, "gamer520", "fixture", null, default);
                Assert(calls == 1 && result.Status == PublicResourceSearchStatus.RequiresBrowser && result.Articles.Length == 0);
            }
        });

        await Test("Gamer cookie reload never follows foreign URLs, changed queries or interactive verification", async () =>
        {
            foreach (var target in new[] { "https://external.invalid/?s=fixture", "/?s=other", "/?s=fixture#fragment", "https://user@www.gamer520.com/?s=fixture" })
            {
                var calls = 0;
                using var http = new HttpClient(new FixtureHttp(request => { calls++; return GamerReload(request, target); }));
                var result = await PublicResourceSites.SearchAsync(http, "gamer520", "fixture", null, default);
                Assert(calls == 1 && result.Status == PublicResourceSearchStatus.RequiresBrowser);
            }
            foreach (var verification in new[] { "captcha", "Just a moment", "cf-turnstile" })
            {
                var calls = 0;
                using var http = new HttpClient(new FixtureHttp(request =>
                {
                    calls++; var response = GamerReload(request);
                    response.Content = new StringContent("<p>" + verification + "</p><script>window.location.href='/?s=fixture';</script>", Encoding.UTF8, "text/html");
                    return response;
                }));
                Assert((await PublicResourceSites.SearchAsync(http, "gamer520", "fixture", null, default)).Status == PublicResourceSearchStatus.RequiresBrowser && calls == 1);
            }
        });

        await Test("Gamer repeated cookie initialization stops after the first reload", async () =>
        {
            var calls = 0;
            using var http = new HttpClient(new FixtureHttp(request => { calls++; return GamerReload(request); }));
            var result = await PublicResourceSites.SearchAsync(http, "gamer520", "fixture", null, default);
            Assert(calls == 2 && result.Status == PublicResourceSearchStatus.RequiresBrowser && result.Articles.Length == 0);
        });

        await Test("Gamer cookie initialization and result page share one byte budget and enforce exact reads", async () =>
        {
            foreach (var claimed in new long[] { PublicResourceSites.MaximumPageBytes, 100 })
            {
                var calls = 0;
                using var http = new HttpClient(new FixtureHttp(request =>
                {
                    if (++calls == 1) return GamerReload(request);
                    var response = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent([1, 2, 3]) };
                    response.Content.Headers.ContentLength = claimed; return response;
                }));
                var result = await PublicResourceSites.SearchAsync(http, "gamer520", "fixture", null, default);
                Assert(calls == 2 && result.Status == PublicResourceSearchStatus.Unavailable && result.Articles.Length == 0);
            }
        });

        await Test("Gamer cancellation also stops a pending anonymous reload", async () =>
        {
            using var cancellation = new CancellationTokenSource(); var calls = 0;
            using var http = new HttpClient(new PendingGamerReload(request => { calls++; return GamerReload(request); }, cancellation));
            await Throws<OperationCanceledException>(() => PublicResourceSites.SearchAsync(http, "gamer520", "fixture", null, cancellation.Token));
            Assert(calls == 1);
        });
    }

    private sealed class PendingGamerReload(Func<HttpRequestMessage, HttpResponseMessage> first, CancellationTokenSource cancellation) : HttpMessageHandler
    {
        private bool sent;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (!sent) { sent = true; return first(request); }
            cancellation.CancelAfter(30);
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("cancelled reload resumed");
        }
    }
}
