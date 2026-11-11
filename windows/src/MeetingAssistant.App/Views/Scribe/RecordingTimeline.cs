using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MeetingAssistant.App.ViewModels;
using MeetingAssistant.Core.Storage;

namespace MeetingAssistant.App.Views;

/// Overview of the whole recording (spec 01 "Pauses" UI, 22 px): speech blocks, pauses in amber
/// (stronger when they suggest a split, length label when wider than 44 px) and the playhead.
/// Click or drag to jump. Bound to a <see cref="SessionDetailViewModel"/> DataContext.
public sealed class RecordingTimeline : FrameworkElement
{
    SessionDetailViewModel? _vm;

    public RecordingTimeline()
    {
        Height = 22;
        Cursor = Cursors.Hand;
        ToolTip = "Speech (blue) and long pauses (amber). Click to jump.";
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.TimelineChanged -= InvalidateVisual;
            _vm = DataContext as SessionDetailViewModel;
            if (_vm is not null) _vm.TimelineChanged += InvalidateVisual;
            InvalidateVisual();
        };
    }

    Brush Res(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;

    protected override void OnRender(DrawingContext dc)
    {
        if (_vm is null) return;
        double w = ActualWidth, h = ActualHeight;
        var duration = Math.Max(_vm.Info.DurationSec, 1);
        double scale = w / duration;
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h), 4, 4));
        var muted = Res("MutedBrush").Clone(); muted.Opacity = 0.55;
        dc.DrawRectangle(muted, null, new Rect(0, 0, w, h));
        var warning = Res("WarningBrush");
        foreach (var p in _vm.PauseList)
        {
            var pw = Math.Max(2, p.Duration * scale);
            var fill = warning.Clone(); fill.Opacity = p.SuggestsSplit ? 0.22 : 0.12;
            dc.DrawRectangle(fill, null, new Rect(p.Start * scale, 0, pw, h));
            if (pw > 44)
            {
                var text = new FormattedText(Pauses.LengthText(p.Duration), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 10, warning,
                    VisualTreeHelper.GetDpi(this).PixelsPerDip) { MaxTextWidth = pw, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
                dc.DrawText(text, new Point(p.Start * scale + (pw - text.Width) / 2, (h - text.Height) / 2));
            }
        }
        var speech = Res("PrimaryBrush").Clone(); speech.Opacity = 0.6;
        foreach (var (start, end) in _vm.Blocks)
            dc.DrawRoundedRectangle(speech, null, new Rect(start * scale, h * 0.275, Math.Max(1, (end - start) * scale), h * 0.45), 1, 1);
        var x = Math.Min(w - 2, Math.Max(0, _vm.CurrentTime * scale - 1));
        dc.DrawRectangle(Res("TextBrush"), null, new Rect(x, 0, 2, h));
        dc.Pop();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        CaptureMouse();
        SeekTo(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (IsMouseCaptured) SeekTo(e.GetPosition(this).X);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) => ReleaseMouseCapture();

    void SeekTo(double x)
    {
        if (_vm is null || ActualWidth <= 0) return;
        _vm.Seek(Math.Clamp(x / ActualWidth, 0, 1) * _vm.Info.DurationSec);
    }
}
