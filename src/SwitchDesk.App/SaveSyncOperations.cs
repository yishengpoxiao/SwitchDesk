using System.IO;
using SwitchDesk.Core;

namespace SwitchDesk.App;

/// <summary>双向存档同步（主机↔电脑，全部有存档的游戏）与跨系统迁移（sysMMC ↔ emuMMC）。</summary>
public partial class MainWindow
{
    private sealed record SyncContext(DeviceClient Peer, DeviceInfo Device, string BackupDirectory, long Generation);
    private SyncContext? syncContext;
    private long syncGeneration;

    private void ClearSaveSyncResults()
    {
        ++syncGeneration;
        syncContext = null;
        vm.SaveLibrary.ClearSync();
    }

    private bool IsCurrentSyncRow(object? parameter) => parameter is SaveSyncRow row && syncContext is {} context &&
        vm.SaveLibrary.SyncRows.Contains(row) && ReferenceEquals(client, context.Peer) &&
        device?.DeviceId == context.Device.DeviceId && device.Environment == context.Device.Environment &&
        settings.BackupDirectory == context.BackupDirectory;

    private SyncContext RequireSyncRow(SaveSyncRow row, string capability)
    {
        RequireDevice(capability);
        if (!IsCurrentSyncRow(row)) throw new IOException("同步结果已失效，请重新检查同步");
        return syncContext!;
    }

    private void DemandSyncContext(DeviceClient peer, DeviceInfo current, SyncContext context)
    {
        if (context.Generation != syncGeneration || !ReferenceEquals(peer, context.Peer) || current.DeviceId != context.Device.DeviceId ||
            current.Environment != context.Device.Environment || settings.BackupDirectory != context.BackupDirectory)
            throw new IOException("主机环境或备份目录已变化，请重新检查同步");
    }

    private Dictionary<string, SaveSyncState> LoadSyncStates() =>
        syncStore.Load().ToDictionary(s => s.Key, StringComparer.Ordinal);

    private void UpsertSyncState(SaveSyncState state)
    {
        var states = syncStore.Load().Where(s => s.Key != state.Key).Append(state).OrderBy(s => s.Key).ToArray();
        syncStore.Save(states);
    }

    private async Task SaveSyncCheckAsync()
    {
        var (connection, identity) = RequireDevice("saves.inspect");
        ClearSaveSyncResults();
        var context = new SyncContext(connection, identity, settings.BackupDirectory, syncGeneration);
        var snapshotList = new SnapshotStore(settings.BackupDirectory).List().ToArray();
        var states = LoadSyncStates();
        var plans = await RunJob("检查存档同步", async (job, ct) =>
        {
            var (peer, current) = RequireDevice("saves.inspect");
            DemandSyncContext(peer, current, context);
            var here = saves.Where(s => s.Environment == current.Environment).ToArray();
            if (here.Length == 0) throw new IOException("当前系统还没有可同步的存档");
            return await SaveSync.BuildPlansAsync(peer, current, here, snapshotList, states, Progress(job), ct, context.BackupDirectory);
        });
        DemandSyncContext(RequireDevice("saves.inspect").Client, device!, context);
        syncContext = context;
        var pending = plans.Count(p => p.Direction is SaveSyncDirection.DeviceNewer or SaveSyncDirection.PcNewer or SaveSyncDirection.Conflict or SaveSyncDirection.NeedsFirstBackup);
        vm.SaveLibrary.SetSyncPlans(plans.Select(p => new SaveSyncRow(p)),
            $"同步检查完成 {DateTime.Now:HH:mm:ss} · 已同步 {plans.Count(p => p.Direction == SaveSyncDirection.UpToDate)} · 待处理 {pending}");
    }

    private async Task SaveSyncRowBackupAsync(object? parameter)
    {
        if (parameter is not SaveSyncRow row) throw new IOException("请选择要同步的存档");
        var save = row.Plan.Save; var context = RequireSyncRow(row, "saves.inspect");
        if (!row.CanBackup) throw new IOException("此同步结果无需备份，请重新检查同步");
        await RunJob("同步备份 · " + save.GameName, async (job, ct) =>
        {
            var (peer, current) = RequireDevice("saves.inspect");
            DemandSyncContext(peer, current, context);
            // 在备份前取指纹，备份后再取一次并比对——防止备份期间游戏写入导致指纹错配
            var beforeFingerprint = (await peer.QueryAsync<SaveDeviceInspection>("saves.inspect",
                new { key = save.Key, titleId = save.TitleId, userId = save.UserId, environment = current.Environment }, ct)).Fingerprint;
            var store = new SnapshotStore(settings.BackupDirectory);
            var snapshot = await store.BackupAsync(peer, current, save, Progress(job), ct);
            var afterFingerprint = (await peer.QueryAsync<SaveDeviceInspection>("saves.inspect",
                new { key = save.Key, titleId = save.TitleId, userId = save.UserId, environment = current.Environment }, ct)).Fingerprint;
            if (!Fingerprints.Equal(beforeFingerprint, afterFingerprint))
                throw new IOException("备份期间存档被游戏修改，请关闭游戏后重试");
            store.Prune(settings.Retention, ct, snapshot.Manifest);
            UpsertSyncState(SaveSync.RecordSync(save, current, afterFingerprint, snapshot.Manifest.ContentHash, DateTimeOffset.UtcNow));
            await Dispatcher.InvokeAsync(LoadLocalLists); return true;
        });
        vm.Notify("已备份到电脑并记录同步状态", settings.BackupDirectory);
        await SaveSyncCheckAsync();
    }

