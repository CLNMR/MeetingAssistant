using System.Windows;
using System.Windows.Media;

namespace MeetingAssistant.App.Controls;

/// Size of an Aya button (spec 03 §12: small 26 / regular 32 / large 40 px high).
public enum ControlSize { Regular, Small, Large }

/// Attached properties the Aya control styles read (icon glyph, size, pill shape, placeholder…).
/// Set them on buttons, text boxes, tab items etc.: `ui:Ui.Icon="{StaticResource Icon.Mic}"`.
public static class Ui
{
    /// Segoe Fluent Icons glyph shown before the content (buttons, tabs, text fields, menu items).
    public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached(
        "Icon", typeof(string), typeof(Ui), new PropertyMetadata(null));
    public static string? GetIcon(DependencyObject o) => (string?)o.GetValue(IconProperty);
    public static void SetIcon(DependencyObject o, string? v) => o.SetValue(IconProperty, v);

    /// Button size (small/regular/large).
    public static readonly DependencyProperty SizeProperty = DependencyProperty.RegisterAttached(
        "Size", typeof(ControlSize), typeof(Ui), new PropertyMetadata(ControlSize.Regular));
    public static ControlSize GetSize(DependencyObject o) => (ControlSize)o.GetValue(SizeProperty);
    public static void SetSize(DependencyObject o, ControlSize v) => o.SetValue(SizeProperty, v);

    /// Pill shape (radius 999, 14 px padding for regular size).
    public static readonly DependencyProperty PillProperty = DependencyProperty.RegisterAttached(
        "Pill", typeof(bool), typeof(Ui), new PropertyMetadata(false));
    public static bool GetPill(DependencyObject o) => (bool)o.GetValue(PillProperty);
    public static void SetPill(DependencyObject o, bool v) => o.SetValue(PillProperty, v);

    /// Placeholder text for text boxes and the search field (shown in textSubtle while empty).
    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.RegisterAttached(
        "Placeholder", typeof(string), typeof(Ui), new PropertyMetadata(null));
    public static string? GetPlaceholder(DependencyObject o) => (string?)o.GetValue(PlaceholderProperty);
    public static void SetPlaceholder(DependencyObject o, string? v) => o.SetValue(PlaceholderProperty, v);

    /// Corner radius override for templated controls that expose none.
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.RegisterAttached(
        "CornerRadius", typeof(CornerRadius), typeof(Ui), new PropertyMetadata(new CornerRadius(8)));
    public static CornerRadius GetCornerRadius(DependencyObject o) => (CornerRadius)o.GetValue(CornerRadiusProperty);
    public static void SetCornerRadius(DependencyObject o, CornerRadius v) => o.SetValue(CornerRadiusProperty, v);

    /// Fill while the pointer is over the control (each button kind sets its own).
    public static readonly DependencyProperty HoverBackgroundProperty = DependencyProperty.RegisterAttached(
        "HoverBackground", typeof(Brush), typeof(Ui), new PropertyMetadata(null));
    public static Brush? GetHoverBackground(DependencyObject o) => (Brush?)o.GetValue(HoverBackgroundProperty);
    public static void SetHoverBackground(DependencyObject o, Brush? v) => o.SetValue(HoverBackgroundProperty, v);

    /// Selected/active state for icon buttons (rail mode buttons) and chips.
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.RegisterAttached(
        "IsActive", typeof(bool), typeof(Ui), new PropertyMetadata(false));
    public static bool GetIsActive(DependencyObject o) => (bool)o.GetValue(IsActiveProperty);
    public static void SetIsActive(DependencyObject o, bool v) => o.SetValue(IsActiveProperty, v);

    /// Small red dot at the top-right of a rail button (a recording runs in the other mode).
    public static readonly DependencyProperty ShowDotProperty = DependencyProperty.RegisterAttached(
        "ShowDot", typeof(bool), typeof(Ui), new PropertyMetadata(false));
    public static bool GetShowDot(DependencyObject o) => (bool)o.GetValue(ShowDotProperty);
    public static void SetShowDot(DependencyObject o, bool v) => o.SetValue(ShowDotProperty, v);

    /// Coloured glow behind the control (agent buttons: teal glow 55 %, blur 7).
    public static readonly DependencyProperty GlowProperty = DependencyProperty.RegisterAttached(
        "Glow", typeof(bool), typeof(Ui), new PropertyMetadata(false));
    public static bool GetGlow(DependencyObject o) => (bool)o.GetValue(GlowProperty);
    public static void SetGlow(DependencyObject o, bool v) => o.SetValue(GlowProperty, v);

    /// Secondary line under a settings row label or a switch label.
    public static readonly DependencyProperty HintProperty = DependencyProperty.RegisterAttached(
        "Hint", typeof(string), typeof(Ui), new PropertyMetadata(null));
    public static string? GetHint(DependencyObject o) => (string?)o.GetValue(HintProperty);
    public static void SetHint(DependencyObject o, string? v) => o.SetValue(HintProperty, v);
}
