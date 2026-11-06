using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SwitchDesk.App;
using SwitchDesk.Core;

namespace SwitchDesk.Tests;

internal static partial class Program
{
    private static async Task ResourceSearchUiTestsAsync(MainWindow window)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var vm = (ShellViewModel)typeof(MainWindow).GetField("vm", flags)!.GetValue(window)!;
        var settingsField = typeof(MainWindow).GetField("settings", flags)!;
        var settings = (AppSettings)settingsField.GetValue(window)!;
        var page = (ResourcesPage)window.FindName("ResourcesPage");
        var options = (Expander)page.FindName("ResourceOptions");
        var results = (ListBox)page.FindName("ResourceResults");
        var priorSource = vm.Resources.SelectedSource; var priorQuery = vm.Resources.SearchText;
        var priorType = vm.Resources.SelectedType; var priorStatus = vm.Resources.Status;
        var priorResults = vm.Resources.Results.ToArray(); var width = window.Width; var height = window.Height;
        var failures = new List<string>();
        void Check(bool value, string message) { if (!value) failures.Add(message); }
        async Task Idle() { CommandManager.InvalidateRequerySuggested(); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout(); }
        async Task Search() => await (Task)typeof(MainWindow).GetMethod("SearchResourcesAsync", flags)!.Invoke(window, null)!;
        bool Unclipped(FrameworkElement element)
        {
            if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0) return false;
            for (var parent = VisualTreeHelper.GetParent(element); parent != null; parent = VisualTreeHelper.GetParent(parent))
            {
                if (parent is FrameworkElement frame)
                {
                    var viewport = new Rect(frame.RenderSize); viewport.Inflate(.5, .5);
                    if (!viewport.Contains(element.TransformToAncestor(frame).TransformBounds(new Rect(element.RenderSize)))) return false;
                }
                if (ReferenceEquals(parent, page)) return true;
            }
            return false;
        }

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var address = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/index.json";
        var serve = Task.Run(async () =>
        {
            for (var request = 0; request < 2; request++)
            {
                using var socket = await listener.AcceptTcpClientAsync(stop.Token);
                await using var stream = socket.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(stop.Token))) { }
                if (request == 0) { started.SetResult(); await release.Task.WaitAsync(stop.Token); }
                else
                {
                    var json = "[{\"name\":\"UI fixture\",\"url\":\"https://fixture.invalid/tool.nro\"}]";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {json.Length}\r\nConnection: close\r\n\r\n{json}"), stop.Token);
                }
            }
        }, stop.Token);
        try
        {
            window.Width = 1050; window.Height = 640; await Idle(); options.IsExpanded = false;
            var longStatus = string.Join(" ", Enumerable.Range(0, 120).Select(i => $"来源{i}无法读取：请检查文件夹位置及访问权限。"));
            vm.Resources.Status = longStatus; await Idle();
            var download = Walk<Button>(page).Single(b => b.Command == vm.Commands["ResourceDownload"]);
            var status = Walk<TextBlock>(page).Single(t => BindingOperations.GetBinding(t, TextBlock.TextProperty)?.Path.Path == "Resources.Status");
            Check(results.ActualHeight >= 96 && Unclipped(results) && Unclipped(download), "long source errors collapse the result viewport or clip its action footer");
            Check(TextCopy.TextAt(status) == longStatus && Equals(status.ToolTip, longStatus), "truncated source status loses its full readable/copyable error");

            settingsField.SetValue(window, settings with { HomebrewIndex = address });
            vm.Resources.SelectedSource = vm.Resources.Sources.Single(s => s.Id == "homebrew");
            vm.Resources.SearchText = ""; vm.Resources.SelectedType = "全部类型";
            var search = Search(); await started.Task.WaitAsync(TimeSpan.FromSeconds(3)); await Idle();
            var cancel = Walk<Button>(page).FirstOrDefault(b => b.Command == vm.Commands["ResourceCancelSearch"]);
            Check(vm.Resources.IsBusy && !options.IsExpanded && cancel is { IsEnabled: true } && Unclipped(cancel),
                "pending Homebrew search has no visible cancellation action with extra options collapsed");
            vm.Commands["ResourceCancelSearch"].Execute(null);
            await search.WaitAsync(TimeSpan.FromSeconds(3)); await Idle();
            Check(!vm.Resources.IsBusy && vm.Resources.Status.Contains("搜索已取消", StringComparison.Ordinal) && vm.Commands["ResourceSearch"].CanExecute(null),
                "cancelling a pending HTTP search leaves the page busy or prevents retry");
            release.SetResult(); await Search().WaitAsync(TimeSpan.FromSeconds(3)); await serve;
            Check(!vm.Resources.IsBusy && vm.Resources.Results.Count == 1 && vm.Resources.Status.Contains("已读取 1 项", StringComparison.Ordinal),
                "cancelled Homebrew search cannot retry successfully");
        }
        finally
        {
            stop.Cancel(); release.TrySetResult(); listener.Stop();
            try { await serve; } catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException) { }
            settingsField.SetValue(window, settings);
            vm.Resources.SelectedSource = priorSource; vm.Resources.SearchText = priorQuery; vm.Resources.SelectedType = priorType;
            vm.Resources.SetResults(priorResults); vm.Resources.Status = priorStatus;
            window.Width = width; window.Height = height; await Idle();
        }
        Assert(failures.Count == 0, string.Join("; ", failures));
    }
}
