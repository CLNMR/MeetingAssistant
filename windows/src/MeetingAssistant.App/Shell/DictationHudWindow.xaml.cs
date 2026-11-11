using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using MeetingAssistant.App.Infrastructure;
using MeetingAssistant.App.ViewModels;

namespace MeetingAssistant.App.Shell;

/// The dictation HUD overlay (spec 04 §1.7). <see cref="ShowOnPointerScreen"/> places it centred
/// horizontally, 70 px above the bottom of the work area of the monitor under the mouse pointer,
/// without activating it; it ignores mouse clicks.
public partial class DictationHudWindow : Window
{
    /// Panel size and distance from the bottom of the work area (DIPs).
    public const double PanelWidth = 620, PanelHeight = 200, BottomOffset = 70;

    readonly HudViewModel _vm;
    readonly Rectangle[] _bars;

    public DictationHudWindow(HudViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        _bars = [B0, B1, B2, B3, B4];
        SourceInitialized += (_, _) => Native.AddExStyle(this,
            Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOPMOST);
        vm.PropertyChanged += OnVmChanged;
    }

    void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HudViewModel.Level)) UpdateBars();
        if (e.PropertyName == nameof(HudViewModel.Phase))
        {
            if (_vm.Phase == HudPhase.Hidden) Hide(); else if (!IsVisible) ShowOnPointerScreen();
        }
    }

    void UpdateBars()
    {
        for (int i = 0; i < _bars.Length; i++)
        {
            bool on = HudViewModel.BarOn(i, _vm.Level);
            if (on) { _bars[i].Fill = (Brush)FindResource("RecordingBrush"); _bars[i].Opacity = 1; }
            else { _bars[i].ClearValue(Shape.FillProperty); _bars[i].ClearValue(OpacityProperty); }
        }
    }

    /// Shows the HUD on the monitor under the pointer without taking focus.
    public void ShowOnPointerScreen()
    {
        var (wa, scale) = Native.MonitorUnderCursor();
        int w = (int)Math.Round(PanelWidth * scale), h = (int)Math.Round(PanelHeight * scale);
        int x = (int)Math.Round(wa.Left + (wa.Width - w) / 2);
        int y = (int)Math.Round(wa.Bottom - BottomOffset * scale - h);
        if (!IsVisible) { Show(); }
        Native.PlacePhysical(this, x, y, w, h);
    }
}
