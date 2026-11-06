using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SwitchDesk.Core;

namespace SwitchDesk.App;
public static class Dialogs
{
    private static Window Shell(Window owner, string title, double width = 560) => new()
    { Owner = owner, Title = title, Style = (Style)Application.Current.FindResource(typeof(Window)), Width = width, SizeToContent = SizeToContent.Height, MaxHeight = Math.Max(400, SystemParameters.WorkArea.Height - 64), ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
    public static bool Confirm(Window owner, string title, string description, string action)
    {
        var dialog = Shell(owner, title); var body = new StackPanel { Margin = new(24) }; dialog.Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        body.Children.Add(new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeights.SemiBold });
        body.Children.Add(new TextBlock { Text = description, Margin = new(0, 20, 0, 24), TextWrapping = TextWrapping.Wrap });
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = new(0, 0, 12, 0) };
        var accept = new Button { Content = action, Style = (Style)Application.Current.FindResource("Primary") };
        accept.Click += (_, _) => dialog.DialogResult = true; row.Children.Add(cancel); row.Children.Add(accept); body.Children.Add(row);
        cancel.Focus(); return dialog.ShowDialog() == true;
    }
    public static string? Text(Window owner, string title, string label, string initial = "")
    {
        var dialog = Shell(owner, title, 450); var body = new StackPanel { Margin = new(24) }; dialog.Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        body.Children.Add(new TextBlock { Text = label, Margin = new(0, 0, 0, 12) }); var input = new TextBox { Text = initial }; body.Children.Add(input);
        var ok = new Button { Content = "确定", IsDefault = true, Margin = new(0, 18, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        ok.Click += (_, _) => dialog.DialogResult = true; body.Children.Add(ok); input.Focus();
        return dialog.ShowDialog() == true ? input.Text : null;
    }
    public static AppSettings? Settings(Window owner, AppSettings current)
    {
        var dialog = Shell(owner, "设置", 720); var body = new StackPanel { Margin = new(24) }; dialog.Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        TextBox PathRow(string title, string value, bool executable = false)
        {
            body.Children.Add(new TextBlock { Text = title, Margin = new(0, 12, 0, 8) }); var row = new DockPanel(); var input = new TextBox { Text = value };
            var browse = new Button { Content = "选择", Margin = new(10, 0, 0, 0) }; DockPanel.SetDock(browse, Dock.Right); row.Children.Add(browse); row.Children.Add(input); body.Children.Add(row);
            browse.Click += (_, _) =>
            {
                if (executable) { var pick = new OpenFileDialog { Filter = "SysDVR 官方客户端|*.exe" }; if (pick.ShowDialog(dialog) == true) input.Text = pick.FileName; }
                else { var pick = new OpenFolderDialog(); if (pick.ShowDialog(dialog) == true) input.Text = pick.FolderName; }
            }; return input;
        }
        var backup = PathRow("存档备份目录", current.BackupDirectory); var library = PathRow("电脑游戏库目录", current.LibraryDirectory);
        var export = PathRow("游戏导出目录", current.ExportDirectory);
        body.Children.Add(new TextBlock { Text = "游戏投屏（SysDVR）", Margin = new(0, 18, 0, 6), FontWeight = FontWeights.SemiBold });
        body.Children.Add(new TextBlock { Text = "完整运行包已内置 SysDVR 6.3 Windows 客户端和运行时，默认自动识别，无需选择文件。", TextWrapping = TextWrapping.Wrap });
        var modeRow = new DockPanel { Margin = new(0, 10, 0, 0) };
        var mode = new ComboBox { Width = 190, SelectedValuePath = "Tag" };
        mode.Items.Add(new ComboBoxItem { Content = "USB 数据线", Tag = StreamingOptions.UsbMode });
        mode.Items.Add(new ComboBoxItem { Content = "网络 Bridge（无线）", Tag = StreamingOptions.BridgeMode });
        mode.SelectedValue = current.SysDvrMode == StreamingOptions.BridgeMode ? StreamingOptions.BridgeMode : StreamingOptions.UsbMode;
        var address = new TextBox { Text = current.SysDvrAddress, ToolTip = "Bridge 模式主机 IP；留空自动发现" };
        modeRow.Children.Add(address); modeRow.Children.Add(mode); body.Children.Add(modeRow);
        body.Children.Add(new TextBlock { Text = "USB 模式投屏会释放管理连接；Bridge 模式通过局域网无线投屏，管理连接保持可用。", Style = (Style)Application.Current.FindResource("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 0) });
        var advancedBody = new StackPanel { Margin = new(0, 10, 0, 0) };
        advancedBody.Children.Add(new TextBlock { Text = "留空使用内置客户端。仅在需要其他官方构建时设置。", Margin = new(0, 0, 0, 8) });
        var player = new TextBox { Text = current.SysDvrClient, ToolTip = "留空自动使用随软件附带的 SysDVR 客户端" };
        var playerRow = new DockPanel(); var choosePlayer = new Button { Content = "选择", Margin = new(10, 0, 0, 0) }; DockPanel.SetDock(choosePlayer, Dock.Right);
        choosePlayer.Click += (_, _) => { var pick = new OpenFileDialog { Filter = "SysDVR 官方客户端|SysDVR-Client.exe|Windows 程序|*.exe" }; if (pick.ShowDialog(dialog) == true) player.Text = pick.FileName; };
        playerRow.Children.Add(choosePlayer); playerRow.Children.Add(player); advancedBody.Children.Add(playerRow);
        body.Children.Add(new Expander { Header = "高级：使用其他官方客户端", Content = advancedBody, Margin = new(0, 10, 0, 0) });
        body.Children.Add(new TextBlock { Text = "局域网（FTP / Homebrew）", Margin = new(0, 18, 0, 6), FontWeight = FontWeights.SemiBold });
        var ftpGrid = new Grid { Margin = new(0, 4, 0, 0) };
        for (var column = 0; column < 4; column++) ftpGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        TextBox Cell(int column, string text, string tip)
        { var input = new TextBox { Text = text, ToolTip = tip, Margin = new(0, 0, 8, 0) }; Grid.SetColumn(input, column); ftpGrid.Children.Add(input); return input; }
        var ftpHost = Cell(0, current.FtpHost, "ftpd 主机地址"); var ftpPort = Cell(1, current.FtpPort.ToString(), "端口（默认 21）");
        var ftpUser = Cell(2, current.FtpUser, "用户名（可选）"); var ftpPassword = new PasswordBox { Password = current.FtpPassword, ToolTip = "仅本次会话使用，不写入配置", Margin = new(0, 0, 8, 0) };
        Grid.SetColumn(ftpPassword, 3); ftpGrid.Children.Add(ftpPassword); body.Children.Add(ftpGrid);
        body.Children.Add(new TextBlock { Text = "FTP 密码仅本次会话使用，不会写入配置文件。", Style = (Style)Application.Current.FindResource("Muted"), Margin = new(0, 6, 0, 0) });
        var homebrew = new TextBox { Text = current.HomebrewIndex, ToolTip = "社区商店提供的 JSON 索引 URL；留空则首次使用时询问" };
        body.Children.Add(new TextBlock { Text = "Homebrew 索引地址（JSON，免费开源内容）", Margin = new(0, 10, 0, 6) }); body.Children.Add(homebrew);
        body.Children.Add(new TextBlock { Text = "自动备份保留版本数（Recovery 恢复前快照独立保留）", Margin = new(0, 14, 0, 8) });
        var retention = new TextBox { Text = current.Retention.ToString() }; body.Children.Add(retention);
        var links = new Button { Content = "打开 Switch 端首次配置说明 ↗", Margin = new(0, 16, 0, 0) };
        links.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/exelix11/SysDVR/releases/tag/v6.3") { UseShellExecute = true }); body.Children.Add(links);
        var ok = new Button { Content = "保存", Margin = new(0, 20, 0, 0), IsDefault = true };
        ok.Click += (_, _) =>
        {
            if (int.TryParse(retention.Text, out var count) && count is >= 1 and <= 1000) dialog.DialogResult = true; else MessageBox.Show(dialog, "保留版本数应为 1–1000");
        }; body.Children.Add(ok);
        return dialog.ShowDialog() == true ? current with
        {
            BackupDirectory = backup.Text, LibraryDirectory = library.Text, ExportDirectory = export.Text,
            SysDvrClient = player.Text, Retention = int.Parse(retention.Text),
            SysDvrMode = mode.SelectedValue as string ?? StreamingOptions.UsbMode,
            SysDvrAddress = address.Text.Trim(), FtpHost = ftpHost.Text.Trim(),
            FtpPort = int.TryParse(ftpPort.Text, out var ftpPortValue) ? ftpPortValue : 21,
            FtpUser = ftpUser.Text.Trim(), FtpPassword = ftpPassword.Password, HomebrewIndex = homebrew.Text.Trim()
        } : null;
    }

    public static Snapshot? SnapshotPick(Window owner, string title, Snapshot[] candidates, Snapshot current)
    {
        var dialog = Shell(owner, title, 520); var body = new StackPanel { Margin = new(24) }; dialog.Content = body;
        body.Children.Add(new TextBlock { Text = $"当前版本 {current.TimeText}\n选择另一份同存档快照进行对比：", Margin = new(0, 0, 0, 12), TextWrapping = TextWrapping.Wrap });
        var list = new ListBox { MaxHeight = 320 };
        foreach (var candidate in candidates) list.Items.Add(candidate);
        body.Children.Add(list);
        var ok = new Button { Content = "对比", Style = (Style)Application.Current.FindResource("Primary"), IsDefault = true, Margin = new(0, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        ok.Click += (_, _) => dialog.DialogResult = true; body.Children.Add(ok);
        list.SelectedIndex = 0;
        return dialog.ShowDialog() == true && list.SelectedItem is Snapshot picked ? picked : null;
    }

    public static void DiffReport(Window owner, Snapshot older, SnapshotDiff diff)
    {
        var dialog = Shell(owner, "快照对比", 620); var body = new StackPanel { Margin = new(24) }; dialog.Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        body.Children.Add(new TextBlock { Text = diff.Current.Manifest.GameName, FontSize = 20, FontWeight = FontWeights.SemiBold });
        body.Children.Add(new TextBlock { Text = $"旧版本  {older.TimeText}\n新版本  {diff.Current.TimeText}\n\n{diff.Summary}", Margin = new(0, 12, 0, 16), TextWrapping = TextWrapping.Wrap });
        foreach (var line in diff.Details()) body.Children.Add(new TextBlock { Text = line, FontFamily = new System.Windows.Media.FontFamily("Consolas, Microsoft YaHei UI"), FontSize = 12, Margin = new(0, 2, 0, 2), TextWrapping = TextWrapping.Wrap });
        var close = new Button { Content = "关闭", IsDefault = true, IsCancel = true, Margin = new(0, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => dialog.Close(); body.Children.Add(close);
        dialog.ShowDialog();
    }

    public static void DuplicateReport(Window owner, IReadOnlyList<LibraryDatabase.DuplicateGroup> groups)
    {
        var dialog = Shell(owner, "重复文件", 640); var body = new StackPanel { Margin = new(24) }; dialog.Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        body.Children.Add(new TextBlock { Text = $"发现 {groups.Count} 组重复；文件不会被自动删除，请自行确认后处理。", Margin = new(0, 0, 0, 16), TextWrapping = TextWrapping.Wrap });
        foreach (var group in groups)
        {
            body.Children.Add(new TextBlock { Text = group.Reason + $"（{group.Items.Length} 个）", FontWeight = FontWeights.SemiBold, Margin = new(0, 10, 0, 6) });
            foreach (var item in group.Items) body.Children.Add(new TextBlock { Text = item.Path + "  ·  " + item.SizeText + "  ·  " + item.Status, FontSize = 12, Foreground = System.Windows.Media.Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Margin = new(12, 0, 0, 2) });
        }
        var close = new Button { Content = "关闭", IsDefault = true, IsCancel = true, Margin = new(0, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => dialog.Close(); body.Children.Add(close);
        dialog.ShowDialog();
    }
}
