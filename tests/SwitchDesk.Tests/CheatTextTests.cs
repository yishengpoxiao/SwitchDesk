using System.IO;
using SwitchDesk.Core;

namespace SwitchDesk.Tests;
internal static partial class Program
{
    private static async Task CheatTextTestsAsync()
    {
        await Test("standard Atmosphere TXT paths keep protected paths closed", () => Sync(() =>
        {
            var path = CheatManager.CheatPath("0100000000010000", "0123456789ABCDEF");
            Assert(path.EndsWith("0123456789abcdef.txt", StringComparison.Ordinal));
            Assert(PathSafety.IsCheatFile(path) && PathSafety.IsCheatFile(path + ".off"));
            Assert(!PathSafety.IsCheatFile(path.Replace("0123456789abcdef.txt", "toggles.txt", StringComparison.Ordinal)));
            PathSafety.DemandWritable(path, allowCheatFiles: true);
            Expect<DeviceException>(() => PathSafety.DemandWritable(path));
            var parsed = CheatManager.Parse("[Fixture]\n80000001\n04000000 00000000 00000001\n08000000 00000000 00000000 00000001\n20000000\n");
            Assert(parsed.Single().CodeLines.Length == 4);
            Expect<InvalidDataException>(() => CheatManager.Parse("[Invalid]\n00000000 00000000 00000000 00000000 00000000\n"));
        }));
        await Test("TXT and legacy cheat imports publish standard TXT files only", async () =>
        {
            var directory = Path.Combine(root, "txt-cheat-import"); Directory.CreateDirectory(directory);
            const string code = "[Fixture]\n04000000 00000000 00000001\n";
            await File.WriteAllTextAsync(Path.Combine(directory, "0123456789abcdef.txt"), code);
            await File.WriteAllTextAsync(Path.Combine(directory, "fedcba9876543210.cheat"), code);
            await File.WriteAllTextAsync(Path.Combine(directory, "notes.txt"), "not a cheat");
            await using var peer = new Peer(); await using var client = await peer.ConnectAsync();
            Assert(await CheatManager.InstallFolderAsync(client, Peer.Save.TitleId, directory, null) == 2);
            var listed = await CheatManager.ListAsync(client, Peer.Save.TitleId);
            Assert(listed.Length == 2 && listed.All(e => e.Path.EndsWith(".txt", StringComparison.Ordinal)));
        });
    }
}
