using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;

namespace SwitchDesk.App;
public sealed class Visible : IValueConverter
{
    public object Convert(object value, Type type, object parameter, CultureInfo culture) => (parameter is string match ? match == "not" ? value is false : value?.ToString() == match : value is true) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type type, object parameter, CultureInfo culture) => Binding.DoNothing;
}
public static class ThemeManager
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);
    public static void ApplyCaption(Window window)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var enabled = Dark && !SystemParameters.HighContrast ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
    }
    public static string Preference { get; private set; } = "System";
    public static bool Dark { get; private set; }
    private static bool listening;
    public static void Apply(string preference)
    {
        Preference = preference;
        if (!listening) { SystemEvents.UserPreferenceChanged += (_, _) => Application.Current?.Dispatcher.BeginInvoke(() => Apply(Preference)); listening = true; }
        var setting = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1);
        Dark = preference == "Dark" || preference == "System" && setting is int n && n == 0;
        var colors = Dark ? new[] { "#101722", "#1A2433", "#202D3E", "#2B3B52", "#F0F4FC", "#AFBCD0", "#34445A", "#9BB8FF", "#263D63", "#151F2D", "#FFB4AB" }
            : new[] { "#F6F8FC", "#FFFFFF", "#EDF1F7", "#E3EBF8", "#202D40", "#5E6E86", "#DCE3EE", "#315FDB", "#E8EEFC", "#EDF2F9", "#B64141" };
        string[] keys = ["Canvas", "Surface", "Subtle", "Hover", "Text", "Secondary", "Line", "Accent", "Selected", "Rail", "Error"];
        for (var i = 0; i < keys.Length; i++) Application.Current.Resources[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
        Application.Current.Resources["OnAccent"] = new SolidColorBrush(Dark ? Color.FromRgb(17, 43, 87) : Colors.White);
        Application.Current.Resources["AccentHover"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(Dark ? "#A8C0FF" : "#1E50BE"));
        Application.Current.Resources["Success"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(Dark ? "#70D5B7" : "#318A76"));
        if (SystemParameters.HighContrast)
        {
            Application.Current.Resources["Text"] = SystemColors.WindowTextBrush;
            Application.Current.Resources["Secondary"] = SystemColors.WindowTextBrush;
            Application.Current.Resources["Canvas"] = SystemColors.WindowBrush;
            Application.Current.Resources["Surface"] = SystemColors.WindowBrush;
            Application.Current.Resources["Accent"] = SystemColors.HighlightBrush;
            Application.Current.Resources["Selected"] = SystemColors.ControlBrush;
            Application.Current.Resources["Hover"] = SystemColors.ControlBrush;
            Application.Current.Resources["Subtle"] = SystemColors.ControlBrush;
            Application.Current.Resources["Rail"] = SystemColors.WindowBrush;
            Application.Current.Resources["Line"] = SystemColors.WindowTextBrush;
            Application.Current.Resources["OnAccent"] = SystemColors.HighlightTextBrush;
            Application.Current.Resources["AccentHover"] = SystemColors.HighlightBrush;
            Application.Current.Resources["Success"] = SystemColors.WindowTextBrush;
        }
        foreach (Window window in Application.Current.Windows) ApplyCaption(window);
        Application.Current.Resources["MotionDuration"] = new Duration(TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? 140 : 0));
    }
}
