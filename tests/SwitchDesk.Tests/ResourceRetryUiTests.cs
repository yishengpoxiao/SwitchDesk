using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using SwitchDesk.App;
using SwitchDesk.Core;

namespace SwitchDesk.Tests;

internal static partial class Program
{
    private static async Task ResourceRetryUiTestsAsync(MainWindow window)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        T Get<T>(string field) => (T)typeof(MainWindow).GetField(field, flags)!.GetValue(window)!;
        async Task Download() => await (Task)typeof(MainWindow).GetMethod("DownloadResourceAsync", flags)!.Invoke(window, null)!;
        var vm = Get<ShellViewModel>("vm"); var state = Get<Phase2State>("phase2");
        var library = Get<LibraryDatabase>("library");
        var origins = Get<Dictionary<string, (ContentSource Source, ContentEntry Entry)>>("ownedResourceEntries");
        var directory = Path.Combine(root, "resource-retry-ui"); Directory.CreateDirectory(directory);
        var downloads = Path.Combine(Get<AppSettings>("settings").LibraryDirectory, "Downloads");
        byte[] content = [1, 2, 3], changed = [9, 9, 9];
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serve = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                using var socket = await listener.AcceptTcpClientAsync(stop.Token);
                await using var stream = socket.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                Assert((await reader.ReadLineAsync(stop.Token))?.StartsWith("GET /retry.nsp ", StringComparison.Ordinal) == true);
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(stop.Token))) { }
                byte[] payload = attempt switch { 0 => content[..1], 2 => [1, 2, 3, 4], 3 => [3, 2, 1], _ => content };
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {(attempt == 0 ? 3 : payload.Length)}\r\nConnection: close\r\n\r\n"), stop.Token);
                await stream.WriteAsync(payload, stop.Token);
            }
        });
        try
        {
            foreach (var owned in new[] { false, true })
            {
                var name = owned ? "owned" : "direct";
                var sourceDirectory = Path.Combine(directory, name); Directory.CreateDirectory(sourceDirectory);
                var oldPath = Path.Combine(sourceDirectory, "changed.nsp"); await File.WriteAllBytesAsync(oldPath, changed);
                var sourcePath = Path.Combine(sourceDirectory, "source.nsp"); await File.WriteAllBytesAsync(sourcePath, changed);
                var source = new ContentSource("resource-retry-" + name, "Retry fixture", ContentSourceKind.Local, sourceDirectory);
                var contract = new ContentEntry(Peer.Save.TitleId, Peer.Save.TitleId, "Base", 0, content.Length, Fingerprints.Of(content), "source.nsp");
                var entry = new ResourceEntry("Retry " + name, ResourceKind.Game,
                    owned ? new Uri(sourcePath).AbsoluteUri : $"http://127.0.0.1:{port}/retry.nsp", "retry.nsp")
                { Size = content.Length, Sha256 = Fingerprints.Of(content) };
                var previous = new ResourceDownload(entry, oldPath, content.Length, Fingerprints.Of(content), true, DateTimeOffset.UtcNow)
                { ContentSourceId = owned ? source.Id : null, ExpectedContent = owned ? contract : null };
                state.Put("resource-downloads", entry.Id, previous);
                if (owned) { state.Put("content-sources", source.Id, source); origins.Add(entry.Id, (source, contract)); }
                try
                {
                    vm.Resources.SelectedType = "全部类型"; vm.Resources.SetResults([new ResourceResultRow(entry)]);
                    var priorRetryDirectories = Directory.GetDirectories(downloads, "retry-*").Order(StringComparer.Ordinal).ToArray();
                    if (owned) await Throws<InvalidDataException>(Download); else await Throws<IOException>(Download);
                    Assert(state.Get<ResourceDownload>("resource-downloads", entry.Id)!.Value.Value == previous, "failed retry replaced the old receipt");
                    Assert((await File.ReadAllBytesAsync(oldPath)).SequenceEqual(changed), "failed retry changed the original file");
                    Assert(Directory.GetDirectories(downloads, "retry-*").Order(StringComparer.Ordinal).SequenceEqual(priorRetryDirectories), "failed retry left its temporary download directory");
                    await File.WriteAllBytesAsync(sourcePath, content);
                    await Download();
                    var repaired = state.Get<ResourceDownload>("resource-downloads", entry.Id)!.Value.Value;
                    Assert(repaired.Path != oldPath && (await File.ReadAllBytesAsync(repaired.Path)).SequenceEqual(content), "retry did not publish a verified new copy");
                    Assert((await File.ReadAllBytesAsync(oldPath)).SequenceEqual(changed), "successful retry overwrote the changed file");
                    Assert(repaired.ExpectedContent == previous.ExpectedContent && repaired.ContentSourceId == previous.ContentSourceId, "retry lost the selected content contract");
                    await Download();
                    Assert(state.Get<ResourceDownload>("resource-downloads", entry.Id)!.Value.Value.Path == repaired.Path, "verified retry was downloaded again");
                }
                finally
                {
                    origins.Remove(entry.Id);
                    if (state.Get<ContentSource>("content-sources", source.Id) is {} savedSource) state.Remove("content-sources", source.Id, savedSource.Revision);
                    if (state.Get<ResourceDownload>("resource-downloads", entry.Id) is {} savedReceipt)
                    { library.Remove(savedReceipt.Value.Path); state.Remove("resource-downloads", entry.Id, savedReceipt.Revision); }
                }
            }
            foreach (var changedDigest in new[] { false, true })
            {
                var fileName = changedDigest ? "retry-revised-digest.nsp" : "retry-revised-size.nsp";
                var oldPath = Path.Combine(downloads, fileName); await File.WriteAllBytesAsync(oldPath, content);
                var oldEntry = new ResourceEntry("Revised index fixture", ResourceKind.Game, $"http://127.0.0.1:{port}/retry.nsp", fileName)
                { Size = content.Length, Sha256 = changedDigest ? Fingerprints.Of(content) : null };
                byte[] replacement = changedDigest ? [3, 2, 1] : [1, 2, 3, 4];
                var selected = oldEntry with { Size = replacement.Length, Sha256 = changedDigest ? Fingerprints.Of(replacement) : null };
                var previous = new ResourceDownload(oldEntry, oldPath, content.Length, Fingerprints.Of(content), changedDigest, DateTimeOffset.UtcNow);
                state.Put("resource-downloads", oldEntry.Id, previous);
                try
                {
                    Assert((selected.Id != oldEntry.Id) == changedDigest);
                    vm.Resources.SetResults([new ResourceResultRow(selected)]);
                    await Download();
                    var updated = state.Get<ResourceDownload>("resource-downloads", selected.Id)!.Value.Value;
                    Assert(updated.Path != oldPath && (await File.ReadAllBytesAsync(updated.Path)).SequenceEqual(replacement));
                    Assert((await File.ReadAllBytesAsync(oldPath)).SequenceEqual(content), "index revision overwrote the previous file");
                    if (changedDigest) Assert(state.Get<ResourceDownload>("resource-downloads", oldEntry.Id)!.Value.Value == previous);
                }
                finally
                {
                    foreach (var id in new[] { oldEntry.Id, selected.Id }.Distinct())
                        if (state.Get<ResourceDownload>("resource-downloads", id) is {} savedReceipt)
                        { library.Remove(savedReceipt.Value.Path); state.Remove("resource-downloads", id, savedReceipt.Revision); }
                }
            }
            await serve.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            stop.Cancel(); listener.Stop();
            try { await serve; } catch (Exception) when (stop.IsCancellationRequested) { }
            vm.Resources.SetDownloads(state.List<ResourceDownload>("resource-downloads"));
        }
    }
}