    private async Task SaveSyncRowRestoreAsync(object? parameter)
    {
        if (parameter is not SaveSyncRow row || row.Plan.PcSnapshot is not { } snapshot) throw new IOException("请选择要写回的电脑快照");
        var save = row.Plan.Save;
        var context = RequireSyncRow(row, "restore.atomic.v1");
        if (!row.CanRestore) throw new IOException("此同步结果不可写回，请重新检查同步");
        if (!Dialogs.Confirm(this, "写回主机存档",
            $"游戏  {save.GameName}\n用户  {save.UserName}\n电脑快照  {snapshot.TimeText}\n\n主机当前内容会先完整保护备份到 Recovery。", "保护当前并写回")) return;
        var restored = await RunJob("同步写回 · " + save.GameName, async (job, ct) =>
        {
            var (peer, current) = RequireDevice("restore.atomic.v1");
            DemandSyncContext(peer, current, context);
            var store = new SnapshotStore(settings.BackupDirectory);
            var result = await store.RestoreAsync(peer, current, save, snapshot.Path, Progress(job), ct,
                expectedFingerprint: row.Plan.DeviceFingerprint, operationId: Guid.NewGuid().ToString("N"), expectedManifest: snapshot.Manifest);
            return await CompleteRestoreAsync(result, async () =>
            {
                var inspection = await peer.QueryAsync<SaveDeviceInspection>("saves.inspect",
                    new { key = save.Key, titleId = save.TitleId, userId = save.UserId, environment = current.Environment }, ct);
                UpsertSyncState(SaveSync.RecordSync(save, current, inspection.Fingerprint, snapshot.Manifest.ContentHash, DateTimeOffset.UtcNow));
            });
        });
        if (restored.Warning == null)
        {
            try { await SaveSyncCheckAsync(); }
            catch (Exception error)
            {
                ClearSaveSyncResults(); restored = RestoreFollowupWarning(restored, error); Log(restored.Warning!);
            }
        }
        vm.Notify(restored.Warning ?? "主机存档已写回 · 恢复前备份保留在 Recovery", Path.GetDirectoryName(restored.RecoveryPath)!);
    }

    /// <summary>把其他系统环境（sysMMC/emuMMC）的备份迁移到当前系统；先在另一系统备份，重启进目标系统后执行恢复。</summary>
    private async Task MigrateToCurrentDeviceAsync()
    {
        var source = vm.SaveLibrary.SelectedSource ?? throw new IOException("请先在上方选择其他系统环境的存档来源");
        if (source.IsCurrentDevice) throw new IOException("请先切换到其他系统环境的存档来源，再执行迁移");
        var snapshot = vm.SaveLibrary.SelectedSnapshot ?? vm.SaveLibrary.Detail.History.FirstOrDefault()
            ?? throw new IOException("请选择要迁移的备份版本");
        var target = saves.FirstOrDefault(s => s.TitleId.Equals(source.Identity.TitleId, StringComparison.OrdinalIgnoreCase) &&
            s.UserId == source.Identity.UserId && s.Type == source.Identity.SaveType)
            ?? throw new IOException("当前系统还没有该游戏此用户的存档；请先在游戏里存一次档，再回到这里迁移");
        var (confirmedPeer, identity) = RequireDevice("restore.atomic.v1");
        if (!Dialogs.Confirm(this, "跨系统迁移存档",
            $"来源  {source.Identity.Environment} · {snapshot.TimeText}\n目标  当前系统 {identity.Environment} · {target.GameName} · {target.UserName}\n\n" +
            "当前系统的这份存档会先完整保护备份；恢复采用带指纹保护的原子写回。\n同游戏在另一系统的其余版本不会受影响。", "迁移到当前系统")) return;
        var restored = await RunJob("跨系统迁移 · " + target.GameName, async (job, ct) =>
        {
            var (peer, current) = RequireDevice("restore.atomic.v1");
            if (!ReferenceEquals(peer, confirmedPeer) || current.DeviceId != identity.DeviceId || current.Environment != identity.Environment)
                throw new IOException("主机连接或系统环境已变化，请重新确认迁移目标");
            var before = await peer.QueryAsync<SaveDeviceInspection>("saves.inspect",
                new { key = target.Key, titleId = target.TitleId, userId = target.UserId, environment = current.Environment }, ct);
            var store = new SnapshotStore(settings.BackupDirectory);
            var result = await store.RestoreAsync(peer, current, target, snapshot.Path, Progress(job), ct,
                expectedFingerprint: before.Fingerprint, operationId: Guid.NewGuid().ToString("N"), foreignSource: true, expectedManifest: snapshot.Manifest);
            return await CompleteRestoreAsync(result);
        });
        vm.Notify(restored.Warning ?? "跨系统迁移完成 · 来源系统的备份仍保留在电脑上，可反向迁移", Path.GetDirectoryName(restored.RecoveryPath)!);
    }
}
