using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using SwitchDesk.App;
using SwitchDesk.Core;

namespace SwitchDesk.Tests;

internal static partial class Program
{
    private static async Task RunRestoreCompletionUiChecksAsync(string directory)
    {
        foreach (var mode in new[] { "readback-and-log", "sync-state", "refresh" })
        {
            var dataRoot = Path.Combine(directory, "restore-completion-" + mode);
            var window = new MainWindow(dataRoot, true)
            { ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -16000, Top = -16000 };
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            T Get<T>(string name) => (T)typeof(MainWindow).GetField(name, flags)!.GetValue(window)!;
            void Set(string name, object value) => typeof(MainWindow).GetField(name, flags)!.SetValue(window, value);
            async Task Invoke(string name, params object[] args) => await (Task)typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, args)!;
            window.Show();
            await using var peer = new Peer { FailPostRestoreInspectionAt = mode == "readback-and-log" ? 1 : mode == "refresh" ? 2 : 0 };
            var client = await peer.ConnectAsync();
            FileStream? lockedLog = null;
            try
            {
                Set("client", client); Set("device", Peer.Info);
                var vm = Get<ShellViewModel>("vm"); vm.Online = true;
                await Invoke("RefreshDeviceAsync", CancellationToken.None);
                var settings = Get<AppSettings>("settings");
                var store = new SnapshotStore(settings.BackupDirectory);
                await store.BackupAsync(client, Peer.Info, Peer.Save, null, default);
                peer.Files["main.dat"] = [9];
                await Invoke("SaveSyncCheckAsync");
                var row = vm.SaveLibrary.SyncRows.Single();
                Assert(vm.Commands["SaveSyncRowRestore"].CanExecute(row), "restore fixture did not produce a current conflict");
                peer.OnRestoreCommitted = () =>
                {
                    if (mode == "readback-and-log") lockedLog = File.Open(
                        Directory.GetFiles(Path.Combine(settings.BackupDirectory, "Recovery"), "*.restore.json", SearchOption.AllDirectories).Single(),
                        FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (mode == "sync-state") Directory.CreateDirectory(Path.Combine(dataRoot, "SaveSync", "save-sync.json"));
                };
                _ = window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    var dialog = Application.Current.Windows.OfType<Window>().Single(candidate => candidate.Owner == window && candidate.Title == "写回主机存档");
                    dialog.DialogResult = true;
                }));
                await Invoke("SaveSyncRowRestoreAsync", row);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Assert(peer.RestoreCommits == 1 && peer.Files["main.dat"].SequenceEqual(new byte[] { 1, 2, 3, 4, 5 }), "confirmed restore was lost");
                Assert(vm.Tasks.Items.Single(job => job.Name.StartsWith("同步写回", StringComparison.Ordinal)).State == "完成", "post-restore failure changed the completed write into failure");
                Assert(vm.Notice.Contains("已写回", StringComparison.Ordinal) && vm.Notice.Contains("无需重复写回", StringComparison.Ordinal), "post-restore failure hid the completed write");
                if (mode == "readback-and-log") Assert(vm.Notice.Contains("完成记录保存失败", StringComparison.Ordinal) && vm.Notice.Contains("后续复查", StringComparison.Ordinal), "followup failure discarded the earlier log warning");
                Assert(!vm.SaveLibrary.SyncChecked && vm.SaveLibrary.SyncRows.Count == 0 && !vm.Commands["SaveSyncRowRestore"].CanExecute(row), "a completed restore retained a reusable stale plan");
                try { await Invoke("SaveSyncRowRestoreAsync", row); throw new Exception("stale restore plan was accepted"); }
                catch (TargetInvocationException error) when (error.InnerException is IOException) { }
                catch (IOException) { }
                Assert(peer.RestoreCommits == 1, "the same stale plan wrote again");
            }
            finally
            {
                lockedLog?.Dispose();
                await Invoke("DisconnectAsync"); window.Close();
            }
        }
    }
}
