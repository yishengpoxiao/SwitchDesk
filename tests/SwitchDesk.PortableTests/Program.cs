using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using SwitchDesk.Core;
using SwitchDesk.Tests;

var root = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Path.GetTempPath(), "SwitchDesk-portable-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int passed = 0, failed = 0, skipped = 0;
// Keep this first: an earlier LibraryDatabase construction used to hide a
// missing native SQLite resolver on macOS and Linux.
await Test("SQLite can initialize through Phase2State before LibraryDatabase", async () =>
{
    using (var state = new Phase2State(Path.Combine(root, "first-state.db")))
    {
        state.Put("portable", "sample", new { text = "中文路径", bytes = 5L << 30 });
        var value = state.Get<JsonElement>("portable", "sample")!.Value.Value;
        Check(value.GetProperty("text").GetString() == "中文路径" && value.GetProperty("bytes").GetInt64() == 5L << 30);
    }
    using var library = new LibraryDatabase(Path.Combine(root, "after-state.db"));
    var folder = Directory.CreateDirectory(Path.Combine(root, "游戏库")).FullName;
    await File.WriteAllBytesAsync(Path.Combine(folder, "fixture.nsp"), [1, 2, 3]);
    await library.ScanAsync(folder, default);
    Check(library.List().Single().Size == 3);
});
await Test("library verifies and retains the expected digest after a mismatch", async () =>
{
    using var library = new LibraryDatabase(Path.Combine(root, "verify.db"));
    var folder = Directory.CreateDirectory(Path.Combine(root, "verify-library")).FullName;
    var source = Path.Combine(folder, "fixture.nsp");
    await File.WriteAllBytesAsync(source, [1, 2, 3]);
    await library.ScanAsync(folder, default);
    var verified = await library.VerifyAsync(library.List().Single(), default);
    await File.WriteAllBytesAsync(source, [9, 8, 7]);
    var bad = await library.VerifyAsync(verified, default);
    Check(verified.Status == "已校验" && bad.Status == "校验不匹配" && bad.Sha256 == verified.Sha256);
});
await Test("snapshot export validates content and never replaces an earlier export", async () =>
{
    var path = Path.Combine(root, "snapshot.zip");
    await CreateSnapshot(path, [1, 2, 3], DateTimeOffset.UtcNow);
    var snapshot = await SnapshotStore.VerifyAsync(path);
    var target = Path.Combine(root, "exports");
    var first = await SnapshotTools.ExportSnapshotAsync(path, target, expectedManifest: snapshot.Manifest);
    var second = await SnapshotTools.ExportSnapshotAsync(path, target, expectedManifest: snapshot.Manifest);
    var exported = await File.ReadAllBytesAsync(first);
    var original = await File.ReadAllBytesAsync(path);
    Check(first != second && exported.SequenceEqual(original));
    await SnapshotStore.VerifyAsync(second, expectedManifest: snapshot.Manifest);
    Check(!Directory.EnumerateFiles(target, "*.partial").Any());
});
await Test("portable filename budgets preserve complete Unicode scalars", () =>
{
    Check(PathSafety.SafeName(new string('中', 33) + "文", 100, 100) == new string('中', 33));
    Check(PathSafety.SafeName(new string('a', 96) + "🚀tail", 100, 100) == new string('a', 96) + "🚀");
    Check(PathSafety.SafeName(new string('a', 97) + "🚀tail", 100, 100) == new string('a', 97));
    Check(PathSafety.SafeName("普通名称", 100, 100) == "普通名称");
    Check(PathSafety.SafeName("🚀", 100, 3) == "_");
    return Task.CompletedTask;
});
await Test("long Chinese snapshot names export within Windows and Unix filename budgets", async () =>
{
    var path = Path.Combine(root, "unicode-snapshot.zip");
    var gameName = new string('游', 119) + "🚀title";
    var userName = new string('名', 119) + "🚀user";
    await CreateSnapshot(path, [1, 2, 3], DateTimeOffset.UtcNow, gameName, userName);
    var target = Path.Combine(root, "unicode-exports");
    var first = await SnapshotTools.ExportSnapshotAsync(path, target);
    var second = await SnapshotTools.ExportSnapshotAsync(path, target);
    Check(first != second);
    foreach (var export in new[] { first, second })
    {
        var leaf = Path.GetFileName(export);
        Check(leaf.Length <= 255 && System.Text.Encoding.UTF8.GetByteCount(leaf) <= 255);
        var verified = await SnapshotStore.VerifyAsync(export);
        Check(verified.Manifest.GameName == gameName && verified.Manifest.UserName == userName);
    }
    Check(!Directory.EnumerateFiles(target, "*.partial").Any());
});
await Test("tampered snapshot cannot publish an export", async () =>
{
    var path = Path.Combine(root, "tampered.zip");
    await CreateSnapshot(path, [1, 2, 3], DateTimeOffset.UtcNow);
    using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
    {
        archive.GetEntry("data/main.dat")!.Delete();
        using var payload = archive.CreateEntry("data/main.dat").Open();
        payload.Write([9, 8, 7]);
    }
    var output = Path.Combine(root, "tampered-exports");
    await Throws<InvalidDataException>(() => SnapshotTools.ExportSnapshotAsync(path, output));
    Check(!Directory.Exists(output) || !Directory.EnumerateFileSystemEntries(output).Any());
});
await Test("retention excludes differently cased manual archives on a case-sensitive volume", async () =>
{
    var scope = Directory.CreateDirectory(Path.Combine(root, "case-retention")).FullName;
    var managed = Directory.CreateDirectory(Path.Combine(scope, "Snapshots")).FullName;
    var probe = Path.Combine(managed, "case-probe");
    await File.WriteAllTextAsync(probe, "fixture");
    if (File.Exists(Path.Combine(scope, "snapshots", "case-probe"))) throw new Skip("volume is case-insensitive");
    var archived = Path.Combine(scope, "snapshots", "archived.zip");
    var current = Path.Combine(managed, "current.zip");
    await CreateSnapshot(archived, [1], DateTimeOffset.UtcNow.AddDays(-1));
    await CreateSnapshot(current, [2], DateTimeOffset.UtcNow);
    new SnapshotStore(scope).Prune(1);
    Check(File.Exists(archived) && File.Exists(current));
});
await Test("FTP upload uses conflict protection when SIZE is unavailable", async () =>
{
    using var server = new FtpMockServer { NoSize = true };
    await using var ftp = new FtpClient();
    await ftp.ConnectAsync(FtpOptions.Anonymous(server.Endpoint.Address.ToString(), server.Endpoint.Port));
    var source = Path.Combine(root, "upload.bin"); await File.WriteAllBytesAsync(source, [6, 7, 8]);
    var original = server.Files["/readme.txt"].ToArray();
    await Throws<IOException>(() => ftp.UploadAsync(source, "/readme.txt", null));
    Check(server.Uploads == 0 && server.Files["/readme.txt"].SequenceEqual(original));
    await ftp.UploadAsync(source, "/switch/new.bin", null);
    Check(server.Files["/switch/new.bin"].SequenceEqual(new byte[] { 6, 7, 8 }));
    await ftp.DisconnectAsync();
});
await Test("FTP truncated download does not publish a partial file", async () =>
{
    using var server = new FtpMockServer { TruncateSuccessfulRetr = true };
    await using var ftp = new FtpClient();
    await ftp.ConnectAsync(FtpOptions.Anonymous(server.Endpoint.Address.ToString(), server.Endpoint.Port));
    var target = Path.Combine(root, "incomplete.bin");
    await Throws<IOException>(() => ftp.DownloadAsync("/readme.txt", target, null));
    Check(!File.Exists(target) && !Directory.EnumerateFiles(root, "incomplete.bin.*.partial").Any());
});
await Test("Unix backslash filenames cannot turn into remote subdirectories", async () =>
{
    if (Path.DirectorySeparatorChar != '/') throw new Skip("requires Unix filename rules");
    var source = Directory.CreateDirectory(Path.Combine(root, "backslash-upload")).FullName;
    await File.WriteAllBytesAsync(Path.Combine(source, "a\\b"), [1, 2, 3]);
    using var server = new FtpMockServer();
    await using var ftp = new FtpClient();
    await ftp.ConnectAsync(FtpOptions.Anonymous(server.Endpoint.Address.ToString(), server.Endpoint.Port));
    await Throws<InvalidDataException>(() => ftp.UploadTreeAsync(source, "/switch/tree", null));
    Check(server.Uploads == 0 && !server.Files.ContainsKey("/switch/tree/a/b"));
});
Console.WriteLine($"PORTABLE RESULT {passed} passed, {failed} failed, {skipped} skipped; fixtures: {root}");
return failed == 0 ? 0 : 1;

async Task Test(string name, Func<Task> action)
{
    try { await action(); passed++; Console.WriteLine("PASS " + name); }
    catch (Skip reason) { skipped++; Console.WriteLine("SKIP " + name + ": " + reason.Message); }
    catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error); }
}
static void Check(bool condition) { if (!condition) throw new Exception("assertion failed"); }
static async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
static async Task CreateSnapshot(string path, byte[] data, DateTimeOffset time, string gameName = "Portable fixture", string userName = "一生破晓")
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    var files = new SnapshotFile[] { new("main.dat", data.Length, Convert.ToHexString(SHA256.HashData(data))) };
    string[] directories = [];
    var digest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { files, directories }, Wire.Json)));
    var manifest = new SaveManifest(1, "portable-fixture", "emuMMC", "0100000000010000", gameName, "fixture-user", userName, "Account", time, files, digest, directories);
    using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
    await using (var payload = archive.CreateEntry("data/main.dat").Open()) await payload.WriteAsync(data);
    await using var metadata = archive.CreateEntry("manifest.json").Open();
    await JsonSerializer.SerializeAsync(metadata, manifest, Wire.Json);
}
sealed class Skip(string message) : Exception(message);
