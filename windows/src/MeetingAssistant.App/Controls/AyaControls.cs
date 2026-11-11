using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MeetingAssistant.App.Controls;

/// A raised surface (spec 03 §12 "Surfaces"): `card` fill, 1 px `border`, radius 16, a faint shadow.
/// With IsAgent it is the agent surface: while IsLit it gets the agent 60 % border and the teal glow.
public class Card : ContentControl
{
    static Card() => DefaultStyleKeyProperty.OverrideMetadata(typeof(Card), new FrameworkPropertyMetadata(typeof(Card)));

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(CornerRadius), typeof(Card), new PropertyMetadata(new CornerRadius(16)));
    /// Corner radius (16 surface, 14 overlay/rail, 10 container).
    public CornerRadius CornerRadius { get => (CornerRadius)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }

    public static readonly DependencyProperty HasShadowProperty = DependencyProperty.Register(
        nameof(HasShadow), typeof(bool), typeof(Card), new PropertyMetadata(true));
    /// Faint drop shadow (black 5 %, blur 1.5, y 1). Off for the transcript panel.
    public bool HasShadow { get => (bool)GetValue(HasShadowProperty); set => SetValue(HasShadowProperty, value); }

    public static readonly DependencyProperty IsAgentProperty = DependencyProperty.Register(
        nameof(IsAgent), typeof(bool), typeof(Card), new PropertyMetadata(false));
    /// Agent surface (status bar, next question, "What Claude will know").
    public bool IsAgent { get => (bool)GetValue(IsAgentProperty); set => SetValue(IsAgentProperty, value); }

    public static readonly DependencyProperty IsLitProperty = DependencyProperty.Register(
        nameof(IsLit), typeof(bool), typeof(Card), new PropertyMetadata(true));
    /// For agent surfaces: lit (teal border and glow) or looking like a plain card.
    public bool IsLit { get => (bool)GetValue(IsLitProperty); set => SetValue(IsLitProperty, value); }
}

/// Uppercase label with wide tracking and an optional icon (spec 03 §3 "Eyebrow").
public class Eyebrow : Control
{
    static Eyebrow() => DefaultStyleKeyProperty.OverrideMetadata(typeof(Eyebrow), new FrameworkPropertyMetadata(typeof(Eyebrow)));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(Eyebrow), new PropertyMetadata("", null, (_, v) => (v as string ?? "").ToUpperInvariant()));
    /// The text; shown uppercased.
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }

    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(string), typeof(Eyebrow), new PropertyMetadata(null));
    /// Optional icon glyph (11 px, 6 px gap).
    public string? Icon { get => (string?)GetValue(IconProperty); set => SetValue(IconProperty, value); }
}

/// Small tinted label (spec 03 §3 "Tag"): 11 pt medium text in Tint on a 12 % fill of Tint, radius 6.
public class AyaTag : Control
{
    static AyaTag() => DefaultStyleKeyProperty.OverrideMetadata(typeof(AyaTag), new FrameworkPropertyMetadata(typeof(AyaTag)));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(AyaTag), new PropertyMetadata(""));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }

    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(string), typeof(AyaTag), new PropertyMetadata(null));
    public string? Icon { get => (string?)GetValue(IconProperty); set => SetValue(IconProperty, value); }

    public static readonly DependencyProperty TintProperty = DependencyProperty.Register(
        nameof(Tint), typeof(Brush), typeof(AyaTag), new PropertyMetadata(null));
    /// Text colour; the fill is the same colour at 12 %. Defaults to primary (set in the style).
    public Brush? Tint { get => (Brush?)GetValue(TintProperty); set => SetValue(TintProperty, value); }
}

/// Card with an eyebrow title, an icon and an optional trailing element (spec 03 §3 "PanelBox").
public class PanelBox : HeaderedContentControl
{
    static PanelBox() => DefaultStyleKeyProperty.OverrideMetadata(typeof(PanelBox), new FrameworkPropertyMetadata(typeof(PanelBox)));

    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(string), typeof(PanelBox), new PropertyMetadata(null));
    public string? Icon { get => (string?)GetValue(IconProperty); set => SetValue(IconProperty, value); }

    public static readonly DependencyProperty TrailingProperty = DependencyProperty.Register(
        nameof(Trailing), typeof(object), typeof(PanelBox), new PropertyMetadata(null));
    /// Usually a count (11 pt medium, monospaced digits, textMuted).
    public object? Trailing { get => GetValue(TrailingProperty); set => SetValue(TrailingProperty, value); }

    public static readonly DependencyProperty HeaderBrushProperty = DependencyProperty.Register(
        nameof(HeaderBrush), typeof(Brush), typeof(PanelBox), new PropertyMetadata(null));
    /// Eyebrow colour (default textMuted; agent panels use agentTextMid).
    public Brush? HeaderBrush { get => (Brush?)GetValue(HeaderBrushProperty); set => SetValue(HeaderBrushProperty, value); }
}

