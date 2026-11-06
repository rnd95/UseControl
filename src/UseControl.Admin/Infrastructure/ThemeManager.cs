using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using UseControl.Admin.ViewModels.Infrastructure;

namespace UseControl.Admin.Infrastructure;

/// <summary>
/// Switches the colours of the panel at run time: the first merged dictionary of the application
/// (Light.xaml or Dark.xaml) is replaced, and everything that uses DynamicResource follows.
/// "System" follows the Windows setting for apps, also when it changes while the panel is open.
/// </summary>
public static partial class ThemeManager
{
    private static bool _listening;

    public static PanelTheme Current { get; private set; } = PanelTheme.System;

    public static bool IsDark { get; private set; }

    /// <summary>Raised after the colours changed (bindings that picked a brush once can refresh).</summary>
    public static event EventHandler? Changed;

    public static void Apply(PanelTheme theme)
    {
        Current = theme;
        var dark = theme == PanelTheme.Dark || (theme == PanelTheme.System && WindowsAppsUseDark());
        if (!_listening && theme == PanelTheme.System)
        {
            _listening = true;
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, _) =>
            {
                if (Current == PanelTheme.System && WindowsAppsUseDark() != IsDark) Application.Current?.Dispatcher.Invoke(() => Apply(PanelTheme.System));
            };
        }
        var changed = dark != IsDark || Application.Current.Resources.MergedDictionaries[0].Source is null;
        IsDark = dark;
        if (!changed) return;

        Application.Current.Resources.MergedDictionaries[0] = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Themes/{(dark ? "Dark" : "Light")}.xaml", UriKind.Absolute),
        };
        foreach (Window w in Application.Current.Windows) SetTitleBar(w);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>A dark or light title bar to match (Windows 10 2004 and later; ignored elsewhere).</summary>
    public static void SetTitleBar(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var on = IsDark ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, 20, ref on, sizeof(int));   // DWMWA_USE_IMMERSIVE_DARK_MODE
    }

    private static bool WindowsAppsUseDark()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
