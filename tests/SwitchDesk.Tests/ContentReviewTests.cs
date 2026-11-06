using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using SwitchDesk.Core;

namespace SwitchDesk.Tests;

internal static partial class Program
{
    private static async Task ContentReviewTestsAsync()
    {
        await Test("Homebrew catalogs require complete HTTP status, range and length before exposing entries", async () =>
        {
            var json = Encoding.UTF8.GetBytes("[{\"name\":\"Tool\",\"dlurl\":\"https://fixture.invalid/tool.nro\"}]");
            foreach (var mode in new[] { "partial", "range", "truncated", "oversized", "valid", "chunked" })
            {
                using var http = new HttpClient(new FixtureHttp(request =>
                {
                    var response = new HttpResponseMessage(mode == "partial" ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
                    {
                        RequestMessage = request,
                        Content = mode == "chunked" ? new StreamContent(new ResourceFragmentedBody(json)) : new ByteArrayContent(json)
                    };
                    if (mode is "partial" or "range") response.Content.Headers.ContentRange = new(0, json.Length - 1, json.Length * 2);
                    if (mode == "truncated") response.Content.Headers.ContentLength = json.Length + 1;
                    if (mode == "oversized") response.Content.Headers.ContentLength = HomebrewStore.MaximumIndexBytes + 1;
                    return response;
                }));
                if (mode is "valid" or "chunked") Assert((await HomebrewStore.FetchIndexAsync(http, "https://fixture.invalid/index.json", default)).Single().Name == "Tool");
                else await Throws<InvalidDataException>(() => HomebrewStore.FetchIndexAsync(http, "https://fixture.invalid/index.json", default));
            }
        });
        await Test("TitleDB retries partial or truncated catalogs instead of publishing incomplete updates", async () =>
        {
            var partial = Encoding.UTF8.GetBytes("id|rightsId|version\n0100000000000800||65536\n");
            var complete = Encoding.UTF8.GetBytes("id|rightsId|version\n0100000000000800||65536\n0100000000001001||0\n");
            foreach (var mode in new[] { "partial", "range", "truncated" })
            {
                var calls = 0;
                using var http = new HttpClient(new FixtureHttp(request =>
                {
                    Assert(request.Headers.AcceptEncoding.Any(value => value.Value == "identity"));
                    var first = ++calls == 1;
                    var response = new HttpResponseMessage(first && mode == "partial" ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
                    { RequestMessage = request, Content = new ByteArrayContent(first ? partial : complete) };
                    if (first && mode is "partial" or "range") response.Content.Headers.ContentRange = new(0, partial.Length - 1, complete.Length);
                    if (first && mode == "truncated") response.Content.Headers.ContentLength = complete.Length;
                    return response;
                }));
                var catalog = await TitleDbProvider.FetchAsync(http, default);
                Assert(calls == 2 && catalog.Components.Length == 2 && catalog.Endpoint!.Contains("api.github.com", StringComparison.Ordinal));
            }
            var failures = 0;
            using var failedHttp = new HttpClient(new FixtureHttp(request =>
            {
                failures++;
                var response = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(partial) };
                response.Content.Headers.ContentLength = complete.Length; return response;
            }));
            await Throws<IOException>(() => TitleDbProvider.FetchAsync(failedHttp, default));
            Assert(failures == 2);
        });
        await Test("owned source indexes reject partial and truncated responses before parsing or replacing a catalog", async () =>
        {
            var source = new ContentSource("http-index", "index", ContentSourceKind.Http, "https://fixture.invalid/library/");
            Expect<InvalidDataException>(() => ContentSources.Parse(Encoding.UTF8.GetBytes("{\"formatVersion\":1,\"entries\":[null]}"), source));
            var bytes = Encoding.UTF8.GetBytes("{\"formatVersion\":1,\"entries\":[]}");
            foreach (var mode in new[] { "partial", "range", "truncated", "valid" })
            {
                using var http = new HttpClient(new FixtureHttp(request =>
                {
                    Assert(request.RequestUri!.AbsoluteUri == source.Root + "index.json");
                    var response = new HttpResponseMessage(mode == "partial" ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
                    { RequestMessage = request, Content = new ByteArrayContent(bytes) };
                    if (mode is "partial" or "range") response.Content.Headers.ContentRange = new(0, bytes.Length - 1, bytes.Length * 2);
                    if (mode == "truncated") response.Content.Headers.ContentLength = bytes.Length + 1;
                    return response;
                }));
                if (mode == "valid") Assert((await ContentSources.FetchAsync(http, source, null, default)).Entries.Length == 0);
                else await Throws<InvalidDataException>(() => ContentSources.FetchAsync(http, source, null, default));
            }
        });
        await Test("cheat sections do not require blank separator lines", () => Sync(() =>
        {
            var sections = CheatManager.Parse("[First]\n01234567 ABCDEF01\n# between sections\n[Second]\n02000000 00000000\n");
            Assert(sections.Length == 2 && sections[0].Name == "First" && sections[1].Name == "Second");
            Assert(sections.All(section => section.CodeLines.Length == 1));
            Expect<InvalidDataException>(() => CheatManager.Parse("[Empty]\n[Next]\n01234567 ABCDEF01\n"));
        }));
        await Test("homebrew index digest is retained and checked before any device writes", async () =>
        {
            byte[] Payload(byte value)
            {
                var bytes = new byte[0x80]; "NRO0"u8.CopyTo(bytes.AsSpan(0x10)); bytes[^1] = value; return bytes;
            }
            var payload = Payload(1);
            byte[] Index(object? digest) => JsonSerializer.SerializeToUtf8Bytes(new[] { new { name = "Digest fixture", dlurl = "https://fixture.invalid/tool.nro", sha256 = digest } });
            var app = HomebrewStore.ParseIndex(Index(Fingerprints.Of(payload))).Single();
            Assert(app.Sha256 == Fingerprints.Of(payload));
            await using var peer = new Peer(); await using var client = await peer.ConnectAsync();
            await Throws<InvalidDataException>(() => HomebrewStore.InstallAsync(client, app, Payload(2), null, null));
            Assert(peer.WriteOperations == 0 && peer.SdFiles.Count == 0);
            var path = await HomebrewStore.InstallAsync(client, app, payload, null, null);
            Assert(peer.SdFiles[path].SequenceEqual(payload));
            foreach (var invalid in new object?[] { "bad", "", null, 123 })
                Expect<InvalidDataException>(() => HomebrewStore.ParseIndex(Index(invalid)));
            Assert(HomebrewStore.ParseIndex(Encoding.UTF8.GetBytes("[{\"name\":\"No digest\",\"dlurl\":\"https://fixture.invalid/other.nro\"}]")).Single().Sha256 == null);
        });
        await Test("homebrew exposes direct NRO files without renaming unsupported archives", async () =>
        {
            var apps = HomebrewStore.ParseIndex(Encoding.UTF8.GetBytes("[{\"name\":\"Archive\",\"dlurl\":\"https://fixture.invalid/archive.zip\"},{\"name\":\"Direct\",\"dlurl\":\"https://fixture.invalid/tool.NRO?download=1\"}]"));
            Assert(apps.Length == 1 && apps[0].Name == "Direct" && apps[0].FileName == "tool.NRO");
            Expect<InvalidDataException>(() => HomebrewStore.ParseIndex(Encoding.UTF8.GetBytes("[{\"name\":\"Archive\",\"dlurl\":\"https://fixture.invalid/archive.zip\"}]")));
            var requests = 0;
            using var http = new HttpClient(new FixtureHttp(request => { requests++; return new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) }; }));
            await Throws<InvalidDataException>(() => HomebrewStore.DownloadAsync(http, apps[0] with { DownloadUrl = "https://fixture.invalid/archive.zip" }, null, default));
            Assert(requests == 0);
            Assert((await HomebrewStore.DownloadAsync(http, apps[0], null, default)).SequenceEqual(new byte[] { 1, 2, 3 }));
        });
        await Test("homebrew refuses unsolicited partial files even when the prefix has a valid NRO magic", async () =>
        {
            var app = new HomebrewApp("Partial", "", "", "", "https://fixture.invalid/tool.nro", "tool.nro");
            var prefix = new byte[128]; "NRO0"u8.CopyTo(prefix.AsSpan(0x10));
            foreach (var status in new[] { HttpStatusCode.PartialContent, HttpStatusCode.OK })
            {
                using var http = new HttpClient(new FixtureHttp(request =>
                {
                    var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(prefix) };
                    response.Content.Headers.ContentRange = new(0, prefix.Length - 1, prefix.Length * 2);
                    return response;
                }));
                await Throws<InvalidDataException>(() => HomebrewStore.DownloadAsync(http, app, null, default));
            }
        });
        await Test("long Homebrew names keep the NRO extension on the actual upload target", async () =>
        {
            var stem = new string('a', 115) + "🚀" + new string('b', 20);
            var fileName = stem + ".NRO";
            var app = new HomebrewApp("Fixture", "", "", "", "https://fixture.invalid/tool.nro", fileName);
            var bytes = new byte[128]; "NRO0"u8.CopyTo(bytes.AsSpan(0x10));
            await using var peer = new Peer(); await using var client = await peer.ConnectAsync();
            var target = await HomebrewStore.InstallAsync(client, app, bytes, null, null);
            Assert(target == "/switch/Fixture/" + new string('a', 115) + ".NRO" && peer.SdFiles[target].SequenceEqual(bytes));
        });
        await Test("homebrew download rejects declared oversize and truncated responses", async () =>
        {
            var app = new HomebrewApp("Direct", "", "", "", "https://fixture.invalid/tool.nro", "tool.nro");
            foreach (var length in new long[] { HomebrewStore.MaximumAppBytes + 1, 4 })
            {
                using var http = new HttpClient(new FixtureHttp(request =>
                {
                    var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
                    response.Content.Headers.ContentLength = length; return response;
                }));
                await Throws<InvalidDataException>(() => HomebrewStore.DownloadAsync(http, app, null, default));
            }
        });
    }
}
