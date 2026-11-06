using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SwitchDesk.App;
using SwitchDesk.Core;

namespace SwitchDesk.Tests;

internal static partial class Program
{
    private static async Task RunSdFileContextUiChecksAsync(string directory)
    {
        foreach (var mode in new[] { "environment", "reconnect", "rename-parent" })
        {
            var window = new MainWindow(Path.Combine(directory, "sd-context-" + mode), true)
            { ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -16000, Top = -16000 };
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            T Get<T>(string name) => (T)typeof(MainWindow).GetField(name, flags)!.GetValue(window)!;
            void Set(string name, object value) => typeof(MainWindow).GetField(name, flags)!.SetValue(window, value);
            object? Call(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, args);
            async Task Invoke(string name, params object?[] args) => await (Task)Call(name, args)!;
            window.Show();
            await using var peer = new Peer(); await using var replacement = new Peer();
            await using var client = await peer.ConnectAsync();
            await using var newClient = await replacement.ConnectAsync();
            try
            {
                Set("client", client); Set("device", Peer.Info);
                var vm = Get<ShellViewModel>("vm"); vm.Online = true;
                peer.SdFiles["/old/item.txt"] = [1, 2]; replacement.SdFiles["/old/item.txt"] = [3, 4];
                var path = (TextBox)window.FindName("RemotePath"); path.Text = "/old";
                await Invoke("RefreshDeviceAsync", CancellationToken.None);
                await Invoke("LoadFilesAsync", CancellationToken.None);
                var grid = (DataGrid)window.FindName("FilesGrid");
                grid.SelectedItem = grid.Items.Cast<RemoteEntry>().Single();
                await Invoke("ReviewUploadAsync", (object)new[] { Path.Combine(directory, "upload.txt") });
                var uploadContext = Get<object>("uploadContext");
                var dialogName = mode == "rename-parent" ? "重命名" : "永久删除 SD 文件";
                _ = window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    var dialog = Application.Current.Windows.OfType<Window>().Single(candidate => candidate.Owner == window && candidate.Title == dialogName);
                    if (mode == "environment") Call("ApplyConnectedDevice", Peer.Info with { Environment = "sysMMC" });
                    else if (mode == "reconnect") Set("client", newClient);
                    else { path.Text = "/typed-but-not-opened"; Walk<TextBox>(dialog).Single().Text = "renamed.txt"; }
                    dialog.DialogResult = true;
                }));
                if (mode == "rename-parent")
                {
                    await Invoke("RenameAsync");
                    Assert(peer.SdFiles.ContainsKey("/old/renamed.txt") && !peer.SdFiles.ContainsKey("/typed-but-not-opened/renamed.txt"), "rename moved the selected file into an unvisited typed path");
                }
                else
                {
                    await Throws<IOException>(() => Invoke("DeleteAsync"));
                    Assert(peer.SdFiles.ContainsKey("/old/item.txt") && replacement.SdFiles.ContainsKey("/old/item.txt"), "stale confirmation deleted a file after the connection changed");
                    try { Call("DemandFileContext", uploadContext); throw new Exception("stale upload accepted"); }
                    catch (TargetInvocationException error) when (error.InnerException is IOException) { }
                    if (mode == "environment")
                    {
                        Assert(grid.ItemsSource == null && vm.Storages.Length == 0 && Get<GameTitle[]>("titles").Length == 0 && Get<SaveInfo[]>("saves").Length == 0,
                            "new environment retained the old device's file or catalog data before refresh");
                        Assert(path.Text == "/" && ((Border)window.FindName("UploadOverlay")).Visibility == Visibility.Collapsed && Get<object?>("synced") == null,
                            "new environment retained old upload targets or synchronization time");
                    }
                }
            }
            finally { await Invoke("DisconnectAsync"); window.Close(); }
        }
    }
}
