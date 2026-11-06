using System.Windows.Controls;
using System.Windows.Input;

namespace SwitchDesk.App;

public partial class ResourcesPage : UserControl
{
    public ResourcesPage() => InitializeComponent();

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
