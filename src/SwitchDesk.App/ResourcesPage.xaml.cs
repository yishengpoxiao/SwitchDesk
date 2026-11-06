using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SwitchDesk.App;

public partial class ResourcesPage : UserControl
{
    public ResourcesPage() => InitializeComponent();
    private bool? compact;

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Height <= 0) return;
        var next = e.NewSize.Height < 500;
        if (compact != next) { compact = next; ResourceOptions.IsExpanded = !next; }
        // Keep search and the action footer outside this scroller. Even when the
        // user expands extra options in a short window, results retain space.
        ResourceOptionsScroller.MaxHeight = Math.Clamp(e.NewSize.Height - 390, 48, 155);
    }

    public void FocusSearch()
    {
        ResourceSearchBox.Focus();
        ResourceSearchBox.SelectAll();
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not ShellViewModel shell ||
            !shell.Commands.TryGetValue("ResourceSearch", out var command) || !command.CanExecute(null)) return;
        command.Execute(null);
        e.Handled = true;
    }
}
