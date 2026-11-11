using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using MeetingAssistant.App.Controls;
using MeetingAssistant.App.ViewModels;

namespace MeetingAssistant.App.Views;

/// <summary>
/// The grid of one template table (spec 03 §9.3): a rounded box with the column names, then one row per table row
/// (hairlines between). Answer columns are inline fields at most 180 px wide; other columns flex. Built in code
/// because the column layout is shared by the header and all rows. DataContext: <see cref="TableGridViewModel"/>.
/// </summary>
public sealed class TemplateTableGrid : UserControl
{
    readonly Grid _grid = new();
    readonly TextBlock _empty = new() { Text = "Nothing answered yet.", FontSize = 12, Margin = new Thickness(10) };
    TableGridViewModel? _vm;

    public TemplateTableGrid()
    {
        _empty.SetResourceReference(TextBlock.ForegroundProperty, "TextSubtleBrush");
        var stack = new StackPanel();
        stack.Children.Add(_grid);
        stack.Children.Add(_empty);
        var box = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = stack, ClipToBounds = true };
        box.SetResourceReference(Border.BackgroundProperty, "CardBrush");
        box.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        Content = box;
        DataContextChanged += (_, _) => Attach(DataContext as TableGridViewModel);
    }

    void Attach(TableGridViewModel? vm)
    {
        if (_vm is not null) _vm.PropertyChanged -= Changed;
        _vm = vm;
        if (vm is null) return;
        vm.PropertyChanged += Changed;
        Build();
    }

    void Changed(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TableGridViewModel.VisibleRows) or nameof(TableGridViewModel.Columns)) Build();
    }

    void Build()
    {
        _grid.Children.Clear();
        _grid.ColumnDefinitions.Clear();
        _grid.RowDefinitions.Clear();
        if (_vm is null) return;
        var cols = _vm.Columns;
        foreach (var c in cols)
            _grid.ColumnDefinitions.Add(c.IsAnswer ? new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = 180 }
                                                   : new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        // Header
        _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < cols.Count; i++)
        {
            var t = new TextBlock
            {
                Text = cols[i].Name, FontSize = 11, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxHeight = 30,
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(i == 0 ? 8 : 4, 6, i == cols.Count - 1 ? 8 : 4, 6),
            };
            t.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
            Grid.SetColumn(t, i);
            _grid.Children.Add(t);
        }
        AddLine(1, 1.0);
        var r = 1;
        foreach (var row in _vm.VisibleRows)
        {
            r++;
            _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var bg = new Border { DataContext = row };
            bg.SetBinding(Border.BackgroundProperty, new Binding(nameof(TableRowViewModel.IsChanged)) { Converter = new ChangedBrush() });
            Grid.SetRow(bg, r);
            Grid.SetColumnSpan(bg, Math.Max(1, cols.Count));
            _grid.Children.Add(bg);
            for (int i = 0; i < row.Cells.Count && i < cols.Count; i++)
            {
                var el = Cell(row.Cells[i]);
                el.Margin = new Thickness(i == 0 ? 8 : 4, 3, i == cols.Count - 1 ? 8 : 4, 3);
                Grid.SetRow(el, r);
                Grid.SetColumn(el, i);
                _grid.Children.Add(el);
            }
            r++;
            AddLine(r, 0.5);
        }
        _empty.Visibility = _vm.VisibleRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void AddLine(int row, double opacity)
    {
        while (_grid.RowDefinitions.Count <= row) _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var line = new Border { Height = 1, Opacity = opacity };
        line.SetResourceReference(Border.BackgroundProperty, "BorderBrush");
        Grid.SetRow(line, row);
        Grid.SetColumnSpan(line, Math.Max(1, _grid.ColumnDefinitions.Count));
        _grid.Children.Add(line);
    }

    static FrameworkElement Cell(TableCellViewModel c)
    {
        if (c.Kind == CellKind.Text)
        {
            var t = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxHeight = 34, TextTrimming = TextTrimming.CharacterEllipsis,
                                    VerticalAlignment = VerticalAlignment.Center, DataContext = c };
            t.SetBinding(TextBlock.TextProperty, new Binding(nameof(TableCellViewModel.Value)));
            t.SetResourceReference(TextBlock.ForegroundProperty, c.IsFirstKey ? "TextMutedBrush" : "TextBrush");
            return t;
        }
        var field = new CellField(c);
        if (c.Kind == CellKind.Answer) return field;
        // "Own proposal:" — the fixed prefix, then a field for the rest.
        var dock = new DockPanel { DataContext = c };
        var prefix = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
        prefix.SetBinding(TextBlock.TextProperty, new Binding(nameof(TableCellViewModel.Prefix)));
        prefix.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        dock.Children.Add(prefix);
        dock.Children.Add(field);
        return dock;
    }

    /// Agent at 10 % (row changed by the latest update, Claude's cell values).
    internal static Brush AgentTint()
    {
        var c = Application.Current?.TryFindResource("AgentColor") is Color col ? col : Color.FromRgb(0x35, 0xD2, 0xC1);
        var b = new SolidColorBrush(c) { Opacity = 0.10 };
        b.Freeze();
        return b;
    }

    sealed class ChangedBrush : IValueConverter
    {
        public object? Convert(object? v, Type t, object? p, System.Globalization.CultureInfo c) => v is true ? AgentTint() : Brushes.Transparent;
        public object ConvertBack(object? v, Type t, object? p, System.Globalization.CultureInfo c) => throw new NotSupportedException();
    }
}

/// <summary>
/// A table cell field (spec 03 §9.3): commits once, on Return or when focus leaves, and only when the trimmed value
/// changed; while focused, outside updates do not overwrite what the user types. Claude's values get an agent
/// 10 % fill and the evidence quote as tooltip.
/// </summary>
public sealed class CellField : TextBox
{
    readonly TableCellViewModel _cell;

    public CellField(TableCellViewModel cell)
    {
        _cell = cell;
        SetResourceReference(StyleProperty, typeof(TextBox));
        FontSize = 12;
        MinHeight = 24;
        Padding = new Thickness(6, 3, 6, 3);
        BorderThickness = new Thickness(0);
        TextWrapping = TextWrapping.Wrap;
        Ui.SetPlaceholder(this, "—");
        Ui.SetCornerRadius(this, new CornerRadius(4));
        Text = cell.Value;
        Paint();
        cell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TableCellViewModel.Value) && !IsKeyboardFocusWithin) Text = cell.Value;
            if (e.PropertyName is nameof(TableCellViewModel.FromClaude) or nameof(TableCellViewModel.Evidence)) Paint();
        };
        LostKeyboardFocus += (_, _) => _cell.Commit(Text);
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Return) return;
            _cell.Commit(Text);
            e.Handled = true;
        };
        AutomationProperties.SetName(this, cell.Column);
    }

    void Paint()
    {
        if (_cell.FromClaude) Background = TemplateTableGrid.AgentTint();
        else SetResourceReference(BackgroundProperty, "FieldBrush");
        ToolTip = _cell.Evidence;
    }
}
