using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace MeetingAssistant.App.Themes;

/// Follows the Windows app appearance (light/dark), like the Mac follows the system appearance
/// (spec 03 §12 "Dynamic colours switch on the system appearance"). Swaps the colour dictionary
/// (index 0 of the application's merged dictionaries) and the DWM title bar colour of windows.
public static class ThemeManager
{
    static readonly Uri LightUri = new("pack://application:,,,/MeetingAssistant;component/Themes/Colors.Light.xaml");
    static readonly Uri DarkUri = new("pack://application:,,,/MeetingAssistant;component/Themes/Colors.Dark.xaml");

    /// True while the dark palette is applied.
    public static bool IsDark { get; private set; }

    /// Raised after the palette changed.
    public static event Action? Changed;

    /// Applies the current Windows appearance and listens for changes.
    public static void Initialize()
    {
        Apply(ReadSystemIsDark());
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
                Application.Current?.Dispatcher.BeginInvoke(() => Apply(ReadSystemIsDark()));
        };
    }

    /// `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\AppsUseLightTheme` (0 = dark).
    public static bool ReadSystemIsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    /// Switches the palette and updates every open window's title bar.
    public static void Apply(bool dark)
    {
        var app = Application.Current;
        if (app is null) return;
        var dicts = app.Resources.MergedDictionaries;
        var colors = new ResourceDictionary { Source = dark ? DarkUri : LightUri };
        if (dicts.Count > 0) dicts[0] = colors; else dicts.Add(colors);
        IsDark = dark;
        foreach (Window w in app.Windows) ApplyTitleBar(w);
        Changed?.Invoke();
    }

    /// Dark or light native title bar (Windows 10 2004+ / 11), for windows with a system caption.
    public static void ApplyTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) { window.SourceInitialized += (_, _) => ApplyTitleBar(window); return; }
        int on = IsDark ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
