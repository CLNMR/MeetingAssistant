using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using MeetingAssistant.App.ViewModels;
using MeetingAssistant.Core.Assist;

namespace MeetingAssistant.App.Views;

/// <summary>
/// Aya select (spec 03 §10): a trigger showing the selected title (or "Select…") and a floating list with
/// keyboard control. Lists with more than 12 items get type-to-search (contains, case- and accent-insensitive);
/// short lists jump to the first item starting with a typed letter or digit. Items are <see cref="Choice"/>s.
/// </summary>
public sealed class AyaSelect : UserControl
{
    /// Lists longer than this get the search bar.
    public const int SearchThreshold = 12;
    const double RowHeight = 28;

    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IEnumerable), typeof(AyaSelect), new PropertyMetadata(null, (d, _) => ((AyaSelect)d).UpdateTitle()));
    /// The options (<see cref="Choice"/>).
    public IEnumerable? ItemsSource { get => (IEnumerable?)GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }

    public static readonly DependencyProperty SelectedValueProperty = DependencyProperty.Register(
        nameof(SelectedValue), typeof(object), typeof(AyaSelect),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((AyaSelect)d).UpdateTitle()));
    /// The selected choice's value.
    public object? SelectedValue { get => GetValue(SelectedValueProperty); set => SetValue(SelectedValueProperty, value); }

    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.Register(
        nameof(Placeholder), typeof(string), typeof(AyaSelect), new PropertyMetadata("Select…", (d, _) => ((AyaSelect)d).UpdateTitle()));
    public string Placeholder { get => (string)GetValue(PlaceholderProperty); set => SetValue(PlaceholderProperty, value); }

    readonly ToggleButton _toggle;
    readonly TextBlock _title;
    readonly Popup _popup;
    readonly ListBox _list;
    readonly TextBlock _search;
    readonly Border _searchBar;
    string _query = "";
    DateTime _closedAt;
    List<Choice> _all = new();

    public AyaSelect()
    {
        MinHeight = 32;
        Focusable = false;
        _toggle = new ToggleButton { Focusable = true, MinHeight = 32 };
        _toggle.SetResourceReference(StyleProperty, "Aya.ComboBoxToggle");
        _toggle.Click += (_, _) => ToggleClicked();
        _toggle.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Down or Key.Space or Key.Enter) { Open(); e.Handled = true; }
        };
        _title = new TextBlock
        {
            Margin = new Thickness(12, 0, 32, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis, IsHitTestVisible = false,
        };

        _search = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
        _search.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        _searchBar = new Border { Height = 32, BorderThickness = new Thickness(0, 0, 0, 1), Child = _search };
        _searchBar.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        DockPanel.SetDock(_searchBar, Dock.Top);

        _list = new ListBox { BorderThickness = new Thickness(0), Background = Brushes.Transparent, Focusable = true };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.ItemTemplate = RowTemplate();
        _list.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (ItemAt(e.OriginalSource as DependencyObject) is { } c) { Pick(c); e.Handled = true; }
        };
        _list.MouseMove += (_, e) =>
        {
            if (ItemAt(e.OriginalSource as DependencyObject) is { } c && !Equals(_list.SelectedItem, c)) _list.SelectedItem = c;
        };

        var panel = new DockPanel { LastChildFill = true };
        panel.Children.Add(_searchBar);
        panel.Children.Add(_list);
        var box = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(4), Child = panel,
                               Margin = new Thickness(8, 0, 8, 12), Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 4, Direction = 270, Opacity = 0.14 } };
        box.SetResourceReference(Border.BackgroundProperty, "PopoverBrush");
        box.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        box.PreviewKeyDown += ListKeyDown;
        box.PreviewTextInput += ListTextInput;

        _popup = new Popup
        {
            Child = box, StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Bottom, VerticalOffset = 4,
            HorizontalOffset = -8, PlacementTarget = this, PopupAnimation = PopupAnimation.Fade,
        };
        _popup.Closed += (_, _) =>
        {
            _closedAt = DateTime.UtcNow;
            _toggle.IsChecked = false;
        };

        var root = new Grid();
        root.Children.Add(_toggle);
        root.Children.Add(_title);
        root.Children.Add(_popup);
        Content = root;
        IsEnabledChanged += (_, _) => Opacity = IsEnabled ? 1 : 0.5;
        UpdateTitle();
    }

    static DataTemplate RowTemplate()
    {
        var t = new DataTemplate(typeof(Choice));
        var grid = new FrameworkElementFactory(typeof(DockPanel));
        grid.SetValue(HeightProperty, RowHeight - 4);
        var check = new FrameworkElementFactory(typeof(TextBlock));
        check.SetValue(DockPanel.DockProperty, Dock.Right);
        check.SetValue(TextBlock.FontSizeProperty, 11.0);
        check.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        check.SetValue(TextBlock.MarginProperty, new Thickness(8, 0, 0, 0));
        check.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Icon");
        check.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryBrush");
        check.Name = "Check";
        var title = new FrameworkElementFactory(typeof(TextBlock));
        title.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Title"));
        title.SetValue(TextBlock.FontSizeProperty, 13.0);
        title.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        title.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        grid.AppendChild(check);
        grid.AppendChild(title);
        t.VisualTree = grid;
        return t;
    }

    List<Choice> Items => ItemsSource?.OfType<Choice>().ToList() ?? new List<Choice>();
    Choice? SelectedChoice => Items.FirstOrDefault(c => Equals(c.Value, SelectedValue));

    void UpdateTitle()
    {
        var c = SelectedChoice;
        _title.Text = c?.Title ?? Placeholder;
        _title.SetResourceReference(TextBlock.ForegroundProperty, c is null ? "TextMutedBrush" : "TextBrush");
    }

    void ToggleClicked()
    {
        // A click on the trigger right after the list closed (it closed because of this click) keeps it closed.
        if ((DateTime.UtcNow - _closedAt).TotalMilliseconds < 250 || _popup.IsOpen) { _toggle.IsChecked = false; _popup.IsOpen = false; return; }
        Open();
    }

    void Open()
    {
        _all = Items;
        _query = "";
        var searchable = _all.Count > SearchThreshold;
        _searchBar.Visibility = searchable ? Visibility.Visible : Visibility.Collapsed;
        Refilter();
        var width = Math.Max(ActualWidth, 180);
        ((Border)_popup.Child).Width = width + 16;
        _list.MaxHeight = Math.Min(_all.Count * RowHeight + 8, 320);
        _list.SelectedItem = SelectedChoice is { } s ? _list.Items.OfType<Choice>().FirstOrDefault(x => Equals(x.Value, s.Value)) : _list.Items.OfType<Choice>().FirstOrDefault();
        _toggle.IsChecked = true;
        _popup.IsOpen = true;
        Dispatcher.BeginInvoke(() =>
        {
            MarkSelected();
            if (_list.SelectedItem is { } sel) _list.ScrollIntoView(sel);
            (_list.ItemContainerGenerator.ContainerFromItem(_list.SelectedItem) as ListBoxItem ?? (UIElement)_list).Focus();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    void Close()
    {
        _popup.IsOpen = false;
        _toggle.Focus();
    }

    void Pick(Choice c)
    {
        SelectedValue = c.Value;
        UpdateTitle();
        Close();
    }

    void Refilter()
    {
        var q = AssistText.Fold(_query);
        var hits = q.Length == 0 ? _all : _all.Where(c => AssistText.Fold(c.Title).Contains(q, StringComparison.Ordinal)).ToList();
        _list.ItemsSource = hits;
        _list.SelectedIndex = hits.Count > 0 ? 0 : -1;
        if (_all.Count > SearchThreshold)
            _search.Text = _query.Length == 0 ? "Type to search" : hits.Count == 0 ? $"{_query} · No match" : $"{_query} · {hits.Count}";
        Dispatcher.BeginInvoke(MarkSelected, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// Puts the check mark on the selected row.
    void MarkSelected()
    {
        foreach (var c in _list.Items.OfType<Choice>())
        {
            if (_list.ItemContainerGenerator.ContainerFromItem(c) is not ListBoxItem item) continue;
            var check = FindChild<TextBlock>(item, "Check");
            if (check is not null) check.Text = Equals(c.Value, SelectedValue) && SelectedChoice is not null ? "" : "";
        }
    }

    static T? FindChild<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t && t.Name == name) return t;
            if (FindChild<T>(child, name) is { } hit) return hit;
        }
        return null;
    }

    Choice? ItemAt(DependencyObject? d)
    {
        while (d is not null && d is not ListBoxItem) d = VisualTreeHelper.GetParent(d);
        return (d as ListBoxItem)?.DataContext as Choice;
    }

    void Move(int delta)
    {
        if (_list.Items.Count == 0) return;
        _list.SelectedIndex = Math.Clamp(_list.SelectedIndex + delta, 0, _list.Items.Count - 1);
        _list.ScrollIntoView(_list.SelectedItem);
    }

    void ListKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Up: Move(-1); e.Handled = true; break;
            case Key.Down: Move(1); e.Handled = true; break;
            case Key.Enter:
                if (_list.SelectedItem is Choice c) Pick(c);
                e.Handled = true; break;
            case Key.Space:
                if (_query.Length == 0) { if (_list.SelectedItem is Choice s) Pick(s); }
                else { _query += " "; Refilter(); }
                e.Handled = true; break;
            case Key.Escape: Close(); e.Handled = true; break;
            case Key.Back:
                if (_all.Count > SearchThreshold && _query.Length > 0) { _query = _query[..^1]; Refilter(); }
                e.Handled = true; break;
        }
    }

    void ListTextInput(object sender, TextCompositionEventArgs e)
    {
        var text = e.Text;
        if (string.IsNullOrEmpty(text) || char.IsControl(text[0])) return;
        e.Handled = true;
        if (_all.Count > SearchThreshold)
        {
            _query += text;
            Refilter();
            return;
        }
        // Type-ahead: jump to the first item starting with the letter or digit.
        if (!char.IsLetterOrDigit(text[0])) return;
        var hit = _list.Items.OfType<Choice>().FirstOrDefault(c => c.Title.StartsWith(text, StringComparison.CurrentCultureIgnoreCase));
        if (hit is not null) { _list.SelectedItem = hit; _list.ScrollIntoView(hit); }
    }
}
