using SwitchDesk.Core;

namespace SwitchDesk.App;

public partial class MainWindow
{
    private void InitializeSaveLibrary()
    {
        vm.Bind("SaveLibraryRefresh", _ => _ = Guard(async () =>
        {
            if (vm.Online && !ManagementPaused && !closingApplication)
                await RunJob("刷新存档资料", async (_, ct) => { await RefreshDeviceAsync(ct); return true; });
            else RefreshSaveLibrary();
        }), _ => Volatile.Read(ref activeJobs) == 0);
        vm.Bind("SaveLibraryReveal", _ => Reveal(settings.BackupDirectory));
    }
    private void RefreshSaveLibrary()
    {
        ClearSaveSyncResults();
        var scan = new SnapshotStore(settings.BackupDirectory).Scan();
        vm.SaveLibrary.SetData(saves, scan, device, vm.Online, synced);
    }
}
