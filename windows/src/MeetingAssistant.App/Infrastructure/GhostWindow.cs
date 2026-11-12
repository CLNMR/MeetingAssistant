using System.Windows;
using System.Windows.Threading;
using MeetingAssistant.Core.Scribe;

namespace MeetingAssistant.App.Infrastructure;

/// Ghost mode for the floating window (rule in <see cref="GhostMode"/>). A click-through window gets
/// no mouse events, so the cursor and Ctrl are polled. The window is made layered only while ghost
/// mode is on: WS_EX_LAYERED with a whole-window alpha for the fade, WS_EX_TRANSPARENT for click-through.
public sealed class GhostWindow
{
    readonly Window _w;
    readonly Func<bool> _enabled;
    readonly DispatcherTimer _poll, _fade;
    GhostMode.Look _look = new(1, false);
    bool _layered;
    double _opacity = 1, _fadeFrom = 1;
    DateTime _fadeStart;

    public GhostWindow(Window w, Func<bool> enabled)
    {
        _w = w;
        _enabled = enabled;
        _poll = new DispatcherTimer(DispatcherPriority.Background, w.Dispatcher) { Interval = GhostMode.PollInterval };
        _poll.Tick += (_, _) => Update();
        _fade = new DispatcherTimer(DispatcherPriority.Render, w.Dispatcher) { Interval = TimeSpan.FromMilliseconds(15) };
        _fade.Tick += (_, _) => FadeStep();
        w.SourceInitialized += (_, _) => { _poll.Start(); Update(); };
        w.Closed += (_, _) => { _poll.Stop(); _fade.Stop(); };
    }

    /// Re-check now (after the setting changed).
    public void Update()
    {
        var on = _enabled();
        var look = GhostMode.Compute(on, on && Native.IsCursorOver(_w), on && Native.IsCtrlDown());
        if (look == _look) return;
        _look = look;
        if (look.ClickThrough || look.Opacity < 1) SetLayered(true);
        if (look.ClickThrough) Native.AddExStyle(_w, Native.WS_EX_TRANSPARENT);
        else Native.RemoveExStyle(_w, Native.WS_EX_TRANSPARENT);
        _fadeFrom = _opacity;
        _fadeStart = DateTime.UtcNow;
        _fade.Start();
    }

    void FadeStep()
    {
        var t = Math.Min(1, (DateTime.UtcNow - _fadeStart) / GhostMode.Fade);
        _opacity = _fadeFrom + (_look.Opacity - _fadeFrom) * t;
        if (_layered) Native.SetLayeredOpacity(_w, _opacity);
        if (t < 1) return;
        _fade.Stop();
        // Back to a plain window when ghost mode no longer needs the layered style.
        if (!_look.ClickThrough && _look.Opacity >= 1) SetLayered(false);
    }

    void SetLayered(bool on)
    {
        if (on == _layered) return;
        _layered = on;
        if (on) { Native.AddExStyle(_w, Native.WS_EX_LAYERED); Native.SetLayeredOpacity(_w, _opacity); }
        else Native.RemoveExStyle(_w, Native.WS_EX_LAYERED);
    }
}
