using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using SwitchDesk.App;
using SwitchDesk.Core;

namespace SwitchDesk.Tests;

internal static partial class Program
{
    private static async Task Audit2ResourceTestsAsync()
    {
        await Test("resource HTML media type rejection follows HTTP case-insensitive semantics", async () =>
        {
            var index = new ResourceEntry("HTML fixture", ResourceKind.Game, "https://fixture.invalid/game.nsp", "game.nsp");
            foreach (var media in new[] { "TEXT/HTML", "Text/Html", "APPLICATION/XHTML+XML" })
            {
                var directory = Path.Combine(root, "audit2-resource-html", media.Replace('/', '-'));
                using var downloads = new ResourceDownloads(new HttpClient(new FixtureHttp(request =>
                {
                    var response = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent("Sign in to continue"u8.ToArray()) };
                    response.Content.Headers.ContentType = new MediaTypeHeaderValue(media);
                    return response;
                })));
                await Throws<InvalidDataException>(() => downloads.DownloadAsync(index, directory, null, default));
                Assert(!Directory.EnumerateFiles(directory).Any(), "HTML was published as a downloadable game");
            }
        });

        await Test("shared resource cache path does not switch the selected source on refresh", () => Sync(() =>
        {
            var entryA = new ResourceEntry("Source A", ResourceKind.Game, "https://a.invalid/game.nsp", "game.nsp");
            var entryB = entryA with { Name = "Source B", DownloadUrl = "https://b.invalid/game.nsp" };
            var shared = Path.Combine(root, "audit2-shared-cache.nsp");
            var first = new ResourceDownload(entryA, shared, 3, Fingerprints.Of([1, 2, 3]), true, DateTimeOffset.UtcNow.AddMinutes(-1));
            var second = first with { Entry = entryB, DownloadedAt = first.DownloadedAt.AddMinutes(1) };
            var model = new ResourcesViewModel(); model.SetDownloads([first, second]);
            model.SelectedDownload = model.Downloads.Single(r => r.Entry.Id == first.Entry.Id);
            model.SetDownloads([first, second]);
            Assert(model.Downloads.Count == 2 && model.SelectedDownload!.Entry.Id == first.Entry.Id,
                "refresh selected another source just because both downloads reuse the same verified bytes");
        }));

        await Test("owned resource component contract survives persisted download receipts", () => Sync(() =>
        {
            var expected = new ContentEntry(Peer.Save.TitleId, "0100000000000800", "Update", 65536, 3, Fingerprints.Of([1, 2, 3]), "updates/fixture.nsp");
            var entry = new ResourceEntry("Owned fixture", ResourceKind.Game, "https://fixture.invalid/updates/fixture.nsp", "fixture.nsp")
            { TitleId = Peer.Save.TitleId, Sha256 = expected.Sha256, Size = expected.Size };
            var receipt = new ResourceDownload(entry, Path.Combine(root, "audit2-owned-receipt.nsp"), expected.Size, expected.Sha256, true, DateTimeOffset.UtcNow)
            { ExpectedContent = expected, ContentSourceId = "owned-source-fixture" };
            var restored = JsonSerializer.Deserialize<ResourceDownload>(JsonSerializer.SerializeToUtf8Bytes(receipt, Wire.Json), Wire.Json)!;
            Assert(restored.ExpectedContent == expected && restored.ContentSourceId == receipt.ContentSourceId);
            var package = new PackageInfo("fixture.nsp", 3,
                [new(expected.ComponentId, "Update", expected.Version + 1, "SdCard", 3)
                { ApplicationId = expected.ApplicationId, RequiredSystemVersion = 0, RequiredApplicationVersion = 0 }], expected.ApplicationId, "Fixture", true);
            Expect<InvalidDataException>(() => ContentSources.ValidateInspected(restored.ExpectedContent!, package));
            // Older direct-link receipts remain readable; no content identity is invented.
            var legacy = JsonSerializer.Deserialize<ResourceDownload>(JsonSerializer.SerializeToUtf8Bytes(new
            { entry, path = receipt.Path, size = receipt.Size, sha256 = receipt.Sha256, expectedVerified = true, downloadedAt = receipt.DownloadedAt }, Wire.Json), Wire.Json)!;
            Assert(legacy.ExpectedContent == null && legacy.ContentSourceId == null);
        }));
    }
}
