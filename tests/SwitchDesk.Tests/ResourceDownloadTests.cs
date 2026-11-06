using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SwitchDesk.Core;

namespace SwitchDesk.Tests;

internal static partial class Program
{
    private static async Task ResourceDownloadTestsAsync()
    {
        await Test("resource indexes and direct downloads reject range bodies mislabeled as HTTP 200", async () =>
        {
            var json = Encoding.UTF8.GetBytes("{\"formatVersion\":1,\"entries\":[{\"kind\":\"Homebrew\",\"name\":\"Tool\",\"url\":\"https://fixture.invalid/tool.nro\"}]}");
            using var http = new HttpClient(new FixtureHttp(request =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(json) };
                response.Content.Headers.ContentRange = new(0, json.Length - 1, json.Length * 2); return response;
            }));
            await Throws<InvalidDataException>(() => ResourceIndex.FetchAsync(http, "https://fixture.invalid/index.json", default));
            using var downloads = new ResourceDownloads(new HttpClient(new FixtureHttp(request =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent([1, 2, 3]) };
                response.Content.Headers.ContentRange = new(0, 2, 100); return response;
            })));
            var directory = Path.Combine(root, "resources-http-200-range");
            await Throws<InvalidDataException>(() => downloads.DownloadAsync(new("Fixture", ResourceKind.Game, "https://fixture.invalid/game.nsp", "game.nsp"), directory, null, default));
            Assert(!Directory.EnumerateFiles(directory).Any(), "partial response was published as a complete download without an expected digest");
        });
        await Test("resource index explicitly identifies games, Homebrew and cheats without guessing identities", () => Sync(() =>
        {
            var json = """
                {"formatVersion":1,"entries":[
                  {"kind":"Game","name":"Game fixture","url":"https://fixture.invalid/game.nsp","size":3},
                  {"kind":"Homebrew","name":"Tool fixture","url":"https://fixture.invalid/tool.NRO?download=1","sourceName":"Fixture index"},
                  {"kind":"Cheat","name":"Cheat fixture","url":"https://fixture.invalid/codes.txt","titleId":"01000000000000ab","buildId":"00112233445566aa"}
                ]}
                """;
            var entries = ResourceIndex.Parse(Encoding.UTF8.GetBytes(json));
            Assert(entries.Length == 3 && entries[0].Kind == ResourceKind.Game && entries[0].TitleId == null && entries[0].Sha256 == null);
            Assert(entries[1].FileName == "tool.NRO" && entries[1].Kind == ResourceKind.Homebrew && entries[1].SourceName == "Fixture index");
            Assert(entries[2].TitleId == "01000000000000AB" && entries[2].BuildId == "00112233445566AA");
            foreach (var invalid in new[]
            {
                "{\"formatVersion\":\"1\",\"entries\":[]}",
                "{\"formatVersion\":1,\"entries\":[{\"name\":\"missing kind\",\"url\":\"https://fixture.invalid/game.nsp\"}]}",
                "{\"formatVersion\":1,\"entries\":[{\"kind\":\"Game\",\"name\":\"bad size\",\"size\":\"3\",\"url\":\"https://fixture.invalid/game.nsp\"}]}",
                "{\"formatVersion\":1,\"formatVersion\":1,\"entries\":[]}"
            }) Expect<InvalidDataException>(() => ResourceIndex.Parse(Encoding.UTF8.GetBytes(invalid)));
            var repeated = JsonSerializer.SerializeToUtf8Bytes(new { formatVersion = 1, entries = new[] {
                new { kind = "Game", name = "same", url = "https://fixture.invalid/game.nsp" },
                new { kind = "Game", name = "same", url = "https://fixture.invalid/game.nsp" } } });
            Expect<InvalidDataException>(() => ResourceIndex.Parse(repeated));
        }));

        await Test("resource validation rejects disguised archives, script URLs, credentials and unsafe filenames", () => Sync(() =>
        {
            var entry = new ResourceEntry("Fixture", ResourceKind.Game, "https://fixture.invalid/game.nsp", "game.nsp");
            foreach (var invalid in new[]
            {
                entry with { Kind = (ResourceKind)100 }, entry with { DownloadUrl = "javascript:alert(1)" },
                entry with { DownloadUrl = "https://user:secret@fixture.invalid/game.nsp" },
                entry with { DownloadUrl = "https://fixture.invalid/game.zip" }, entry with { DownloadUrl = "https://fixture.invalid/file.js" },
                entry with { FileName = "../game.nsp" }, entry with { FileName = "NUL.nsp" }, entry with { FileName = "name.nsp." },
                entry with { FileName = "game.nro" }, entry with { DownloadUrl = "https://fixture.invalid/escaped%2fgame.nsp", FileName = "" },
                entry with { Sha256 = "bad" }, entry with { Size = -1 }, entry with { Size = ResourceIndex.MaximumBytes(ResourceKind.Game) + 1 },
                entry with { TitleId = "not-title-id" }, entry with { BuildId = "not-build-id" }, entry with { Name = "line\nbreak" }
            }) Expect<InvalidDataException>(() => ResourceIndex.Validate(invalid));
        }));

        await Test("resource download verifies source digest and never overwrites an existing file", async () =>
        {
            byte[] content = [1, 2, 3, 4]; var calls = 0;
            var entry = new ResourceEntry("Verified game fixture", ResourceKind.Game, "https://fixture.invalid/game.nsp", "game.nsp")
                { Size = content.Length, Sha256 = Fingerprints.Of(content).ToLowerInvariant() };
            using var downloads = new ResourceDownloads(new HttpClient(new FixtureHttp(request => { calls++; return new(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(content) }; })));
            var directory = Path.Combine(root, "resources-verified");
            var result = await downloads.DownloadAsync(entry, directory, null, default);
            Assert(result.ExpectedVerified && result.Size == content.Length && result.Sha256 == Fingerprints.Of(content));
            Assert(result.Entry.Sha256 == result.Sha256 && (await File.ReadAllBytesAsync(result.Path)).SequenceEqual(content));
            await ResourceDownloads.VerifyAsync(result, default);
            await Throws<IOException>(() => downloads.DownloadAsync(entry, directory, null, default));
            Assert(calls == 1 && Directory.GetFiles(directory).Length == 1);
        });

        await Test("resource calculated checksum remains distinct from source verification and works offline", async () =>
        {
            byte[] content = [8, 7, 6];
            var entry = new ResourceEntry("Tool fixture", ResourceKind.Homebrew, "https://fixture.invalid/tool.nro", "tool.nro");
            using var downloads = new ResourceDownloads(new HttpClient(new FixtureHttp(request => new(HttpStatusCode.OK) { RequestMessage = request, Content = new StreamContent(new ResourceFragmentedBody(content)) })));
            var result = await downloads.DownloadAsync(entry, Path.Combine(root, "resources-calculated"), null, default);
            Assert(!result.ExpectedVerified && result.Sha256 == Fingerprints.Of(content) && result.Size == 3);
            await ResourceDownloads.VerifyAsync(result with { Entry = result.Entry with { DownloadUrl = new Uri(result.Path).AbsoluteUri } }, default);
            await File.WriteAllBytesAsync(result.Path, [6, 7, 8]);
            await Throws<InvalidDataException>(() => ResourceDownloads.VerifyAsync(result, default));
        });

        await Test("resource download refuses HTML, scripts and redirects before publishing files", async () =>
        {
            var directory = Path.Combine(root, "resources-not-files");
            var entry = new ResourceEntry("Fixture", ResourceKind.Game, "https://fixture.invalid/game.nsp", "game.nsp");
            var responseFactories = new Func<HttpRequestMessage, HttpResponseMessage>[]
            {
                request => new(HttpStatusCode.Found) { RequestMessage = request, Headers = { Location = new("https://other.invalid/game.nsp") } },
                request => new(HttpStatusCode.OK) { RequestMessage = new(HttpMethod.Get, "https://other.invalid/game.nsp"), Content = new ByteArrayContent([1]) },
                request => new(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent("login page", Encoding.UTF8, "text/html") },
                request => new(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent("alert(1)", Encoding.UTF8, "application/javascript") },
                request => new(HttpStatusCode.OK) { RequestMessage = request, Content = new StreamContent(new ResourceFragmentedBody("\uFEFF  <!DOCTYPE html><html>login</html>"u8.ToArray())) }
            };
            for (var i = 0; i < responseFactories.Length; ++i)
            {
                using var downloads = new ResourceDownloads(new HttpClient(new FixtureHttp(responseFactories[i])));
                if (i < 2) await Throws<IOException>(() => downloads.DownloadAsync(entry, directory, null, default));
                else await Throws<InvalidDataException>(() => downloads.DownloadAsync(entry, directory, null, default));
                Assert(!Directory.GetFiles(directory).Any());
            }
        });

        await Test("resource download rejects short, oversized and checksum-mismatched bodies without leftover partials", async () =>
        {
            var directory = Path.Combine(root, "resources-invalid-body");
            var entry = new ResourceEntry("Cheat fixture", ResourceKind.Cheat, "https://fixture.invalid/codes.txt", "codes.txt");
            foreach (var scenario in Enumerable.Range(0, 5))
            {
                using var downloads = new ResourceDownloads(new HttpClient(new FixtureHttp(request =>
                {
                    HttpContent content = scenario == 4 ? new StreamContent(new ResourceFragmentedBody(new byte[1024 * 1024 + 1], 65536)) : new ByteArrayContent([1, 2, 3]);
                    if (scenario == 0) content.Headers.ContentLength = 4;
                    if (scenario == 1) content.Headers.ContentLength = ResourceIndex.MaximumBytes(ResourceKind.Cheat) + 1;
                    return new(HttpStatusCode.OK) { RequestMessage = request, Content = content };
                })));
                var selected = scenario switch { 2 => entry with { Size = 2 }, 3 => entry with { Sha256 = Fingerprints.Of([9]) }, _ => entry };
                await Throws<InvalidDataException>(() => downloads.DownloadAsync(selected, directory, null, default));
                Assert(!Directory.GetFiles(directory).Any());
            }
        });

        await Test("resource cancellation removes only its own partial and never publishes an incomplete resource", async () =>
        {
            var directory = Path.Combine(root, "resources-cancel"); Directory.CreateDirectory(directory);
            var unrelated = Path.Combine(directory, "other-task.partial"); await File.WriteAllTextAsync(unrelated, "keep");
            var entry = new ResourceEntry("Cancelled fixture", ResourceKind.Game, "https://fixture.invalid/game.nsp", "game.nsp");
            using var downloads = new ResourceDownloads(new HttpClient(new FixtureHttp(request => new(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(new byte[Wire.ChunkSize + 1]) })));
            using var cancellation = new CancellationTokenSource();
            await Throws<OperationCanceledException>(() => downloads.DownloadAsync(entry, directory, new ResourceCancelProgress(cancellation), cancellation.Token));
            Assert(Directory.GetFiles(directory).SequenceEqual(new[] { unrelated }) && await File.ReadAllTextAsync(unrelated) == "keep");
            using var precancelled = new CancellationTokenSource(); precancelled.Cancel();
            var untouched = Path.Combine(root, "resources-pre-cancelled");
            await Throws<OperationCanceledException>(() => downloads.DownloadAsync(entry, untouched, null, precancelled.Token));
            Assert(!Directory.Exists(untouched));
        });

        await Test("resource stalled body has an inactivity timeout and cleans its staging file", async () =>
        {
            var directory = Path.Combine(root, "resources-stall");
            using var downloads = new ResourceDownloads(new HttpClient(new FixtureHttp(request => new(HttpStatusCode.OK) { RequestMessage = request, Content = new StreamContent(new ResourceStalledBody()) })), TimeSpan.FromMilliseconds(30));
            await Throws<IOException>(() => downloads.DownloadAsync(new("Stalled fixture", ResourceKind.Cheat, "https://fixture.invalid/codes.txt", "codes.txt"), directory, null, default));
            Assert(!Directory.GetFiles(directory).Any());
        });

        await Test("resource index HTTP fetch is bounded and does not follow an unreviewed redirect", async () =>
        {
            var json = Encoding.UTF8.GetBytes("{\"formatVersion\":1,\"entries\":[{\"kind\":\"Homebrew\",\"name\":\"Tool\",\"url\":\"https://fixture.invalid/tool.nro\"}]}");
            using var http = new HttpClient(new FixtureHttp(request => new(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(json) }));
            Assert((await ResourceIndex.FetchAsync(http, "https://fixture.invalid/index.json", default)).Single().Kind == ResourceKind.Homebrew);
            using var oversized = new HttpClient(new FixtureHttp(request =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent([1]) };
                response.Content.Headers.ContentLength = ResourceIndex.MaximumIndexBytes + 1; return response;
            }));
            await Throws<InvalidDataException>(() => ResourceIndex.FetchAsync(oversized, "https://fixture.invalid/index.json", default));
            using var redirect = new HttpClient(new FixtureHttp(request => new(HttpStatusCode.Found) { RequestMessage = request }));
            await Throws<IOException>(() => ResourceIndex.FetchAsync(redirect, "https://fixture.invalid/index.json", default));
        });
    }

    private sealed class ResourceFragmentedBody(byte[] bytes, int maximumChunk = 1) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => base.ReadAsync(buffer[..Math.Min(maximumChunk, buffer.Length)], ct);
    }
    private sealed class ResourceCancelProgress(CancellationTokenSource cancellation) : IProgress<TransferProgress>
    { public void Report(TransferProgress progress) { if (progress.Completed > 0) cancellation.Cancel(); } }
    private sealed class ResourceStalledBody : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { await Task.Delay(Timeout.Infinite, ct); return 0; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException(); public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
