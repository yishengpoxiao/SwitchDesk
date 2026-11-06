using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using SwitchDesk.Core;

namespace SwitchDesk.Tests;

internal static partial class Program
{
    private static async Task PublicResourceSitesTestsAsync()
    {
        string Article(string url, string title, string category, string extra = "") =>
            $"<article class='post'><header><span class='meta-category'><a href='/{category}'>category</a></span><h2 class='entry-title'><a href='{url}'>{title}</a></h2></header>{extra}<time datetime='2026-11-04T08:00:00+08:00'>today</time></article>";

        await Test("public resource search URLs use the confirmed public form and encode queries", () => Sync(() =>
        {
            var query = "死亡细胞 & ? #";
            var search = PublicResourceSites.SearchUrl("gamer520", query, ResourceKind.Game);
            Assert(search == "https://www.gamer520.com/?s=" + Uri.EscapeDataString(query));
            Assert(PublicResourceSites.SearchUrl("gamer520", "", ResourceKind.Cheat) == "https://www.gamer520.com/jinshouzhi");
            Assert(PublicResourceSites.SearchUrl("nsthwj", query, ResourceKind.Game) == "https://nsthwj.cn/?q=" + Uri.EscapeDataString(query));
            Expect<InvalidDataException>(() => PublicResourceSites.SearchUrl("other", "", ResourceKind.Game));
            Expect<InvalidDataException>(() => PublicResourceSites.SearchUrl("gamer520", "line\r\ninjection", ResourceKind.Game));
            Expect<InvalidDataException>(() => PublicResourceSites.SearchUrl("gamer520", "", (ResourceKind)999));
        }));

        await Test("public resource metadata filters Switch categories and never treats article pages as downloads", () => Sync(() =>
        {
            var html = Article("/101.html", "Switch &amp; Game", "gameswitch") +
                Article("/102.html", "PC version", "pcplay") +
                Article("/103.html", "Cheat TitleID: 0100646009fbe000", "jinshouzhi") +
                Article("/104.html", "Switch simulator bundle", "gameswitch", "<a href='/pcplay'>PC PLAY</a>") +
                Article("/105.html", "Name says Switch 金手指", "pcplay");
            var games = PublicResourceSites.ParseGamer520(html, ResourceKind.Game);
            var cheats = PublicResourceSites.ParseGamer520(html, ResourceKind.Cheat);
            Assert(games.Length == 1 && games[0].Title == "Switch & Game" && games[0].TitleId == null);
            Assert(games[0].PageUrl == "https://www.gamer520.com/101.html" && games[0].PublishedAt?.Offset == TimeSpan.FromHours(8));
            Assert(cheats.Length == 1 && cheats[0].TitleId == "0100646009FBE000" && cheats[0].Kind == ResourceKind.Cheat);
            Assert(PublicResourceSites.ParseGamer520(html, null).Length == 2 && PublicResourceSites.ParseGamer520(html, ResourceKind.Homebrew).Length == 0);
        }));

        await Test("public resource metadata excludes hidden scripts, foreign origins, unsafe links and duplicates", () => Sync(() =>
        {
            var good = Article("/201.html", "<span>Visible</span>", "gameswitch");
            var hidden = "<script>" + Article("/202.html", "Script fixture", "gameswitch") + "</script>";
            var comment = "<!--" + Article("/203.html", "Comment fixture", "gameswitch") + "-->";
            var bad = new[] { "javascript:alert(1)", "https://www.gamer520.com.attacker.invalid/204.html", "https://user@www.gamer520.com/204.html", "https://pan.example/204.html", "/204.html?redirect=evil", "/go?post_id=204", "/download.nsp" };
            var html = good + hidden + comment + Article("https://gamer520.com/201.html", "Alias duplicate", "gameswitch") +
                string.Concat(bad.Select((url, index) => Article(url, "unsafe " + index, "gameswitch")));
            var results = PublicResourceSites.ParseGamer520(html, ResourceKind.Game);
            Assert(results.Length == 1 && results[0].Title == "Visible");
            Expect<InvalidDataException>(() => PublicResourceSites.ParseGamer520(new string('x', PublicResourceSites.MaximumPageBytes + 1), null));
        }));

        await Test("public resource HTTP failures and JavaScript announcements have explicit states", async () =>
        {
            using (var blocked = new HttpClient(new FixtureHttp(request => new(HttpStatusCode.Forbidden) { RequestMessage = request })))
            {
                var result = await PublicResourceSites.SearchAsync(blocked, "gamer520", "fixture", ResourceKind.Game, default);
                Assert(result.Status == PublicResourceSearchStatus.RequiresBrowser && result.Articles.Length == 0 && result.Message.Contains("403", StringComparison.Ordinal));
            }
            var calls = 0;
            using var announcement = new HttpClient(new FixtureHttp(request =>
            {
                calls++; Assert(request.RequestUri?.AbsoluteUri == "https://nsthwj.com/thwj/navigation/notice");
                return new(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent("<noscript>notice-web needs JavaScript</noscript>", Encoding.UTF8, "text/html") };
            }));
            var notice = await PublicResourceSites.SearchAsync(announcement, "nsthwj", "fixture", null, default);
            Assert(calls == 1 && notice.Status == PublicResourceSearchStatus.RequiresBrowser && notice.Message.Contains("公告", StringComparison.Ordinal));
            var unsupported = await PublicResourceSites.SearchAsync(announcement, "gamer520", "fixture", ResourceKind.Homebrew, default);
            Assert(calls == 1 && unsupported.Status == PublicResourceSearchStatus.RequiresBrowser);
        });

        await Test("public resource HTTP parses bounded metadata and refuses redirects and non-HTML payloads", async () =>
        {
            using (var http = new HttpClient(new FixtureHttp(request => new(HttpStatusCode.OK)
            { RequestMessage = request, Content = new StringContent(Article("/301.html", "Fixture", "gameswitch"), Encoding.UTF8, "text/html") })))
            {
                var result = await PublicResourceSites.SearchAsync(http, "gamer520", "fixture", ResourceKind.Game, default);
                Assert(result.Status == PublicResourceSearchStatus.Available && result.Articles.Length == 1);
            }
            using (var redirect = new HttpClient(new FixtureHttp(request => new(HttpStatusCode.Found)
            { RequestMessage = request, Headers = { Location = new Uri("https://external.invalid/") } })))
                Assert((await PublicResourceSites.SearchAsync(redirect, "gamer520", "fixture", null, default)).Status == PublicResourceSearchStatus.RequiresBrowser);
            using (var payload = new HttpClient(new FixtureHttp(request => new(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent([1, 2, 3]) })))
            {
                // No content type is also common for small servers; bytes cannot
                // become entries unless the explicit HTML article structure parses.
                Assert((await PublicResourceSites.SearchAsync(payload, "gamer520", "fixture", null, default)).Articles.Length == 0);
            }
            using (var binary = new HttpClient(new FixtureHttp(request => new(HttpStatusCode.OK)
            { RequestMessage = request, Content = new StringContent("binary fixture", Encoding.UTF8, "application/octet-stream") })))
                Assert((await PublicResourceSites.SearchAsync(binary, "gamer520", "fixture", null, default)).Status == PublicResourceSearchStatus.Unavailable);
            foreach (var length in new long[] { PublicResourceSites.MaximumPageBytes + 1, 100 })
            {
                using var oversized = new HttpClient(new FixtureHttp(request =>
                {
                    var response = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent([1, 2, 3]) };
                    response.Content.Headers.ContentLength = length; return response;
                }));
                Assert((await PublicResourceSites.SearchAsync(oversized, "gamer520", "fixture", null, default)).Status == PublicResourceSearchStatus.Unavailable);
            }
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            using var never = new HttpClient(new FixtureHttp(request => throw new Exception("cancelled request reached network fixture")));
            await Throws<OperationCanceledException>(() => PublicResourceSites.SearchAsync(never, "gamer520", "fixture", null, cancelled.Token));
        });
    }
}
