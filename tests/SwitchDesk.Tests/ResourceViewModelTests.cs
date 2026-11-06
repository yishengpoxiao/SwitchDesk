using System.IO;
using SwitchDesk.App;
using SwitchDesk.Core;

namespace SwitchDesk.Tests;

internal static partial class Program
{
    private static async Task ResourceViewModelTestsAsync()
    {
        await Test("resource public articles cannot masquerade as downloadable files", () => Sync(() =>
        {
            var article = new PublicResourceArticle("Article fixture", "https://fixture.invalid/article", "Fixture site", ResourceKind.Game);
            var row = new ResourceResultRow(article);
            Assert(!row.CanDownload && row.Entry == null && row.Article == article && row.PageUrl == article.PageUrl);
            var entry = new ResourceEntry("File fixture", ResourceKind.Game, "https://fixture.invalid/game.nsp", "game.nsp")
            { SourcePage = article.PageUrl, SourceName = "Explicit fixture index" };
            var downloadable = new ResourceResultRow(entry);
            Assert(downloadable.CanDownload && downloadable.Article == null && downloadable.Entry == entry && downloadable.PageUrl == article.PageUrl);
        }));
        await Test("resource type filtering cannot keep a hidden result selected", () => Sync(() =>
        {
            var model = new ResourcesViewModel();
            var game = new ResourceResultRow(new ResourceEntry("Game fixture", ResourceKind.Game, "https://fixture.invalid/game.nsp", "game.nsp"));
            var cheat = new ResourceResultRow(new ResourceEntry("Cheat fixture", ResourceKind.Cheat, "https://fixture.invalid/0123456789abcdef.cheat", "0123456789abcdef.cheat"));
            model.SetResults([game, cheat]); model.SelectedResult = game;
            model.SelectedType = "金手指";
            Assert(model.Results.Count == 1 && model.SelectedResult == cheat && model.SelectedKind == ResourceKind.Cheat);
            model.SelectedType = "Homebrew / 插件";
            Assert(model.Empty && model.SelectedResult == null && model.SelectedKind == ResourceKind.Homebrew);
            model.SelectedType = "全部类型";
            Assert(model.Results.Count == 2 && model.SelectedResult != null && model.SelectedKind == null);
            model.SelectedSource = model.Sources.Single(s => s.Id == "imported");
            Assert(model.Empty && model.SelectedResult == null && model.Status.Contains("来源已切换", StringComparison.Ordinal));
            model.SetResults([]);
            Assert(model.Empty && model.SelectedResult == null);
            model.SetResults([], sourceAvailable: false);
            Assert(model.EmptyHeading.Contains("无法", StringComparison.Ordinal) && !model.EmptyHeading.Contains("没有找到", StringComparison.Ordinal));
        }));
        await Test("resource download refresh preserves selection and identifies missing files", async () =>
        {
            var directory = Path.Combine(root, "resource-view-model"); Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "fixture.nro"); await File.WriteAllBytesAsync(path, [1, 2, 3]);
            var entry = new ResourceEntry("Homebrew fixture", ResourceKind.Homebrew, "https://fixture.invalid/fixture.nro", "fixture.nro");
            var present = new ResourceDownload(entry, path, 3, Fingerprints.Of([1, 2, 3]), true, DateTimeOffset.UtcNow);
            var missing = present with { Path = Path.Combine(directory, "missing.nro"), ExpectedVerified = false, DownloadedAt = present.DownloadedAt.AddMinutes(1) };
            var model = new ResourcesViewModel(); model.SetDownloads([present, missing]);
            Assert(model.Downloads.Count == 2 && !model.DownloadsEmpty && model.Downloads[0].Value == missing);
            Assert(!model.Downloads[0].Exists && model.Downloads[0].Status.Contains("移动或删除", StringComparison.Ordinal));
            model.SelectedDownload = model.Downloads.Single(r => r.Path == path);
            model.SetDownloads([present, missing]);
            Assert(model.SelectedDownload is { Exists: true, Path: var selectedPath } && selectedPath == path);
            model.SetDownloads([]);
            Assert(model.DownloadsEmpty && model.SelectedDownload == null);
        });
    }
}
