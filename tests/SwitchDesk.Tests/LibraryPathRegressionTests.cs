using System.IO;
using System.Text.Json;
using SwitchDesk.Core;

namespace SwitchDesk.Tests;

internal static partial class Program
{
    private static async Task LibraryPathRegressionTests()
    {
        await Test("library retries metadata after a temporary sharing lock without requiring a timestamp change", async () =>
        {
            var directory = Directory.CreateDirectory(Path.Combine(root, "library-locked-metadata")).FullName;
            var file = Path.Combine(directory, "locked.nsp"); await File.WriteAllBytesAsync(file, [1]);
            var manifest = new GameManifest(1, "fixture", "test", "0100000000010000", "Recovered title",
                new("0100000000010000", "Base", 0, "SdCard", 1), Fingerprints.Of(new byte[] { 1 }), 1, DateTimeOffset.UtcNow);
            var sidecar = file + ".switchdesk.json";
            await File.WriteAllTextAsync(sidecar, JsonSerializer.Serialize(manifest, Wire.Json));
            var stamp = File.GetLastWriteTimeUtc(sidecar);
            using var database = new LibraryDatabase(Path.Combine(root, "library-locked-metadata.db"));
            using (var locked = new FileStream(sidecar, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                await database.ScanAsync(directory, default);
                Assert(database.List().Single().Status == "清单暂不可读取");
            }
            Assert(File.GetLastWriteTimeUtc(sidecar) == stamp);
            await database.ScanAsync(directory, default);
            Assert(database.List().Single().Name == manifest.Name && database.List().Single().Status == "有导出清单，待校验");
        });
        await Test("oversized valid export metadata is isolated without blocking the rest of a library scan", async () =>
        {
            var directory = Directory.CreateDirectory(Path.Combine(root, "library-oversized-metadata")).FullName;
            var file = Path.Combine(directory, "oversized.nsp"); await File.WriteAllBytesAsync(file, [1]);
            var manifest = new GameManifest(1, "fixture", "test", "0100000000010000", new string('x', 16 * 1024 * 1024),
                new("0100000000010000", "Base", 0, "SdCard", 1), Fingerprints.Of(new byte[] { 1 }), 1, DateTimeOffset.UtcNow);
            await using (var output = File.Create(file + ".switchdesk.json")) await JsonSerializer.SerializeAsync(output, manifest, Wire.Json);
            var normal = Path.Combine(directory, "normal.nsp"); await File.WriteAllBytesAsync(normal, [2]);
            using var database = new LibraryDatabase(Path.Combine(root, "library-oversized-metadata.db"));
            await database.ScanAsync(directory, default);
            Assert(database.List().Count == 2);
            Assert(database.List().Single(item => item.Path == file).Status == "清单无效");
            Assert(database.List().Single(item => item.Path == normal).Status == "待识别");
        });
        await Test("incomplete export component metadata cannot abort the remaining library scan", async () =>
        {
            var directory = Directory.CreateDirectory(Path.Combine(root, "library-invalid-component")).FullName;
            var manifest = new GameManifest(1, "fixture", "test", "0100000000010000", "fixture",
                new("0100000000010000", "Base", 0, "SdCard", 1), Fingerprints.Of(new byte[] { 1 }), 1, DateTimeOffset.UtcNow);
            var invalid = new[] { manifest with { Component = manifest.Component with { Kind = null! } },
                manifest with { Component = manifest.Component with { TitleId = null! } },
                manifest with { TitleId = "invalid" }, manifest with { Sha256 = new string('Z', 64) } };
            for (var i = 0; i < invalid.Length; ++i)
            {
                var path = Path.Combine(directory, i + ".nsp"); await File.WriteAllBytesAsync(path, [1]);
                await File.WriteAllTextAsync(path + ".switchdesk.json", JsonSerializer.Serialize(invalid[i], Wire.Json));
            }
            var good = Path.Combine(directory, "valid.nsp"); await File.WriteAllBytesAsync(good, [1]);
            await File.WriteAllTextAsync(good + ".switchdesk.json", JsonSerializer.Serialize(manifest, Wire.Json));
            using var db = new LibraryDatabase(Path.Combine(root, "library-invalid-component.db"));
            await db.ScanAsync(directory, default);
            Assert(db.List().Count == 5 && db.List().Count(item => item.Status == "清单无效" && item.TitleId == null) == 4);
            Assert(db.List().Single(item => item.Path == good).Status == "有导出清单，待校验");
        });
        await Test("failed database initialization releases its file and disposed libraries reject reuse", () => Sync(() =>
        {
            var path = Path.Combine(root, "broken-library.db"); File.WriteAllBytes(path, new byte[512]);
            Expect<IOException>(() => { using var invalid = new LibraryDatabase(path); });
            using (var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) exclusive.SetLength(0);
            using var database = new LibraryDatabase(path);
            database.Dispose(); database.Dispose();
            Expect<ObjectDisposedException>(() => database.List());
            Expect<ObjectDisposedException>(() => database.Remove(Path.Combine(root, "any.nsp")));
        }));
        await Test("library scan preserves platform path identity across repeated scans", async () =>
        {
            var directory = Path.Combine(root, "library-case-review"); Directory.CreateDirectory(directory);
            var source = Path.Combine(directory, "Game.nsp"); await File.WriteAllBytesAsync(source, [1, 2, 3]);
            using var database = new LibraryDatabase(Path.Combine(root, "library-case-review.db"));
            await database.ScanAsync(directory, default);
            if (OperatingSystem.IsWindows())
            {
                await database.ScanAsync(directory.ToUpperInvariant(), default);
                await database.ScanAsync(directory.ToLowerInvariant(), default);
                Assert(database.List().Count == 1 && database.List().Single().Path == source);
                // Previously created aliases remain readable; rescanning cannot
                // throw from duplicate dictionary keys or add a third alias.
                var original = database.List().Single();
                database.Upsert(original with { Path = original.Path.ToUpperInvariant() });
                Assert(database.List().Count == 2);
                await database.ScanAsync(directory, default);
                await database.ScanAsync(directory.ToLowerInvariant(), default);
                Assert(database.List().Count == 2);
            }
            else
            {
                var second = Path.Combine(directory, "game.nsp");
                if (!File.Exists(second))
                {
                    await File.WriteAllBytesAsync(second, [4, 5, 6]);
                    await database.ScanAsync(directory, default);
                    await database.ScanAsync(directory, default);
                    Assert(database.List().Count == 2);
                }
                else await database.ScanAsync(directory, default);
            }
        });
    }
}
