using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace SwitchDesk.App;

public partial class MainWindow
{
    private async Task CheckUiPolishAsync(string output, Action<bool, string> check)
    {
        async Task Idle() { await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); UpdateLayout(); }
        void ClearSearch(TextBox box)
        {
            box.ApplyTemplate();
            var button = (Button)box.Template.FindName("ClearSearch", box);
            check(button.Visibility == Visibility.Visible, "nonempty search exposes a clear action");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(box.Text.Length == 0 && button.Visibility == Visibility.Collapsed, "clear action empties search and restores its hint");
        }
        Navigate("library");
        var selected = vm.Library.Selected;
        vm.Library.Source = "主机"; vm.Library.Search = "星海"; await Idle();
        ClearSearch(SearchBox);
        check(vm.Library.Search == "" && vm.Library.Source == "主机" && vm.Library.Selected == selected,
            "clear library search preserves the selected game and source filter");
        vm.Library.Source = "全部";
        Navigate("saves"); vm.SaveLibrary.SelectedGame = null; vm.SaveLibrary.Search = "星海"; await Idle();
        ClearSearch((TextBox)SavesPage.FindName("SaveSearchBox"));
        check(vm.SaveLibrary.Search == "", "clear save search updates the bound query");
        Navigate("resources"); vm.Resources.SearchText = "测试查询"; await Idle();
        ClearSearch((TextBox)ResourcesPage.FindName("ResourceSearchBox"));
        check(vm.Resources.SearchText == "", "clear resource search updates the bound query without requesting a download");

        var viewer = new ScrollViewer
        {
            Width = 300, Height = 120, Content = new Border { Width = 1500, Height = 800 },
            HorizontalScrollBarVisibility = ScrollBarVisibility.Visible, VerticalScrollBarVisibility = ScrollBarVisibility.Visible
        };
        viewer.Measure(new Size(300, 120)); viewer.Arrange(new Rect(0, 0, 300, 120)); viewer.UpdateLayout();
        viewer.ScrollToHorizontalOffset(400); viewer.ScrollToVerticalOffset(100); viewer.UpdateLayout();
        var horizontal = Descendants<ScrollBar>(viewer).Single(x => x.Orientation == Orientation.Horizontal);
        var track = (Track)horizontal.Template.FindName("PART_Track", horizontal);
        var before = viewer.HorizontalOffset; var top = viewer.VerticalOffset;
        ((RoutedCommand)track.IncreaseRepeatButton.Command).Execute(null, track.IncreaseRepeatButton); viewer.UpdateLayout();
        check(viewer.HorizontalOffset > before && viewer.VerticalOffset == top, "horizontal track paging scrolls right without moving vertically");
        ((RoutedCommand)track.DecreaseRepeatButton.Command).Execute(null, track.DecreaseRepeatButton); viewer.UpdateLayout();
        check(viewer.HorizontalOffset < before + viewer.ViewportWidth && track.ValueFromDistance(20, 0) > 0,
            "horizontal track paging and thumb movement follow the pointer direction");

        var preference = ThemeManager.Preference;
        var probe = new MainWindow(Path.Combine(output, "theme-save-rollback"), true);
        try
        {
            probe.ThemeChoice.SelectedValue = "Light";
            var path = Path.Combine(probe.settingsStore.Root, "settings.json");
            var original = File.ReadAllText(path);
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                probe.ThemeChoice.SelectedValue = "Dark";
                check(probe.settings.Theme == "Light" && Equals(probe.ThemeChoice.SelectedValue, "Light") && ThemeManager.Preference == "Light",
                    "failed theme persistence preserves the applied theme and selected preference");
                probe.SetView(true);
                check(!probe.settings.ListView && !probe.vm.Library.ListView, "failed view persistence preserves the current gallery mode");
                var priorSettings = probe.settings;
                var rejected = false;
                try { probe.SaveSettings(probe.settings with { LibraryDirectory = Path.Combine(output, "unsaved-library") }); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { rejected = true; }
                check(rejected && probe.settings == priorSettings, "failed directory persistence preserves the current in-memory settings");
                probe.device = device; probe.vm.Online = true;
                probe.vm.Saves.SetData(saves, []); probe.vm.Saves.SetContext(saves[0].TitleId, device);
                probe.vm.Saves.AutoBackup = true;
                check(!probe.vm.Saves.AutoBackup && probe.settings.AutoBackupKeys.Length == 0,
                    "failed automatic-backup persistence restores the toggle without scheduling a backup");
                probe.vm.Online = false;
                check(File.ReadAllText(path) == original && !Directory.EnumerateFiles(probe.settingsStore.Root, "*.partial").Any(),
                    "failed theme persistence preserves the settings file and removes temporary files");
            }
            probe.ThemeChoice.SelectedValue = "Dark";
            check(probe.settingsStore.Load().Theme == "Dark" && ThemeManager.Preference == "Dark",
                "theme persistence can be retried after the file lock is released");
        }
        finally { probe.Close(); await Idle(); ThemeManager.Apply(preference); }
        Navigate("library");
    }
}
