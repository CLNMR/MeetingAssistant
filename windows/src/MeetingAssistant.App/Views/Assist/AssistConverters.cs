using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using MeetingAssistant.App.Controls;
using MeetingAssistant.App.ViewModels;
using MeetingAssistant.Core.Assist;
using MeetingAssistant.Core.Model;

namespace MeetingAssistant.App.Views;

/// Core <see cref="StepState"/> → <see cref="StepKind"/> for ui:StepLine (bind Message to <c>.Text</c>).
public sealed class StepKindConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => AssistUiHelpers.Kind(value as StepState);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// Speaker colour (spec 03 §3): index −1 is "me" (primary), 0…9 the fixed palette
/// (orange, purple, green, pink, teal, brown, indigo, mint, red, cyan).
public sealed class SpeakerBrushConverter : IValueConverter
{
    static readonly Color[] Palette =
    {
        Color.FromRgb(0xFF, 0x95, 0x00), Color.FromRgb(0xAF, 0x52, 0xDE), Color.FromRgb(0x34, 0xC7, 0x59), Color.FromRgb(0xFF, 0x2D, 0x55),
        Color.FromRgb(0x30, 0xB0, 0xC7), Color.FromRgb(0xA2, 0x84, 0x5E), Color.FromRgb(0x58, 0x56, 0xD6), Color.FromRgb(0x00, 0xC7, 0xBE),
        Color.FromRgb(0xFF, 0x3B, 0x30), Color.FromRgb(0x32, 0xAD, 0xE6),
    };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var i = value is int n ? n : -1;
        if (i < 0 || i >= Palette.Length) return Application.Current?.TryFindResource("PrimaryBrush") as Brush ?? Brushes.RoyalBlue;
        var b = new SolidColorBrush(Palette[i]);
        b.Freeze();
        return b;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// Meeting mode → icon glyph (call phone, in-person people, hybrid).
public sealed class ModeIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch { MeetingMode.InPerson => "Icon.InPerson", MeetingMode.Hybrid => "Icon.Hybrid", _ => "Icon.Call" };
        return Application.Current?.TryFindResource(key);
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// A 0…1 fraction times the ConverterParameter (a width in px), for progress fills.
public sealed class FractionWidthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var f = value is double d ? d : 0;
        var w = double.TryParse(parameter as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : 100;
        return Math.Clamp(f, 0, 1) * w;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// A fraction 0…1 and a total width → the filled width (MultiBinding: fraction, ActualWidth).
public sealed class FractionOfConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var f = values.Length > 0 && values[0] is double d ? d : 0;
        var w = values.Length > 1 && values[1] is double t ? t : 0;
        return Math.Clamp(f, 0, 1) * w;
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
