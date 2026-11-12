using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using MeetingAssistant.Core.Text;

namespace MeetingAssistant.App.Controls;

/// <summary>
/// A Markdown field of the recap (and of CRM changes), spec 03 §8.2: shows the formatted text and switches to an
/// editor (AvalonEdit) with a formatting bar and live highlighting on click. Done or Esc returns to the preview.
/// </summary>
public sealed class MarkdownField : UserControl
{
    public const double MinEditorHeight = 90, MaxEditorHeight = 420, BaseSize = 13;

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(MarkdownField),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((MarkdownField)d).TextChangedOutside()));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }

    readonly Border _box;
    readonly Grid _previewHost;
    readonly StackPanel _preview;
    readonly Button _editButton;
    readonly DockPanel _editorHost;
    readonly TextEditor _editor;
    readonly Colorizer _colorizer;
    bool _editing, _syncing, _decided;

    public MarkdownField()
    {
        _preview = new StackPanel { Margin = new Thickness(12, 10, 12, 10), MinHeight = 20 };
        _editButton = new Button
        {
            Content = "Edit", Margin = new Thickness(6), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed, ToolTip = "Edit the Markdown text",
        };
        _editButton.SetResourceReference(StyleProperty, "Aya.Button.Secondary");
        Ui.SetSize(_editButton, ControlSize.Small);
        Ui.SetIcon(_editButton, "");
        _editButton.Click += (_, _) => StartEditing();
        _previewHost = new Grid { Background = Brushes.Transparent, Cursor = Cursors.IBeam };
        _previewHost.Children.Add(_preview);
        _previewHost.Children.Add(_editButton);
        _previewHost.MouseEnter += (_, _) => _editButton.Visibility = Visibility.Visible;
        _previewHost.MouseLeave += (_, _) => _editButton.Visibility = Visibility.Collapsed;
        _previewHost.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject o && FindAncestor<Hyperlink>(o) is not null) return;
            if (!_editButton.IsMouseOver) StartEditing();
        };

        _editor = new TextEditor
        {
            WordWrap = true, ShowLineNumbers = false, FontSize = BaseSize, Background = Brushes.Transparent,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(8), MinHeight = MinEditorHeight, MaxHeight = MaxEditorHeight,
        };
        _editor.SetResourceReference(FontFamilyProperty, "Font.UI");
        _editor.SetResourceReference(ForegroundProperty, "TextBrush");
        _editor.Options.EnableHyperlinks = false;
        _editor.Options.EnableEmailHyperlinks = false;
        _editor.Options.HighlightCurrentLine = false;
        _editor.Options.AllowScrollBelowDocument = false;
        _editor.Options.EnableRectangularSelection = false;
        _editor.Options.CutCopyWholeLine = false;
        _colorizer = new Colorizer(this);
        _editor.TextArea.TextView.LineTransformers.Add(_colorizer);
        _editor.TextChanged += (_, _) =>
        {
            _colorizer.Invalidate();
            if (_syncing) { _editor.TextArea.TextView.Redraw(); return; }
            _syncing = true;
            try { Text = _editor.Text; } finally { _syncing = false; }
            _editor.TextArea.TextView.Redraw();
        };
        _editor.TextArea.PreviewKeyDown += OnKey;

        var bar = Toolbar();
        DockPanel.SetDock(bar, Dock.Top);
        var line = new Border { Height = 1 };
        line.SetResourceReference(Border.BackgroundProperty, "BorderBrush");
        DockPanel.SetDock(line, Dock.Top);
        _editorHost = new DockPanel { Visibility = Visibility.Collapsed };
        _editorHost.Children.Add(bar);
        _editorHost.Children.Add(line);
        _editorHost.Children.Add(_editor);

        var content = new Grid();
        content.Children.Add(_previewHost);
        content.Children.Add(_editorHost);
        _box = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = content, ClipToBounds = true };
        _box.SetResourceReference(Border.BackgroundProperty, "CardBrush");
        _box.SetResourceReference(Border.BorderBrushProperty, "InputBrush");
        Content = _box;
        Loaded += (_, _) => { if (!_decided) { _decided = true; SetEditing(string.IsNullOrWhiteSpace(Text), focus: false); } };
    }

    // ---- Modes ----

    void StartEditing() => SetEditing(true, focus: true);

    void SetEditing(bool on, bool focus)
    {
        _decided = true;
        _editing = on;
        _previewHost.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        _editorHost.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        _box.SetResourceReference(Border.BorderBrushProperty, on ? "PrimaryBorder60Brush" : "InputBrush");
        if (on)
        {
            if (_editor.Text != Text) { _syncing = true; _editor.Document.Text = Text ?? ""; _syncing = false; }
            if (focus)
                Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { _editor.Focus(); _editor.CaretOffset = _editor.Document.TextLength; });
        }
        else RebuildPreview();
    }

    void TextChangedOutside()
    {
        if (_syncing) return;
        if (!_decided && IsLoaded) { _decided = true; SetEditing(string.IsNullOrWhiteSpace(Text), focus: false); return; }
        if (_editing)
        {
            if (_editor.Text != Text) { _syncing = true; _editor.Document.Text = Text ?? ""; _syncing = false; }
        }
        else RebuildPreview();
    }

    // ---- Formatting bar ----

    FrameworkElement Toolbar()
    {
        var p = new DockPanel { LastChildFill = false, Margin = new Thickness(6, 4, 6, 4) };
        var host = new Border { Child = p };
        host.SetResourceReference(Border.BackgroundProperty, "FieldBrush");

        var heading = Tool("H", "Heading", null, weight: FontWeights.SemiBold);
        var menu = new ContextMenu { PlacementTarget = heading, Placement = PlacementMode.Bottom };
        for (int level = 1; level <= 3; level++)
        {
            int l = level;
            var item = new MenuItem { Header = $"Heading {l}" };
            item.Click += (_, _) => Lines(MarkdownLineStyle.Heading, l);
            menu.Items.Add(item);
        }
        heading.Click += (_, _) => menu.IsOpen = true;
        var tools = new UIElement[]
        {
            heading,
            Tool("B", "Bold (Ctrl+B)", () => Wrap("**"), weight: FontWeights.Bold),
            Tool("I", "Italic (Ctrl+I)", () => Wrap("*"), style: FontStyles.Italic),
            Tool("S", "Strikethrough (Ctrl+Shift+X)", () => Wrap("~~"), strike: true),
            Tool("</>", "Code (Ctrl+E)", () => Wrap("`"), mono: true),
            Separator(),
            Tool("", "Bulleted list", () => Lines(MarkdownLineStyle.Bullet), icon: true),
            Tool("1.", "Numbered list", () => Lines(MarkdownLineStyle.Numbered)),
            Tool("", "Checklist", () => Lines(MarkdownLineStyle.Task), icon: true),
            Tool("", "Quote", () => Lines(MarkdownLineStyle.Quote), icon: true),
        };
        foreach (var t in tools) { DockPanel.SetDock(t, Dock.Left); p.Children.Add(t); }

        var done = new Button { Content = "Done", ToolTip = "Show the formatted text (Esc)" };
        done.SetResourceReference(StyleProperty, "Aya.Button.Secondary");
        Ui.SetSize(done, ControlSize.Small);
        done.Click += (_, _) => SetEditing(false, focus: false);
        DockPanel.SetDock(done, Dock.Right);
        p.Children.Add(done);
        return host;
    }

    Button Tool(string label, string tip, Action? action, FontWeight? weight = null, FontStyle? style = null,
                bool strike = false, bool mono = false, bool icon = false)
    {
        var tb = new TextBlock
        {
            Text = label, FontSize = icon ? 14 : 12.5, FontWeight = weight ?? FontWeights.Medium, FontStyle = style ?? FontStyles.Normal,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        if (strike) tb.TextDecorations = TextDecorations.Strikethrough;
        if (mono) tb.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Mono");
        if (icon) tb.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Icon");
        var b = new Button { Content = tb, Width = 28, Height = 24, ToolTip = tip, Focusable = false, Margin = new Thickness(0, 0, 2, 0) };
        b.SetResourceReference(StyleProperty, "Aya.IconButton.Borderless");
        if (action is not null) b.Click += (_, _) => action();
        return b;
    }

    static FrameworkElement Separator()
    {
        var s = new Border { Width = 1, Height = 16, Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        s.SetResourceReference(Border.BackgroundProperty, "BorderBrush");
        return s;
    }

    // ---- Editing commands ----

    void Wrap(string marker) => Apply(Markdown.Wrap(_editor.Text, _editor.SelectionStart, _editor.SelectionLength, marker));
    void Lines(MarkdownLineStyle s, int level = 2) => Apply(Markdown.ToggleLines(_editor.Text, _editor.SelectionStart, _editor.SelectionLength, s, level));

    /// Replaces only the changed part, as one undoable edit, then sets the selection.
    void Apply(MarkdownEditResult? r)
    {
        if (r is not { } res) return;
        var old = _editor.Text; var neu = res.Text;
        int n = Math.Min(old.Length, neu.Length), p = 0, s = 0;
        while (p < n && old[p] == neu[p]) p++;
        while (s < n - p && old[old.Length - 1 - s] == neu[neu.Length - 1 - s]) s++;
        int len = old.Length - p - s;
        var repl = neu.Substring(p, neu.Length - p - s);
        if (len > 0 || repl.Length > 0) _editor.Document.Replace(p, len, repl);
        int start = Math.Min(res.SelectionStart, _editor.Document.TextLength);
        _editor.Select(start, Math.Min(res.SelectionLength, _editor.Document.TextLength - start));
        _editor.Focus();
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        MarkdownEditResult? r = null;
        bool handled = true;
        switch (key)
        {
            case Key.Enter when mods == ModifierKeys.None:
                r = Markdown.Newline(_editor.Text, _editor.SelectionStart, _editor.SelectionLength); handled = r is not null; break;
            case Key.Tab when mods == ModifierKeys.None:
                r = Markdown.Indent(_editor.Text, _editor.SelectionStart, _editor.SelectionLength, outdent: false); handled = r is not null; break;
            case Key.Tab when mods == ModifierKeys.Shift:
                r = Markdown.Indent(_editor.Text, _editor.SelectionStart, _editor.SelectionLength, outdent: true); handled = r is not null; break;
            case Key.Escape:
                SetEditing(false, focus: false); break;
            case Key.B when mods == ModifierKeys.Control: Wrap("**"); break;
            case Key.I when mods == ModifierKeys.Control: Wrap("*"); break;
            case Key.E when mods == ModifierKeys.Control: Wrap("`"); break;
            case Key.X when mods == (ModifierKeys.Control | ModifierKeys.Shift): Wrap("~~"); break;
            default: handled = false; break;
        }
        if (r is not null) Apply(r);
        if (handled) e.Handled = true;
    }

    // ---- Preview ----

    void RebuildPreview()
    {
        _preview.Children.Clear();
        if (string.IsNullOrWhiteSpace(Text))
        {
            var empty = new TextBlock { Text = "Empty. Click to write.", FontSize = BaseSize };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "TextSubtleBrush");
            _preview.Children.Add(empty);
            return;
        }
        MarkdownBlock? prev = null;
        foreach (var b in Markdown.Blocks(Text))
        {
            var el = Block(b);
            double gap = prev is null ? 0 : (b is MarkdownItem && prev is MarkdownItem) ? 4 : 10;
            if (b is MarkdownHeading h && prev is not null) gap += h.Level <= 2 ? 4 : 2;
            el.Margin = new Thickness(el.Margin.Left, gap, 0, 0);
            _preview.Children.Add(el);
            prev = b;
        }
    }

    public static double HeadingSize(int level) => level switch { 1 => BaseSize + 5, 2 => BaseSize + 3, 3 => BaseSize + 1.5, _ => BaseSize };

    FrameworkElement Block(MarkdownBlock b)
    {
        switch (b)
        {
            case MarkdownHeading h:
                return Para(h.Text, HeadingSize(h.Level), FontWeights.SemiBold);
            case MarkdownParagraph p:
                return Para(p.Text, BaseSize);
            case MarkdownItem it:
            {
                var g = new Grid { Margin = new Thickness(it.Depth * 18, 0, 0, 0) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var marker = new TextBlock { FontSize = BaseSize, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 6, 0) };
                switch (it.Marker.Kind)
                {
                    case MarkdownMarkerKind.Bullet:
                        marker.Text = new[] { "•", "◦", "▪" }[it.Depth % 3]; marker.FontWeight = FontWeights.Bold;
                        marker.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryBrush"); break;
                    case MarkdownMarkerKind.Number:
                        marker.Text = $"{it.Marker.Number}."; marker.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush"); break;
                    default:
                        marker.Text = it.Marker.Done ? "" : "";
                        marker.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Icon");
                        marker.FontSize = BaseSize - 1; marker.VerticalAlignment = VerticalAlignment.Center;
                        marker.SetResourceReference(TextBlock.ForegroundProperty, it.Marker.Done ? "PrimaryBrush" : "TextSubtleBrush"); break;
                }
                var text = Para(it.Text, BaseSize);
                if (it.Marker is { Kind: MarkdownMarkerKind.Task, Done: true }) text.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
                Grid.SetColumn(text, 1);
                g.Children.Add(marker); g.Children.Add(text);
                return g;
            }
            case MarkdownQuote q:
            {
                var t = Para(q.Text, BaseSize);
                t.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
                var bd = new Border { BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(8, 0, 0, 0), Child = t };
                bd.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
                return bd;
            }
            case MarkdownCode c:
            {
                var t = new TextBlock { Text = c.Text, FontSize = BaseSize - 1, TextWrapping = TextWrapping.Wrap };
                t.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Mono");
                var bd = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(8), Child = t };
                bd.SetResourceReference(Border.BackgroundProperty, "FieldBrush");
                return bd;
            }
            case MarkdownTable tb:
                return Table(tb);
            default:
            {
                var r = new Border { Height = 1, Margin = new Thickness(0, 4, 0, 4) };
                r.SetResourceReference(Border.BackgroundProperty, "BorderBrush");
                return r;
            }
        }
    }

    FrameworkElement Table(MarkdownTable t)
    {
        var g = new Grid();
        for (int c = 0; c < t.Header.Count; c++) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var all = new List<IReadOnlyList<string>> { t.Header };
        all.AddRange(t.Rows);
        for (int r = 0; r < all.Count; r++)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int c = 0; c < t.Header.Count; c++)
            {
                var text = Para(c < all[r].Count ? all[r][c] : "", BaseSize - 1, r == 0 ? FontWeights.SemiBold : FontWeights.Normal);
                var cell = new Border
                {
                    Child = text, Padding = new Thickness(8, 5, 8, 5),
                    BorderThickness = new Thickness(c > 0 ? 1 : 0, r > 0 ? 1 : 0, 0, 0),
                };
                cell.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
                if (r == 0) cell.SetResourceReference(Border.BackgroundProperty, "MutedBrush");
                Grid.SetRow(cell, r); Grid.SetColumn(cell, c);
                g.Children.Add(cell);
            }
        }
        var bd = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Child = g, ClipToBounds = true };
        bd.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        return bd;
    }

    /// A wrapping text block with the inline runs (bold, italic, strikethrough, code, links) and its line breaks.
    TextBlock Para(string text, double size, FontWeight? weight = null)
    {
        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = size, FontWeight = weight ?? FontWeights.Normal };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        foreach (var run in Markdown.Inline(text))
        {
            var parts = run.Text.Split('\n');
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0) tb.Inlines.Add(new LineBreak());
                if (parts[i].Length == 0) continue;
                Inline inl = StyledRun(parts[i], run.Style, size);
                if (run.Link is { } url)
                {
                    var link = new Hyperlink(inl) { ToolTip = url };
                    link.SetResourceReference(TextElement.ForegroundProperty, "PrimaryBrush");
                    link.Click += (_, _) => App.Current.OpenUrl(url);
                    inl = link;
                }
                tb.Inlines.Add(inl);
            }
        }
        return tb;
    }

    Run StyledRun(string text, MarkdownStyle style, double size)
    {
        var r = new Run(text);
        if (style.HasFlag(MarkdownStyle.Bold)) r.FontWeight = FontWeights.SemiBold;
        if (style.HasFlag(MarkdownStyle.Italic)) r.FontStyle = FontStyles.Italic;
        if (style.HasFlag(MarkdownStyle.Strike)) r.TextDecorations = TextDecorations.Strikethrough;
        if (style.HasFlag(MarkdownStyle.Code))
        {
            r.SetResourceReference(TextElement.FontFamilyProperty, "Font.Mono");
            r.FontSize = size - 1;
            r.SetResourceReference(TextElement.BackgroundProperty, "MutedBrush");
        }
        return r;
    }

    static T? FindAncestor<T>(DependencyObject? o) where T : DependencyObject
    {
        while (o is not null)
        {
            if (o is T t) return t;
            o = o is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(o) : LogicalTreeHelper.GetParent(o);
        }
        return null;
    }

    // ---- Highlighting ----

    /// Colours the editor from <see cref="Markdown.Highlight"/>: headings larger, markup faint, list markers in the
    /// accent colour, inline styles, code and tables monospaced.
    sealed class Colorizer(MarkdownField owner) : DocumentColorizingTransformer
    {
        List<MarkdownSpan>? _spans;

        /// The text changed: the spans are computed again on the next line drawn.
        public void Invalidate() => _spans = null;

        protected override void ColorizeLine(DocumentLine line)
        {
            _spans ??= Markdown.Highlight(CurrentContext.Document.Text);
            int ls = line.Offset, le = line.EndOffset;
            if (le <= ls) return;
            Brush? B(string key) => owner.TryFindResource(key) as Brush;
            var ui = owner.TryFindResource("Font.UI") as FontFamily ?? new FontFamily("Segoe UI");
            var mono = owner.TryFindResource("Font.Mono") as FontFamily ?? new FontFamily("Consolas");
            foreach (var sp in _spans)
            {
                int a = Math.Max(sp.Start, ls), b = Math.Min(sp.Start + sp.Length, le);
                if (b <= a) continue;
                switch (sp.Kind)
                {
                    case MarkdownSpanKind.Heading:
                        ChangeLinePart(a, b, el =>
                        {
                            el.TextRunProperties.SetFontRenderingEmSize(HeadingSize(sp.Level));
                            el.TextRunProperties.SetTypeface(new Typeface(ui, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal));
                        });
                        break;
                    case MarkdownSpanKind.Quote:
                        if (B("TextMutedBrush") is { } m) ChangeLinePart(a, b, el => el.TextRunProperties.SetForegroundBrush(m));
                        break;
                    case MarkdownSpanKind.CodeBlock or MarkdownSpanKind.Table:
                        ChangeLinePart(a, b, el =>
                        {
                            el.TextRunProperties.SetTypeface(new Typeface(mono, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal));
                            el.TextRunProperties.SetFontRenderingEmSize(BaseSize - 1);
                        });
                        break;
                    case MarkdownSpanKind.Syntax:
                        if (B("TextSubtleBrush") is { } s) ChangeLinePart(a, b, el => el.TextRunProperties.SetForegroundBrush(s));
                        break;
                    case MarkdownSpanKind.ListMarker:
                        ChangeLinePart(a, b, el =>
                        {
                            if (B("PrimaryBrush") is { } p) el.TextRunProperties.SetForegroundBrush(p);
                            el.TextRunProperties.SetTypeface(new Typeface(ui, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal));
                        });
                        break;
                    case MarkdownSpanKind.Styled:
                        ChangeLinePart(a, b, el =>
                        {
                            var p = el.TextRunProperties;
                            var cur = p.Typeface;
                            if (sp.Style.HasFlag(MarkdownStyle.Code))
                            {
                                p.SetTypeface(new Typeface(mono, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal));
                                p.SetFontRenderingEmSize(p.FontRenderingEmSize - 1);
                                if (B("MutedBrush") is { } mb) p.SetBackgroundBrush(mb);
                            }
                            else
                            {
                                var weight = sp.Style.HasFlag(MarkdownStyle.Bold) ? FontWeights.Bold : cur.Weight;
                                var fstyle = sp.Style.HasFlag(MarkdownStyle.Italic) ? FontStyles.Italic : cur.Style;
                                p.SetTypeface(new Typeface(cur.FontFamily, fstyle, weight, cur.Stretch));
                            }
                            var deco = new TextDecorationCollection();
                            if (sp.Style.HasFlag(MarkdownStyle.Strike)) deco.Add(TextDecorations.Strikethrough);
                            if (sp.Link)
                            {
                                deco.Add(TextDecorations.Underline);
                                if (B("PrimaryBrush") is { } pb) p.SetForegroundBrush(pb);
                            }
                            if (deco.Count > 0) p.SetTextDecorations(deco);
                        });
                        break;
                }
            }
        }
    }
}
