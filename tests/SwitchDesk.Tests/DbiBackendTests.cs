using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using SwitchDesk.Core;

namespace SwitchDesk.Tests;

internal static partial class Program
{
    private static async Task DbiBackendTestsAsync()
    {
        var directory = Path.Combine(root, "dbi-backend"); Directory.CreateDirectory(directory);
        var backend = Path.Combine(directory, "official backend fixture.exe");
        await File.WriteAllTextAsync(backend, "Fixture only; never executed.");
        var package = Path.Combine(directory, "察言观色 & $(literal) [v0].nsp");
        byte[] bytes = [1, 2, 3, 4]; await File.WriteAllBytesAsync(package, bytes);
        var source = new DbiBackendSource(package, bytes.Length, Fingerprints.Of(bytes));
        var cacheBytes = "Mock backend executable; never executed."u8.ToArray();
        var asset = new DbiBackendAsset(DbiBackendService.OfficialUrl, cacheBytes.Length, Fingerprints.Of(cacheBytes));
        void Writable(string path) { using var unlocked = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read); }
        void Locked(string path) => Expect<IOException>(() => Writable(path));

        await Test("DBI backend pins the official release and preserves literal visible process arguments", () => Sync(() =>
        {
            Assert(DbiBackendService.OfficialVersion == "912ru" && DbiBackendService.OfficialLength == 10609140);
            Assert(DbiBackendService.OfficialSha256 == "D2CA9E101609DFDFF56F94446B08B9DE75E344489885A78DAAC6021E43992FF2");
            Assert(DbiBackendService.OfficialUrl == "https://github.com/rashevskyv/dbi/releases/download/912ru/dbibackend.exe");
            var second = Path.Combine(directory, "a b & c.xci");
            var info = DbiBackendService.BuildStartInfo(backend, [package, second]);
            Assert(info.FileName == backend && info.WorkingDirectory == directory && info.ArgumentList.SequenceEqual([package, second]));
            Assert(info.Arguments.Length == 0 && !info.UseShellExecute && !info.CreateNoWindow
                && !info.RedirectStandardOutput && !info.RedirectStandardError && info.WindowStyle == ProcessWindowStyle.Normal);
        }));
        await Test("DBI argument validation rejects ambiguous names, unsupported files and Windows length overflow", async () =>
        {
            string[][] invalid = [[], ["relative.nsp"], [package, Path.Combine(directory, "other", Path.GetFileName(package).ToUpperInvariant())],
                [Path.Combine(directory, "file\nname.nsp")], [Path.Combine(directory, "plugin.nro")],
                Enumerable.Range(0, 400).Select(i => Path.Combine(directory, new string('a', 120) + i + ".nsp")).ToArray()];
            foreach (var paths in invalid)
                await Throws<InvalidDataException>(() => Sync(() => DbiBackendService.BuildStartInfo(backend, paths)));
            await Throws<InvalidDataException>(() => Sync(() => DbiBackendService.ValidateExecutable("relative.exe")));
            await Throws<FileNotFoundException>(() => Sync(() => DbiBackendService.ValidateExecutable(package)));
            await Throws<FileNotFoundException>(() => Sync(() => DbiBackendService.ValidateExecutable(Path.Combine(directory, "missing.exe"))));
        });
        await Test("DBI preparation validates expected bytes and holds files read-only without starting a process", async () =>
        {
            using var service = new DbiBackendService(); var starts = 0;
            var process = new DbiFixtureProcess();
            await using var session = await service.PrepareAsync(backend, [source], info => { starts++; Assert(info.ArgumentList.Single() == package); return process; }, default);
            Assert(starts == 0 && !session.HasStarted && !session.HasExited && session.Sources.SequenceEqual([source]));
            Locked(package); Locked(backend);
            using (var read = File.OpenRead(package)) Assert(read.Length == bytes.Length);
            session.Start(); Assert(starts == 1 && session.HasStarted && !session.HasExited && session.ProcessId == 912);
            Locked(package);
            await Throws<InvalidOperationException>(() => Sync(session.Start));
            process.Exit(0); await session.WaitForExitAsync();
            Assert(session.HasExited); Writable(package); Writable(backend);
        });
        await Test("DBI failed source constraints and cancellation release every preparation handle", async () =>
        {
            using var service = new DbiBackendService();
            var different = Path.Combine(directory, "different.nsp"); await File.WriteAllBytesAsync(different, bytes);
            foreach (var rejected in new[] { source with { ExpectedLength = 5 }, source with { ExpectedSha256 = new string('0', 64) },
                source with { ExpectedLength = -1 }, source with { ExpectedSha256 = "bad" } })
            {
                await Throws<InvalidDataException>(async () => await service.PrepareAsync(backend, [source, rejected with { Path = different }], default));
                Writable(package); Writable(different); Writable(backend);
            }
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Throws<OperationCanceledException>(async () => await service.PrepareAsync(backend, [source], cancelled.Token));
            Writable(package); Writable(backend);
            var changed = Path.Combine(directory, "changed.xcz"); await File.WriteAllBytesAsync(changed, [4, 3, 2, 1]);
            await Throws<InvalidDataException>(async () => await service.PrepareAsync(backend, [new(changed, bytes.Length, Fingerprints.Of(bytes))], default));
            Writable(changed); Writable(backend);
        });
        await Test("DBI cancelled waits and disposal keep packages locked until the owned process actually exits", async () =>
        {
            using var service = new DbiBackendService(); var process = new DbiFixtureProcess();
            var session = await service.PrepareAsync(backend, [source], _ => process, default); session.Start();
            try
            {
                using var cancel = new CancellationTokenSource(); cancel.Cancel();
                await Throws<OperationCanceledException>(() => session.WaitForExitAsync(cancel.Token));
                Assert(!session.HasExited && process.CloseRequests == 0 && !process.Disposed); Locked(package);
                Assert(!session.RequestClose() && process.CloseRequests == 1 && !process.HasExited);
                var disposing = session.DisposeAsync().AsTask(); Assert(!disposing.IsCompleted); Locked(package);
                process.Exit(0); await disposing; Writable(package);
                Assert(process.Disposed);
            }
            finally { process.Exit(0); await session.DisposeAsync(); }
        });
        await Test("DBI backend errors report exit codes and release locks without killing a process", async () =>
        {
            using var service = new DbiBackendService(); var process = new DbiFixtureProcess();
            await using var session = await service.PrepareAsync(backend, [source], _ => process, default);
            session.Start(); process.Exit(23);
            try { await session.WaitForExitAsync(); Assert(false, "nonzero DBI exit accepted"); }
            catch (IOException error) { Assert(error.Message.Contains("23", StringComparison.Ordinal)); }
            await session.DisposeAsync(); Assert(process.Disposed && process.CloseRequests == 0); Writable(package);
        });
        await Test("DBI failed process launch and unused prepared sessions release source locks", async () =>
        {
            using var service = new DbiBackendService();
            await using (var failed = await service.PrepareAsync(backend, [source], _ => throw new IOException("fixture launch failure"), default))
                await Throws<IOException>(() => Sync(failed.Start));
            Writable(package); Writable(backend);
            var unused = await service.PrepareAsync(backend, [source], default);
            await unused.DisposeAsync(); Writable(package);
            await Throws<ObjectDisposedException>(() => Sync(unused.Start));
        });
        await Test("DBI official download atomically publishes verified cache and reuses it without HTTP", async () =>
        {
            var requests = 0; var cache = Path.Combine(directory, "verified-cache");
            using var http = new HttpClient(new DbiFixtureHandler(request =>
            { requests++; Assert(request.RequestUri!.AbsoluteUri == asset.Url); return new(HttpStatusCode.OK) { Content = new ByteArrayContent(cacheBytes) }; }));
            using var service = new DbiBackendService(http, asset);
            var path = await service.DownloadOfficialAsync(cache, null, default);
            Assert(path == DbiBackendService.GetOfficialCachePath(cache) && (await File.ReadAllBytesAsync(path)).SequenceEqual(cacheBytes));
            Assert(await service.ResolveAsync(null, cache, null, default) == path && requests == 1);
            Assert(!Directory.EnumerateFiles(cache, "*.partial", SearchOption.AllDirectories).Any());
            await using var session = await service.PrepareAsync(path, [source], default);
            Locked(path); Locked(package);
        });
        await Test("DBI corrupt official cache is rejected rather than executed or silently reused", async () =>
        {
            var cache = Path.Combine(directory, "corrupt-cache"); var path = DbiBackendService.GetOfficialCachePath(cache);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllBytesAsync(path, new byte[cacheBytes.Length]);
            using var http = new HttpClient(new DbiFixtureHandler(_ => throw new Exception("corrupt cache must not be used or replaced implicitly")));
            using var service = new DbiBackendService(http, asset);
            await Throws<InvalidDataException>(() => service.DownloadOfficialAsync(cache, null, default));
            await Throws<InvalidDataException>(() => service.ResolveAsync(path, cache, null, default));
            Assert((await File.ReadAllBytesAsync(path)).All(value => value == 0));
        });
        await Test("DBI verifies official executable again under its preparation lock", async () =>
        {
            var cache = Path.Combine(directory, "changed-cache");
            using var http = new HttpClient(new DbiFixtureHandler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(cacheBytes) }));
            using var service = new DbiBackendService(http, asset);
            var path = await service.DownloadOfficialAsync(cache, null, default);
            await File.WriteAllBytesAsync(path, new byte[cacheBytes.Length]);
            await Throws<InvalidDataException>(async () => await service.PrepareAsync(path, [source], default));
            Writable(path); Writable(package);
        });
        await Test("DBI download rejects wrong lengths and digests without leaving executable or partial files", async () =>
        {
            var cases = new[] { new byte[cacheBytes.Length], cacheBytes[..^1], cacheBytes.Concat(new byte[] { 0 }).ToArray() };
            for (var i = 0; i < cases.Length; i++)
            {
                var body = cases[i]; var cache = Path.Combine(directory, "invalid-download-" + i);
                using var http = new HttpClient(new DbiFixtureHandler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }));
                using var service = new DbiBackendService(http, asset);
                await Throws<InvalidDataException>(() => service.DownloadOfficialAsync(cache, null, default));
                Assert(!Directory.EnumerateFiles(cache, "*", SearchOption.AllDirectories).Any());
            }
        });
        await Test("DBI cancelled body download removes partial files and never publishes an executable", async () =>
        {
            var cache = Path.Combine(directory, "cancelled-download"); using var cancel = new CancellationTokenSource();
            using var http = new HttpClient(new DbiFixtureHandler(_ => new(HttpStatusCode.OK) { Content = new StreamContent(new DbiCancellingBody(cancel)) }));
            using var service = new DbiBackendService(http, asset);
            await Throws<OperationCanceledException>(() => service.DownloadOfficialAsync(cache, null, cancel.Token));
            Assert(!Directory.EnumerateFiles(cache, "*", SearchOption.AllDirectories).Any());
        });
        await Test("DBI checks actual streamed lengths despite a matching advertised Content-Length", async () =>
        {
            foreach (var extra in new[] { false, true })
            {
                var cache = Path.Combine(directory, extra ? "extra-body" : "short-body");
                var body = extra ? cacheBytes.Concat(new byte[] { 0 }).ToArray() : cacheBytes[..^1];
                using var http = new HttpClient(new DbiFixtureHandler(_ =>
                {
                    var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
                    response.Content.Headers.ContentLength = asset.Length; return response;
                }));
                using var service = new DbiBackendService(http, asset);
                if (extra) await Throws<InvalidDataException>(() => service.DownloadOfficialAsync(cache, null, default));
                else await Throws<EndOfStreamException>(() => service.DownloadOfficialAsync(cache, null, default));
                Assert(!Directory.EnumerateFiles(cache, "*", SearchOption.AllDirectories).Any());
            }
        });
        await Test("DBI custom backend resolution never downloads and settings normalize old missing paths", async () =>
        {
            using var http = new HttpClient(new DbiFixtureHandler(_ => throw new Exception("custom backend must not download")));
            using var service = new DbiBackendService(http, asset);
            Assert(await service.ResolveAsync(backend, Path.Combine(directory, "unused-cache"), null, default) == backend);
            var settingsDirectory = Path.Combine(directory, "settings"); var store = new SettingsStore(settingsDirectory);
            Assert(store.Load().DbiBackendPath == "");
            store.Save(AppSettings.Default(settingsDirectory) with { DbiBackendPath = backend });
            Assert(store.Load().DbiBackendPath == backend);
            await File.WriteAllTextAsync(Path.Combine(settingsDirectory, "settings.json"),
                JsonSerializer.Serialize(AppSettings.Default(settingsDirectory) with { DbiBackendPath = null! }, Wire.Json));
            Assert(store.Load().DbiBackendPath == "");
        });
    }

    private sealed class DbiFixtureHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(send(request)); }
    }
    private sealed class DbiFixtureProcess : IDbiBackendProcess
    {
        private readonly TaskCompletionSource exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int exitCode;
        public int Id => 912;
        public bool HasExited => exit.Task.IsCompleted;
        public int ExitCode => HasExited ? exitCode : throw new InvalidOperationException();
        public int CloseRequests { get; private set; }
        public bool Disposed { get; private set; }
        public void Exit(int code) { if (HasExited) return; exitCode = code; exit.TrySetResult(); }
        public Task WaitForExitAsync(CancellationToken ct) => exit.Task.WaitAsync(ct);
        public bool CloseMainWindow() { CloseRequests++; return false; }
        public void Dispose() { Assert(HasExited, "live DBI process was disposed"); Disposed = true; }
    }
    private sealed class DbiCancellingBody(CancellationTokenSource cancellation) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); buffer.Span[0] = 1; cancellation.Cancel(); return ValueTask.FromResult(1); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