/// Eyebrow, 24 pt title, optional subtitle, trailing controls bottom-right (spec 03 §3 "PageHeader").
public class PageHeader : Control
{
    static PageHeader() => DefaultStyleKeyProperty.OverrideMetadata(typeof(PageHeader), new FrameworkPropertyMetadata(typeof(PageHeader)));

    public static readonly DependencyProperty EyebrowProperty = DependencyProperty.Register(nameof(Eyebrow), typeof(string), typeof(PageHeader));
    public string? Eyebrow { get => (string?)GetValue(EyebrowProperty); set => SetValue(EyebrowProperty, value); }

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(PageHeader));
    public string? Title { get => (string?)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(PageHeader));
    public string? Subtitle { get => (string?)GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }

    public static readonly DependencyProperty TrailingProperty = DependencyProperty.Register(nameof(Trailing), typeof(object), typeof(PageHeader));
    public object? Trailing { get => GetValue(TrailingProperty); set => SetValue(TrailingProperty, value); }
}

/// The blue rounded square with the white mark (spec 03 §1 logo tile). Size it with Width/Height.
public class LogoTile : Control
{
    static LogoTile() => DefaultStyleKeyProperty.OverrideMetadata(typeof(LogoTile), new FrameworkPropertyMetadata(typeof(LogoTile)));
}

/// The soft blue/teal glow behind the Assist screens (spec 03 §12 "Backdrop"). Not hit-testable.
public class Backdrop : Control
{
    static Backdrop() => DefaultStyleKeyProperty.OverrideMetadata(typeof(Backdrop), new FrameworkPropertyMetadata(typeof(Backdrop)));
    public Backdrop() => IsHitTestVisible = false;
}

/// State of one async step (spec 03 §3 "StepLine").
public enum StepKind { Idle, Working, Done, Failed }

/// Status line for one async step: spinner + message, check + message (success), warning + message.
public class StepLine : Control
{
    static StepLine() => DefaultStyleKeyProperty.OverrideMetadata(typeof(StepLine), new FrameworkPropertyMetadata(typeof(StepLine)));

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(StepKind), typeof(StepLine), new PropertyMetadata(StepKind.Idle));
    public StepKind Kind { get => (StepKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }

    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(string), typeof(StepLine), new PropertyMetadata(""));
    public string? Message { get => (string?)GetValue(MessageProperty); set => SetValue(MessageProperty, value); }
}

/// Small indeterminate spinner (rotating arc) used by StepLine and status texts.
public class Spinner : Control
{
    static Spinner() => DefaultStyleKeyProperty.OverrideMetadata(typeof(Spinner), new FrameworkPropertyMetadata(typeof(Spinner)));
}

/// Titled placeholder for a feature screen that the next phase fills in.
public class ScreenPlaceholder : Control
{
    static ScreenPlaceholder() => DefaultStyleKeyProperty.OverrideMetadata(typeof(ScreenPlaceholder), new FrameworkPropertyMetadata(typeof(ScreenPlaceholder)));

    public static readonly DependencyProperty EyebrowProperty = DependencyProperty.Register(nameof(Eyebrow), typeof(string), typeof(ScreenPlaceholder));
    public string? Eyebrow { get => (string?)GetValue(EyebrowProperty); set => SetValue(EyebrowProperty, value); }

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(ScreenPlaceholder));
    public string? Title { get => (string?)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(ScreenPlaceholder));
    public string? Subtitle { get => (string?)GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }

    public static readonly DependencyProperty SpecProperty = DependencyProperty.Register(nameof(Spec), typeof(string), typeof(ScreenPlaceholder));
    /// The spec section the screen implements (shown as a muted note).
    public string? Spec { get => (string?)GetValue(SpecProperty); set => SetValue(SpecProperty, value); }
}

/// One option of a segmented control (ListBox with style Aya.Segmented): value, title, optional icon glyph.
public sealed record Segment(object Value, string Title, string? Icon = null);
