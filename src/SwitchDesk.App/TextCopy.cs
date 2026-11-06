using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using SwitchDesk.Core;

namespace SwitchDesk.App;

/// <summary>Copies the full displayed text without changing text layout or editor shortcuts.</summary>
public static class TextCopy
{
    private static int initialized;

    public static void Initialize()
    {
        if (Interlocked.Exchange(ref initialized, 1) != 0) return;
        EventManager.RegisterClassHandler(typeof(Window), Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(OnMouseUp));
        EventManager.RegisterClassHandler(typeof(Window), Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnKeyDown));
    }

    public static bool TryWrite(string? text, Action<string> writer, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(text)) return false;
        try { writer(text); return true; }
        catch (ExternalException) { error = "剪贴板正被其他程序使用，请稍后再复制。"; return false; }
    }

    public static string? TextAt(DependencyObject? source)
    {
        if (Ancestors(source).Any(IsEditor)) return null;
        return Ancestors(source).OfType<TextBlock>().FirstOrDefault()?.Text;
    }

    public static string? KeyboardText(DependencyObject? source)
    {
        var ancestors = Ancestors(source).ToArray();
        // These controls already implement selection-aware copy commands.
        if (ancestors.Any(x => IsEditor(x) || x is DataGrid)) return null;
        var list = ancestors.OfType<ListBox>().FirstOrDefault();
        if (list == null) return source is TextBlock text ? text.Text : null;
        var rows = list.SelectedItems.Cast<object>().Select(item => ItemText(list, item)).Where(value => value.Length > 0);
        var value = string.Join(Environment.NewLine, rows);
        return value.Length == 0 ? null : value;
    }

    private static void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled || e.ChangedButton != MouseButton.Right || e.OriginalSource is not DependencyObject source) return;
        var text = TextAt(source);
        if (string.IsNullOrWhiteSpace(text)) return;
        var target = Ancestors(source).OfType<TextBlock>().First();
        // Preserve any menus supplied by the control or its owner.
        if (Ancestors(target).OfType<FrameworkElement>().Any(element => element.ContextMenu != null)) return;
        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.MousePoint };
        menu.SetResourceReference(Control.BackgroundProperty, "Surface");
        menu.SetResourceReference(Control.ForegroundProperty, "Text");
        menu.SetResourceReference(Control.BorderBrushProperty, "Line");
        var copy = new MenuItem { Header = "复制文本" };
        copy.Click += (_, _) => CopyOrExplain(target.Text, sender as Window);
        menu.Items.Add(copy); menu.IsOpen = true; e.Handled = true;
    }

    private static void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key != Key.C || Keyboard.Modifiers != ModifierKeys.Control) return;
        var text = KeyboardText(Keyboard.FocusedElement as DependencyObject);
        if (string.IsNullOrWhiteSpace(text)) return;
        CopyOrExplain(text, sender as Window); e.Handled = true;
    }

    private static void CopyOrExplain(string text, Window? owner)
    {
        if (!TryWrite(text, Clipboard.SetText, out var error) && error.Length > 0)
        {
            if (owner == null) MessageBox.Show(error, "复制文本");
            else MessageBox.Show(owner, error, "复制文本");
        }
    }

    private static bool IsEditor(DependencyObject source) => source is TextBoxBase or PasswordBox;

    private static IEnumerable<DependencyObject> Ancestors(DependencyObject? source)
    {
        while (source != null)
        {
            yield return source;
            source = source switch
            {
                Visual visual => VisualTreeHelper.GetParent(visual),
                FrameworkContentElement content => content.Parent,
                _ => LogicalTreeHelper.GetParent(source)
            };
        }
    }

    private static string ItemText(ListBox list, object item)
    {
        if (item is string value) return value;
        var container = list.ItemContainerGenerator.ContainerFromItem(item) as DependencyObject;
        if (container != null) return VisualText(container);
        // A virtualized selected row may not have a live container. Use its display
        // template without attaching it to a window or firing Loaded handlers.
        if (list.ItemTemplate?.LoadContent() is FrameworkElement template)
        {
            template.DataContext = item; template.Measure(new Size(10000, double.PositiveInfinity));
            return VisualText(template);
        }
        if (list.DisplayMemberPath.Length > 0)
            return item.GetType().GetProperty(list.DisplayMemberPath)?.GetValue(item) as string ?? "";
        return "";
    }

    private static string VisualText(DependencyObject element)
    {
        var values = new List<string>();
        void Visit(DependencyObject current)
        {
            if (current is UIElement { Visibility: not Visibility.Visible }) return;
            if (current is TextBlock text)
            {
                text.GetBindingExpression(TextBlock.TextProperty)?.UpdateTarget();
                if (text.FontFamily.Source != "Segoe MDL2 Assets" && !string.IsNullOrWhiteSpace(text.Text)) values.Add(text.Text);
                return;
            }
            if (current is not Visual) return;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(current); i++) Visit(VisualTreeHelper.GetChild(current, i));
        }
        Visit(element); return string.Join(Environment.NewLine, values);
    }
}

public static class DiagnosticText
{
    public static string Build(DeviceInfo? device, string connection, bool online, IEnumerable<string> logs, string? installError)
    {
        var result = new StringBuilder();
        result.AppendLine("电脑端：" + BuildInfo.ClientId);
        result.AppendLine("Windows：" + Environment.OSVersion);
        result.AppendLine("连接状态：" + connection);
        result.AppendLine("资料来源：" + (online ? "当前连接" : "离线记录"));
        result.AppendLine("Switch 配套：" + (device?.Companion ?? "未连接"));
        if (device != null)
        {
            result.AppendLine("主机型号：" + device.Model);
            result.AppendLine("系统版本：" + device.Firmware);
            result.AppendLine("Atmosphère：" + device.Atmosphere);
            result.AppendLine("系统环境：" + device.Environment);
            result.AppendLine("设备标识：" + device.DeviceId);
            result.AppendLine("USB：" + device.UsbSpeed);
            result.AppendLine("运行模式：" + (device.ApplicationMode ? "完整应用" : "相册模式"));
            result.AppendLine("配套能力：" + string.Join(", ", device.Capabilities));
        }
        if (!string.IsNullOrWhiteSpace(installError)) result.AppendLine().AppendLine("当前安装提示：").AppendLine(installError);
        result.AppendLine().AppendLine("最近日志：");
        foreach (var line in logs.TakeLast(100)) result.AppendLine(line);
        return result.ToString();
    }
}
