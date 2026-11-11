using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using MeetingAssistant.App.Infrastructure;

namespace MeetingAssistant.App.Shell;

/// The main window (spec 03 §1). Closing it hides it; the app keeps running in the notification area
/// (the Mac keeps running with its menu-bar item). Its bounds are remembered under `mainWindow.frame`.
public partial class MainWindow : Window
{
    readonly IUiState _ui;

    /// Set by the app when it quits, so closing really closes.
    public bool AllowClose { get; set; }

    public MainWindow(IUiState ui)
    {
        _ui = ui;
        InitializeComponent();
        RestoreFrame();
        StateChanged += (_, _) => UpdateMaximized();
        SourceInitialized += (_, _) =>
        {
            Themes.ThemeManager.ApplyTitleBar(this);
            // Windows 11: rounded corners for the custom-chrome window.
            int round = 2; // DWMWCP_ROUND
            _ = DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 33, ref round, sizeof(int));
        };
    }

    void UpdateMaximized()
    {
        // A maximised WindowChrome window extends past the screen by the resize border; pad it back in.
        var b = SystemParameters.WindowResizeBorderThickness;
        Root.Margin = WindowState == WindowState.Maximized
            ? new Thickness(b.Left + 4, b.Top + 4, b.Right + 4, b.Bottom + 4)
            : new Thickness(0);
        MaxButton.Content = FindResource(WindowState == WindowState.Maximized ? "Icon.Restore" : "Icon.Maximize");
    }

    void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Maximize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        SaveFrame();
        if (!AllowClose) { e.Cancel = true; Hide(); }
        base.OnClosing(e);
    }

    void SaveFrame()
    {
        var r = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (r.IsEmpty) return;
        var s = string.Join(",", new[] { r.Left, r.Top, r.Width, r.Height }.Select(v => v.ToString("0", CultureInfo.InvariantCulture)));
        if (WindowState == WindowState.Maximized) s += ",max";
        _ui.Set(UiKeys.MainFrame, s);
    }

    void RestoreFrame()
    {
        if (FrameParser.TryParse(_ui.Get<string?>(UiKeys.MainFrame, null), out var r, out var max) && FrameParser.IsOnScreen(r))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = r.Left; Top = r.Top; Width = Math.Max(MinWidth, r.Width); Height = Math.Max(MinHeight, r.Height);
            if (max) WindowState = WindowState.Maximized;
        }
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}

/// Parses "left,top,width,height[,max]" window frames stored in the UI state.
public static class FrameParser
{
    public static bool TryParse(string? s, out Rect rect, out bool maximized)
    {
        rect = Rect.Empty; maximized = false;
        if (string.IsNullOrEmpty(s)) return false;
        var p = s.Split(',');
        if (p.Length < 4) return false;
        var n = new double[4];
        for (int i = 0; i < 4; i++)
            if (!double.TryParse(p[i], NumberStyles.Float, CultureInfo.InvariantCulture, out n[i])) return false;
        if (n[2] <= 0 || n[3] <= 0) return false;
        rect = new Rect(n[0], n[1], n[2], n[3]);
        maximized = p.Length > 4 && p[4] == "max";
        return true;
    }

    /// True when the frame's top-left area is inside the virtual screen (monitors may have changed).
    public static bool IsOnScreen(Rect r)
    {
        var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                          SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        return vs.IntersectsWith(new Rect(r.Left, r.Top, Math.Min(r.Width, 100), 40));
    }
}
