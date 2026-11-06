using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SwitchDesk.Core;

namespace SwitchDesk.Tests;
internal static partial class Program
{
    private static async Task ExtensionTests()
    {
        await Test("ftp connects and lists via MLSD", async () =>
        {
            using var server = new FtpMockServer(); await using var ftp = new FtpClient();
            await ftp.ConnectAsync(FtpOptions.Anonymous(server.Endpoint.Address.ToString(), server.Endpoint.Port));
            Assert(ftp.IsConnected);
            var entries = await ftp.ListAsync("/");
            Assert(entries.Count == 2);
            var file = entries.Single(x => x.Name == "readme.txt");
            Assert(!file.IsDirectory && file.Size == 5 && file.Path == "/readme.txt");
            Assert(entries.Single(x => x.Name == "switch").IsDirectory);
            await ftp.DisconnectAsync(); Assert(!ftp.IsConnected);
        });
        await Test("ftp list falls back to unix and dos listings", async () =>
        {
            using var server = new FtpMockServer { NoMlsd = true }; await using var ftp = new FtpClient();
            await ftp.ConnectAsync(FtpOptions.Anonymous(server.Endpoint.Address.ToString(), server.Endpoint.Port));
            var unix = await ftp.ListAsync("/switch");
            Assert(unix.Single(x => x.Name == "data.bin").Size == 70000);
            await ftp.DisconnectAsync();
            using var dos = new FtpMockServer { NoMlsd = true, UseDosList = true }; await using var ftpDos = new FtpClient();
            await ftpDos.ConnectAsync(FtpOptions.Anonymous(dos.Endpoint.Address.ToString(), dos.Endpoint.Port));
            var parsed = await ftpDos.ListAsync("/");
            Assert(parsed.Single(x => x.Name == "readme.txt").Size == 5);
            Assert(parsed.Single(x => x.Name == "switch").IsDirectory);
        });
        await Test("ftp lists an empty directory without failing", async () =>
        {
            using var server = new FtpMockServer { NoMlsd = true }; server.Directories.Add("/switch/empty");
            await using var ftp = new FtpClient();
            await ftp.ConnectAsync(FtpOptions.Anonymous(server.Endpoint.Address.ToString(), server.Endpoint.Port));
            Assert((await ftp.ListAsync("/switch/empty")).Count == 0);
            Assert((await ftp.ListAsync("/")).Count == 2);
        });
        await Test("ftp rejects newline injection in every command path", async () =>
        {
            using var server = new FtpMockServer(); await using var ftp = new FtpClient();
            await ftp.ConnectAsync(FtpOptions.Anonymous(server.Endpoint.Address.ToString(), server.Endpoint.Port));
            var source = Path.Combine(root, "ftp-injection.bin"); await File.WriteAllBytesAsync(source, [1, 2]);
            await Throws<InvalidDataException>(async () => await ftp.UploadAsync(source, "/switch/a\r\nDELE /switch/data.bin", null));
            await Throws<InvalidDataException>(async () => await ftp.MkdirAsync("/switch/a\nb"));
            await Throws<InvalidDataException>(async () => await ftp.DeleteAsync("/switch/x\ry"));
            Assert(server.Files.ContainsKey("/switch/data.bin"));
        });
        await Test("ftp download is byte exact, reports progress, publishes no partial file", async () =>
        {
            using var server = new FtpMockServer(); await using var ftp = new FtpClient();
            await ftp.ConnectAsync(FtpOptions.Anonymous(server.Endpoint.Address.ToString(), server.Endpoint.Port));
            var destination = Path.Combine(root, "ftp-download.bin");
            var progress = new List<TransferProgress>();
            await ftp.DownloadAsync("/readme.txt", destination, new CollectProgress(progress));
            Assert(File.ReadAllBytes(destination).SequenceEqual(new byte[] { 1, 2, 3, 4, 5 }));
            Assert(progress.Last().Completed == 5 && progress.Last().Total == 5);
            Assert(!Directory.GetFiles(root, "*.partial").Any());
        });
        await Test("ftp upload round trip and protected path guards", async () =>
        {
            using var server = new FtpMockServer(); await using var ftp = new FtpClient();
            await ftp.ConnectAsync(FtpOptions.Anonymous(server.Endpoint.Address.ToString(), server.Endpoint.Port));
            var source = Path.Combine(root, "ftp-upload.bin"); var payload = RandomNumberGenerator.GetBytes(3000);
            await File.WriteAllBytesAsync(source, payload);
            await ftp.UploadAsync(source, "/switch/upload.bin", null);
            Assert(server.Files["/switch/upload.bin"].SequenceEqual(payload));
            Assert(!server.Files.Keys.Any(key => key.Contains(".sdk-")), "atomic upload must not leave temporary files");
            await Throws<IOException>(async () => await ftp.UploadAsync(source, "/switch/upload.bin", null));
            await Throws<DeviceException>(async () => await ftp.UploadAsync(source, "/Nintendo/steal.bin", null));
            await Throws<DeviceException>(async () => await ftp.MkdirAsync("/atmosphere/evil"));
            await ftp.MkdirAsync("/switch/incoming");
            await ftp.RenameAsync("/switch/upload.bin", "/switch/incoming/renamed.bin");
            Assert(server.Files.ContainsKey("/switch/incoming/renamed.bin"));
            await ftp.DeleteAsync("/switch/incoming/renamed.bin");
            await ftp.RemoveDirectoryAsync("/switch/incoming");
            Assert(!server.Files.ContainsKey("/switch/incoming/renamed.bin"));
        });
        await Test("ftp recursive folder upload keeps nested and empty directories", async () =>
        {
            using var server = new FtpMockServer(); await using var ftp = new FtpClient();
            await ftp.ConnectAsync(FtpOptions.Anonymous(server.Endpoint.Address.ToString(), server.Endpoint.Port));
            var local = Path.Combine(root, "ftp-tree");
            Directory.CreateDirectory(Path.Combine(local, "nested")); Directory.CreateDirectory(Path.Combine(local, "empty"));
            var bytes = RandomNumberGenerator.GetBytes(4096);
            await File.WriteAllBytesAsync(Path.Combine(local, "nested", "data.bin"), bytes);
            await ftp.UploadTreeAsync(local, "/switch/tree", null);
            Assert(server.Files["/switch/tree/nested/data.bin"].SequenceEqual(bytes));
            Assert(server.Directories.Contains("/switch/tree/empty"));
            Assert(!server.Files.Keys.Any(key => key.Contains(".sdk-")), "tree upload must not leave temporary files");
        });
        await Test("ftp login failure surfaces a clear error", async () =>
        {
            using var server = new FtpMockServer { FailLogin = true }; await using var ftp = new FtpClient();
            await Throws<IOException>(async () => await ftp.ConnectAsync(FtpOptions.Anonymous(server.Endpoint.Address.ToString(), server.Endpoint.Port)));
        });
        await Test("ftp transfer failure keeps the control connection usable", async () =>
        {
            using var server = new FtpMockServer { FailTransfer = true }; await using var ftp = new FtpClient();
            await ftp.ConnectAsync(FtpOptions.Anonymous(server.Endpoint.Address.ToString(), server.Endpoint.Port));
            await Throws<IOException>(async () => await ftp.DownloadAsync("/readme.txt", Path.Combine(root, "ftp-broken.bin"), null));
            var entries = await ftp.ListAsync("/");
            Assert(entries.Count == 2);
        });
        await Test("ftp cancelled download aborts cleanly and control survives", async () =>
        {
            using var server = new FtpMockServer { RetrDelayMs = 800 }; await using var ftp = new FtpClient();
            await ftp.ConnectAsync(FtpOptions.Anonymous(server.Endpoint.Address.ToString(), server.Endpoint.Port));
            using var cancel = new CancellationTokenSource(250);
            await Throws<OperationCanceledException>(async () => await ftp.DownloadAsync("/switch/data.bin", Path.Combine(root, "ftp-cancel.bin"), null, cancel.Token));
            Assert(!File.Exists(Path.Combine(root, "ftp-cancel.bin")));
            var entries = await ftp.ListAsync("/");
            Assert(entries.Count == 2);
        });
        await Test("ftp validates address, port and rejects newline injection", async () =>
        {
            await using var ftp = new FtpClient();
            await Throws<InvalidDataException>(async () => await ftp.ConnectAsync(new FtpOptions("", 21, "", "")));
            await Throws<InvalidDataException>(async () => await ftp.ConnectAsync(new FtpOptions("localhost", 0, "", "")));
            await Throws<InvalidDataException>(async () => await ftp.ConnectAsync(new FtpOptions("localhost", 70000, "", "")));
            using var server = new FtpMockServer(); await using var live = new FtpClient();
            await live.ConnectAsync(FtpOptions.Anonymous(server.Endpoint.Address.ToString(), server.Endpoint.Port));
            await Throws<InvalidDataException>(async () => await live.ListAsync("/a\nb"));
        });
        await Test("cheat path exception is strictly scoped", () => Sync(() =>
        {
            var enabled = "/atmosphere/contents/0100000000010000/cheats/0d784a915f5b4ac2.cheat";
            var disabled = enabled + ".off";
            Assert(PathSafety.IsCheatPath(enabled) && PathSafety.IsCheatPath(disabled));
            var titleDirectory = "/atmosphere/contents/0100000000010000";
            Assert(PathSafety.IsCheatDirectory(titleDirectory) && PathSafety.IsCheatDirectory(titleDirectory + "/cheats"));
            Assert(!PathSafety.IsCheatDirectory("/atmosphere/contents/010000000001000"));
            Assert(!PathSafety.IsCheatDirectory(titleDirectory + "/exefs"));
            foreach (var rejected in new[] { "/atmosphere/package3/x.cheat", "/atmosphere/contents/010000000001000/cheats/0d784a915f5b4ac2.cheat",
                "/atmosphere/contents/0100000000010000/cheats/toggles.txt", "/atmosphere/contents/zzzzzzzzzzzzzzzz/cheats/0d784a915f5b4ac2.cheat",
                "/atmosphere/contents/0100000000010000/cheats/0d784a915f5b4ac2.cheat.exe", "/atmosphere/contents/0100000000010000/exefs/code.ips" })
                Assert(!PathSafety.IsCheatPath(rejected));
            PathSafety.DemandWritable(enabled, allowCheatFiles: true); PathSafety.DemandWritable(disabled, allowCheatFiles: true);
            PathSafety.DemandWritable(titleDirectory, allowCheatFiles: true); PathSafety.DemandWritable(titleDirectory + "/cheats", allowCheatFiles: true);
            Expect<DeviceException>(() => PathSafety.DemandWritable(enabled));
            Expect<DeviceException>(() => PathSafety.DemandWritable(titleDirectory + "/cheats"));
            Expect<DeviceException>(() => PathSafety.DemandWritable("/atmosphere/contents/0100000000010000/exefs/code.ips", allowCheatFiles: true));
            Expect<DeviceException>(() => PathSafety.DemandWritable("/switch/SwitchDesk/nro", allowCheatFiles: true));
        }));
        await Test("cheat file parser accepts EdiZon shape and rejects junk", () => Sync(() =>
        {
            var sections = CheatManager.Parse("[Gold v1]\r\n01234567 ABCDEF01\r\n22222222 33333333\r\n\r\n[Invincible]\r\n02000000 00000000\r\n");
            Assert(sections.Length == 2 && sections[0].Name == "Gold v1" && sections[0].CodeLines.Length == 2 && sections[1].Name == "Invincible");
            Expect<InvalidDataException>(() => CheatManager.Parse("[Broken]\r\nzzzz\r\n"));
            Expect<InvalidDataException>(() => CheatManager.Parse("\r\n\r\n"));
            Assert(CheatManager.Parse("[0d784a915f5b4ac2.cheat]\n01234567 ABCDEF01\n").Length == 1);
            var commented = CheatManager.Parse("\uFEFF# 文件说明\r\n[Gold]\r\n01234567 ABCDEF01 ; 行内注释\r\n\r\n; 尾注\r\n[Second]\r\n02000000 00000000\r\n");
            Assert(commented.Length == 2 && commented[0].CodeLines.Single() == "01234567 ABCDEF01" && commented[1].Name == "Second");
        }));
        await Test("cheat install, list, disable and delete over the protocol", async () =>
        {
            await using var peer = new Peer(); await using var client = await peer.ConnectAsync();
            var content = Encoding.UTF8.GetBytes("[Test v1]\r\n01234567 ABCDEF01\r\n");
            Assert((await CheatManager.ListAsync(client, "0100000000010000")).Length == 0);
            await CheatManager.InstallAsync(client, "0100000000010000", "0d784a915f5b4ac2", content, null);
            var enabledPath = "/atmosphere/contents/0100000000010000/cheats/0d784a915f5b4ac2.txt";
            Assert(Encoding.UTF8.GetString(peer.SdFiles[enabledPath]) == "[Test v1]\n01234567 ABCDEF01\n");
            Assert(peer.SdDirectories.Contains("/atmosphere/contents/0100000000010000") && peer.SdDirectories.Contains("/atmosphere/contents/0100000000010000/cheats"));
            var listed = await CheatManager.ListAsync(client, "0100000000010000");
            var entry = listed.Single();
            Assert(entry.BuildId == "0d784a915f5b4ac2" && entry.Enabled && entry.Path == enabledPath);
            await CheatManager.SetEnabledAsync(client, enabledPath, false);
            Assert(peer.SdFiles.ContainsKey(enabledPath + ".off") && !peer.SdFiles.ContainsKey(enabledPath));
            Assert((await CheatManager.ListAsync(client, "0100000000010000")).Single().Enabled == false);
            await CheatManager.DeleteAsync(client, enabledPath + ".off");
            Assert((await CheatManager.ListAsync(client, "0100000000010000")).Length == 0);
            await Throws<InvalidDataException>(async () => await CheatManager.InstallAsync(client, "short", "0d784a915f5b4ac2", content, null));
        });
        await Test("homebrew index parser handles store style json", () => Sync(() =>
        {
            var store = HomebrewStore.ParseIndex(Encoding.UTF8.GetBytes("{\"list\":[{\"name\":\"App One\",\"author\":\"dev\",\"version\":\"1.2\",\"details\":\"工具\",\"dlurl\":\"https://example.com/app-one.nro\"},{\"name\":\"No Url\"},{\"name\":\"Zip App\",\"url\":\"https://example.com/pack/app.zip\"}]}"));
            Assert(store.Length == 1 && store[0].FileName == "app-one.nro" && store[0].Author == "dev" && store[0].Version == "1.2");
            var array = HomebrewStore.ParseIndex(Encoding.UTF8.GetBytes("[{\"name\":\"Solo\",\"dlurl\":\"https://example.com/solo.nro\"}]"));
            Assert(array.Length == 1 && array[0].Name == "Solo");
            Expect<InvalidDataException>(() => HomebrewStore.ParseIndex(Encoding.UTF8.GetBytes("{\"list\":[]}")));
            Expect<InvalidDataException>(() => HomebrewStore.ParseIndex(Encoding.UTF8.GetBytes("{\"list\":\"x\"}")));
        }));
        await Test("homebrew install writes to /switch and enforces protection", async () =>
        {
            await using var peer = new Peer(); await using var client = await peer.ConnectAsync();
            var app = new HomebrewApp("示例工具", "dev", "1.0", "说明", "https://example.com/tool.nro", "tool.nro");
            var payload = RandomNumberGenerator.GetBytes(999);
            "NRO0"u8.CopyTo(payload.AsSpan(0x10));
            var path = await HomebrewStore.InstallAsync(client, app, payload, null, null);
            Assert(path == "/switch/示例工具/tool.nro");
            Assert(peer.SdFiles[path].SequenceEqual(payload));
            await Throws<InvalidDataException>(async () => await HomebrewStore.InstallAsync(client, app, payload, "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", null));
            await Throws<InvalidDataException>(async () => await HomebrewStore.InstallAsync(client, app, payload, "abc", null));
            var protected_ = new HomebrewApp("SwitchDesk", "x", "1", "", "https://example.com/s.nro", "s.nro");
            await Throws<DeviceException>(async () => await HomebrewStore.InstallAsync(client, protected_, payload, null, null));
        });
        await Test("streaming argument builder covers usb and bridge modes", () => Sync(() =>
        {
            Assert(Streaming.BuildArguments(new(StreamingOptions.UsbMode)).SequenceEqual(new[] { "usb" }));
            Assert(Streaming.BuildArguments(new(StreamingOptions.UsbMode, null, "标题")).SequenceEqual(new[] { "usb", "--title", "标题" }));
            Assert(Streaming.BuildArguments(new(StreamingOptions.BridgeMode, "192.168.1.20")).SequenceEqual(new[] { "bridge", "192.168.1.20" }));
            Assert(Streaming.BuildArguments(new(StreamingOptions.BridgeMode)).SequenceEqual(new[] { "bridge" }));
            Assert(UsbStreamingRuntime.BuildSourceArguments(StreamingOptions.UsbMode, null).SequenceEqual(new[] { "usb" }));
            Expect<InvalidDataException>(() => Streaming.BuildArguments(new("Rtsp")));
            Expect<InvalidDataException>(() => Streaming.BuildArguments(new(StreamingOptions.BridgeMode, "bad host!")));
            Expect<InvalidDataException>(() => Streaming.ValidateAddress(StreamingOptions.BridgeMode, "http://x/y"));
        }));
        await Test("settings roundtrip keeps ftp and streaming fields", () => Sync(() =>
        {
            var directory = Path.Combine(root, "settings-ftp"); var store = new SettingsStore(directory);
            var settings = store.Load() with { SysDvrMode = StreamingOptions.BridgeMode, SysDvrAddress = "192.168.1.20", FtpHost = "10.0.0.5", FtpPort = 2121, FtpUser = "u", FtpPassword = "p" };
            store.Save(settings);
            var loaded = new SettingsStore(directory).Load();
            Assert(loaded.SysDvrMode == "Bridge" && loaded.SysDvrAddress == "192.168.1.20" && loaded.FtpHost == "10.0.0.5" && loaded.FtpPort == 2121 && loaded.FtpUser == "u");
            Assert(loaded.FtpPassword.Length == 0, "FTP password must not be persisted");
            var raw = File.ReadAllText(Path.Combine(directory, "settings.json"));
            Assert(!raw.Contains("\"p\"") && raw.Contains("\"ftpPassword\":\"\""), "settings file must store an empty password");
            var clearDirectory = Path.Combine(root, "settings-password-scrub"); Directory.CreateDirectory(clearDirectory);
            var legacyWithPassword = AppSettings.Default(clearDirectory) with { FtpPassword = "secret" };
            File.WriteAllText(Path.Combine(clearDirectory, "settings.json"), JsonSerializer.Serialize(legacyWithPassword, Wire.Json));
            Assert(new SettingsStore(clearDirectory).Load().FtpPassword.Length == 0);
            Assert(!File.ReadAllText(Path.Combine(clearDirectory, "settings.json")).Contains("secret"), "legacy plaintext password should be scrubbed");
            var legacyDirectory = Path.Combine(root, "settings-legacy"); Directory.CreateDirectory(legacyDirectory);
            File.WriteAllText(Path.Combine(legacyDirectory, "settings.json"), "{\"BackupDirectory\":\"C:\\\\b\",\"LibraryDirectory\":\"C:\\\\l\",\"ExportDirectory\":\"C:\\\\e\",\"SysDvrClient\":\"\",\"Retention\":5,\"AutoBackupKeys\":[]}");
            var legacy = new SettingsStore(legacyDirectory).Load();
            Assert(legacy.SysDvrMode == "Usb" && legacy.SysDvrAddress.Length == 0 && legacy.FtpPort == 21 && legacy.FtpHost.Length == 0);
        }));
        await Test("library missing marking only touches the scanned directory", async () =>
        {
            var first = Path.Combine(root, "library-scope-a"); var second = Path.Combine(root, "library-scope-b");
            Directory.CreateDirectory(first); Directory.CreateDirectory(second);
            var firstFile = Path.Combine(first, "a.nsp"); var secondFile = Path.Combine(second, "b.nsp");
            await File.WriteAllBytesAsync(firstFile, [1]); await File.WriteAllBytesAsync(secondFile, [2]);
            using var db = new LibraryDatabase(Path.Combine(root, "scope.db"));
            await db.ScanAsync(first, default); await db.ScanAsync(second, default);
            File.Delete(firstFile);
            await db.ScanAsync(second, default);
            Assert(db.List().Single(x => x.Path == Path.GetFullPath(firstFile)).Status != "文件缺失");
            await db.ScanAsync(first, default);
            Assert(db.List().Single(x => x.Path == Path.GetFullPath(firstFile)).Status == "文件缺失");
            Assert(db.List().Single(x => x.Path == Path.GetFullPath(secondFile)).Status != "文件缺失");
        });
        await Test("library scan marks missing files and explicit cleanup removes them", async () =>
        {
            var directory = Path.Combine(root, "library-missing"); Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, "game.nsp"); await File.WriteAllBytesAsync(file, [1]);
            using (var db = new LibraryDatabase(Path.Combine(root, "missing.db"))) await db.ScanAsync(directory, default);
            File.Delete(file);
            using (var db = new LibraryDatabase(Path.Combine(root, "missing.db")))
            {
                await db.ScanAsync(directory, default);
                Assert(db.List().Single().Status == "文件缺失");
                db.Remove(file); Assert(db.List().Count == 0);
            }
            var second = Path.Combine(directory, "second.xci"); await File.WriteAllBytesAsync(second, [2]);
            using (var db = new LibraryDatabase(Path.Combine(root, "missing2.db"))) await db.ScanAsync(directory, default);
            File.Delete(second);
            using (var db = new LibraryDatabase(Path.Combine(root, "missing2.db")))
            {
                await db.ScanAsync(directory, default);
                Assert(db.RemoveMissing() == 1 && db.List().Count == 0);
            }
        });
        await Test("library duplicate detection groups by title and by content", async () =>
        {
            var directory = Path.Combine(root, "library-dupes"); Directory.CreateDirectory(directory);
            var body = new byte[] { 9, 8, 7 };
            foreach (var name in new[] { "copy-a.nsp", "copy-b.nsp" }) await File.WriteAllBytesAsync(Path.Combine(directory, name), body);
            var unique = Path.Combine(directory, "unique.nsp"); await File.WriteAllBytesAsync(unique, [1, 1, 1, 1]);
            var manifest = new GameManifest(1, "DEV", "emuMMC", "0100000000010000", "重复游戏", new TitleComponent("0100000000010001", "Update", 0, "SdCard", 3), Convert.ToHexString(SHA256.HashData(body)), 3, DateTimeOffset.UtcNow);
            await File.WriteAllTextAsync(Path.Combine(directory, "copy-a.nsp") + ".switchdesk.json", JsonSerializer.Serialize(manifest, Wire.Json));
            using var db = new LibraryDatabase(Path.Combine(root, "dupes.db")); await db.ScanAsync(directory, default);
            var groups = await db.FindDuplicatesAsync(default);
            Assert(groups.Single(g => g.Reason == "内容完全一致").Items.Length == 2);
            var titled = groups.FirstOrDefault(g => g.Reason == "同 TitleID 与类型");
            Assert(titled == null);
            var sidecar = Path.Combine(directory, "copy-b.nsp") + ".switchdesk.json";
            await File.WriteAllTextAsync(sidecar, JsonSerializer.Serialize(manifest, Wire.Json));
            File.SetLastWriteTimeUtc(Path.Combine(directory, "copy-b.nsp"), DateTime.UtcNow.AddSeconds(2));
            await db.ScanAsync(directory, default);
            groups = await db.FindDuplicatesAsync(default);
            Assert(groups.Single(g => g.Reason == "同 TitleID 与类型").Items.Length == 2);
        });
        await Test("library verify all reports verified and mismatched counts", async () =>
        {
            var directory = Path.Combine(root, "library-verify"); Directory.CreateDirectory(directory);
            var good = Path.Combine(directory, "good.nsp"); var goodBytes = new byte[] { 4, 5, 6 };
            await File.WriteAllBytesAsync(good, goodBytes);
            var manifest = new GameManifest(1, "DEV", "emuMMC", "0100000000020000", "批量校验", new TitleComponent("0100000000020001", "Base", 0, "SdCard", 3), Convert.ToHexString(SHA256.HashData(goodBytes)), 3, DateTimeOffset.UtcNow);
            await File.WriteAllTextAsync(good + ".switchdesk.json", JsonSerializer.Serialize(manifest, Wire.Json));
            var bad = Path.Combine(directory, "bad.nsp"); await File.WriteAllBytesAsync(bad, [7]);
            var badManifest = new GameManifest(1, "DEV", "emuMMC", "0100000000030000", "批量校验坏", new TitleComponent("0100000000030001", "Base", 0, "SdCard", 1), Convert.ToHexString(SHA256.HashData(new byte[] { 9 })), 1, DateTimeOffset.UtcNow);
            await File.WriteAllTextAsync(bad + ".switchdesk.json", JsonSerializer.Serialize(badManifest, Wire.Json));
            using var db = new LibraryDatabase(Path.Combine(root, "verify.db"));
            await db.ScanAsync(directory, default);
            Assert(db.List().Where(x => x.Status == "有导出清单，待校验").Count() == 2);
            var report = await db.VerifyAllAsync(null, default);
            Assert(report.Verified == 1 && report.Mismatched == 1);
            Assert(db.List().Single(x => x.Path == good).Status == "已校验");
            Assert(db.List().Single(x => x.Path == bad).Status == "校验不匹配");
        });
        await Test("snapshot compare detects added removed and changed files", async () =>
        {
            await using var peer = new Peer(); await using var client = await peer.ConnectAsync();
            var store = new SnapshotStore(Path.Combine(root, "compare"));
            var first = await store.BackupAsync(client, Peer.Info, Peer.Save, null, default);
            peer.Files["main.dat"] = [9, 9]; peer.Files["extra.dat"] = [7];
            peer.Files.Remove("nested/config.bin");
            var second = await store.BackupAsync(client, Peer.Info, Peer.Save, null, default);
            Assert(second.Path != first.Path);
            var diff = await SnapshotTools.CompareAsync(first.Path, second.Path);
            Assert(diff.Summary.Contains("新增 1") && diff.Summary.Contains("删除 1") && diff.Summary.Contains("变更 1"));
            Assert(diff.Changed.Single().Path == "main.dat");
            Assert(diff.Added.Single().Path == "extra.dat");
            Assert(diff.Removed.Single().Path == "nested/config.bin");
            var same = await SnapshotTools.CompareAsync(first.Path, first.Path);
            Assert(same.Added.Length == 0 && same.Removed.Length == 0 && same.Changed.Length == 0 && same.Unchanged == 2);
            var other = Peer.Save with { UserId = "USER-B" };
            var foreign = await store.BackupAsync(client, Peer.Info, other, null, default);
            await Throws<InvalidDataException>(async () => await SnapshotTools.CompareAsync(first.Path, foreign.Path));
        });
        await Test("snapshot export never overwrites and adds a suffix", async () =>
        {
            await using var peer = new Peer(); await using var client = await peer.ConnectAsync();
            var store = new SnapshotStore(Path.Combine(root, "export"));
            var backup = await store.BackupAsync(client, Peer.Info, Peer.Save, null, default);
            var destinationDirectory = Path.Combine(root, "export-dest");
            var exported = await SnapshotTools.ExportSnapshotAsync(backup.Path, destinationDirectory);
            Assert(File.ReadAllBytes(exported).SequenceEqual(File.ReadAllBytes(backup.Path)));
            Assert(Path.GetFileName(exported).StartsWith("测试游戏_一生破晓_"));
            var second = await SnapshotTools.ExportSnapshotAsync(backup.Path, destinationDirectory);
            Assert(second != exported && File.Exists(second) && File.Exists(exported));
            Assert(File.ReadAllBytes(second).SequenceEqual(File.ReadAllBytes(backup.Path)));
            using (var zip = System.IO.Compression.ZipFile.Open(backup.Path, System.IO.Compression.ZipArchiveMode.Update))
            { zip.GetEntry("data/main.dat")!.Delete(); using var data = zip.CreateEntry("data/main.dat").Open(); data.Write(new byte[] { 0 }); }
            await Throws<InvalidDataException>(async () => await SnapshotTools.ExportSnapshotAsync(backup.Path, Path.Combine(root, "export-dest2")));
        });
        await Test("save sync plans classify backup, restore and conflict", async () =>
        {
            await using var peer = new Peer(); await using var client = await peer.ConnectAsync();
            var store = new SnapshotStore(Path.Combine(root, "sync"));
            var states = new Dictionary<string, SaveSyncState>();
            var plans = await SaveSync.BuildPlansAsync(client, Peer.Info, [Peer.Save], [], states, null, default);
            Assert(plans.Single().Direction == SaveSyncDirection.NeedsFirstBackup);
            var snapshot = await store.BackupAsync(client, Peer.Info, Peer.Save, null, default);
            var inspection = await client.QueryAsync<SaveDeviceInspection>("saves.inspect",
                new { key = Peer.Save.Key, titleId = Peer.Save.TitleId, userId = Peer.Save.UserId, environment = Peer.Info.Environment }, default);
            var state = SaveSync.RecordSync(Peer.Save, Peer.Info, inspection.Fingerprint, snapshot.Manifest.ContentHash, DateTimeOffset.UtcNow);
            states[state.Key] = state;
            plans = await SaveSync.BuildPlansAsync(client, Peer.Info, [Peer.Save], store.List().ToArray(), states, null, default);
            Assert(plans.Single().Direction == SaveSyncDirection.UpToDate);
            peer.Files["main.dat"] = [7, 7, 7];
            plans = await SaveSync.BuildPlansAsync(client, Peer.Info, [Peer.Save], store.List().ToArray(), states, null, default);
            Assert(plans.Single().Direction == SaveSyncDirection.DeviceNewer);
            peer.Files["main.dat"] = [1, 2, 3, 4, 5]; peer.Files["extra.dat"] = [8];
            var second = await store.BackupAsync(client, Peer.Info, Peer.Save, null, default);
            Assert(second.Path != snapshot.Path);
            peer.Files.Remove("extra.dat");
            plans = await SaveSync.BuildPlansAsync(client, Peer.Info, [Peer.Save], store.List().ToArray(), states, null, default);
            Assert(plans.Single().Direction == SaveSyncDirection.PcNewer && plans.Single().PcSnapshot!.Path == second.Path);
            peer.Files["main.dat"] = [9];
            plans = await SaveSync.BuildPlansAsync(client, Peer.Info, [Peer.Save], store.List().ToArray(), states, null, default);
            Assert(plans.Single().Direction == SaveSyncDirection.Conflict);
        });
        await Test("cross-environment save migration is gated and atomic", async () =>
        {
            await using var peer = new Peer(); await using var client = await peer.ConnectAsync();
            var store = new SnapshotStore(Path.Combine(root, "foreign"));
            var backup = await store.BackupAsync(client, Peer.Info, Peer.Save, null, default);
            using (var zip = System.IO.Compression.ZipFile.Open(backup.Path, System.IO.Compression.ZipArchiveMode.Update))
            {
                var entry = zip.GetEntry("manifest.json")!;
                SaveManifest manifest; using (var stream = entry.Open()) manifest = JsonSerializer.Deserialize<SaveManifest>(stream, Wire.Json)!;
                var foreign = manifest with { DeviceId = "FOREIGN-DEVICE", Environment = "sysMMC" };
                entry.Delete();
                using var replacement = zip.CreateEntry("manifest.json").Open();
                JsonSerializer.Serialize(replacement, foreign, Wire.Json);
            }
            var writes = peer.WriteOperations;
            await Throws<DeviceException>(() => store.RestoreAsync(client, Peer.Info, Peer.Save, backup.Path, null, default));
            Assert(peer.WriteOperations == writes);
            peer.RejectForeignSource = true;
            await Throws<DeviceException>(async () => await store.RestoreAsync(client, Peer.Info, Peer.Save, backup.Path, null, default, foreignSource: true));
            peer.RejectForeignSource = false;
            var before = await client.QueryAsync<SaveDeviceInspection>("saves.inspect",
                new { key = Peer.Save.Key, titleId = Peer.Save.TitleId, userId = Peer.Save.UserId, environment = Peer.Info.Environment }, default);
            await store.RestoreAsync(client, Peer.Info, Peer.Save, backup.Path, null, default,
                expectedFingerprint: before.Fingerprint, operationId: Guid.NewGuid().ToString("N"), foreignSource: true);
            Assert(peer.RestoreCommits == 1 && peer.RestoreForeignSeen);
            await Throws<DeviceException>(() => store.RestoreAsync(client, Peer.Info, Peer.Save with { UserId = "USER-B" }, backup.Path, null, default, foreignSource: true));
        });
    }
    private static void Expect<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } catch (Exception ex) { throw new Exception("Expected " + typeof(T).Name + " but got " + ex.GetType().Name); } throw new Exception("Expected " + typeof(T).Name); }
    private sealed class CollectProgress(List<TransferProgress> sink) : IProgress<TransferProgress>
    { public void Report(TransferProgress value) => sink.Add(value); }
}
