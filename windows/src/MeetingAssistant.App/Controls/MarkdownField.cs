using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using MeetingAssistant.Core.Text;

namespace MeetingAssistant.App.Controls;

/// <summary>
/// A Markdown field of the recap (and of CRM changes), spec 03 §8.2: always the editor (AvalonEdit), so a click
/// puts the cursor where it lands. The Markdown is highlighted as it is typed; the formatting bar shows while the
/// field has the keyboard focus. Done or Esc takes the focus away.
/// </summary>
public sealed class MarkdownField : UserControl
{
    public const double MinEditorHeight = 44, MaxEditorHeight = 420, BaseSize = 13;

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(MarkdownField),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((MarkdownField)d).TextChangedOutside()));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }

    /// <summary>Puts the cursor into the field when it appears (debug snapshots only; a field never takes the focus by itself).</summary>
    public bool AutoFocus { get; set; }

    readonly Border _box;
    readonly FrameworkElement _bar, _barLine;
    readonly TextBlock _placeholder;
    readonly TextEditor _editor;
    readonly Colorizer _colorizer;
    ContextMenu? _headingMenu;
    bool _syncing, _focused;

    public MarkdownField()
    {
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
            UpdatePlaceholder();
            if (_syncing) { _editor.TextArea.TextView.Redraw(); return; }
            _syncing = true;
            try { Text = _editor.Text; } finally { _syncing = false; }
            _editor.TextArea.TextView.Redraw();
        };
        _editor.TextArea.PreviewKeyDown += OnKey;

        // "Empty. Click to write." over an empty field without focus; clicks go through to the editor.
        _placeholder = new TextBlock
        {
            Text = "Empty. Click to write.", FontSize = BaseSize, IsHitTestVisible = false,
            Margin = new Thickness(10, 8, 8, 8), VerticalAlignment = VerticalAlignment.Top,
        };
        _placeholder.SetResourceReference(TextBlock.ForegroundProperty, "TextSubtleBrush");
        var textHost = new Grid();
        textHost.Children.Add(_editor);
        textHost.Children.Add(_placeholder);

        _bar = Toolbar();
        DockPanel.SetDock(_bar, Dock.Top);
        var line = new Border { Height = 1 };
        line.SetResourceReference(Border.BackgroundProperty, "BorderBrush");
        DockPanel.SetDock(line, Dock.Top);
        _barLine = line;
        var content = new DockPanel();
        content.Children.Add(_bar);
        content.Children.Add(line);
        content.Children.Add(textHost);

        _box = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = content, ClipToBounds = true };
        _box.SetResourceReference(Border.BackgroundProperty, "CardBrush");
        Content = _box;
        IsKeyboardFocusWithinChanged += (_, _) => UpdateFocus();
        SetFocused(false);
        Loaded += (_, _) =>
        {
            if (!AutoFocus) return;
            AutoFocus = false;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { _editor.Focus(); _editor.CaretOffset = _editor.Document.TextLength; });
        };
    }

    // ---- Focus ----

    /// The field counts as focused while the keyboard focus is inside it, or the heading menu (a popup) is open.
    void UpdateFocus() => SetFocused(IsKeyboardFocusWithin || _headingMenu?.IsOpen == true);

    void SetFocused(bool on)
    {
        _focused = on;
        _bar.Visibility = _barLine.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        _box.SetResourceReference(Border.BorderBrushProperty, on ? "PrimaryBorder60Brush" : "InputBrush");
        UpdatePlaceholder();
    }

    void UpdatePlaceholder() =>
        _placeholder.Visibility = !_focused && string.IsNullOrWhiteSpace(_editor.Text) ? Visibility.Visible : Visibility.Collapsed;

    /// Done / Esc: takes the keyboard focus away from the field.
    void Leave()
    {
        if (_headingMenu is { IsOpen: true } m) m.IsOpen = false;
        var scope = FocusManager.GetFocusScope(this);
        if (scope is not null) FocusManager.SetFocusedElement(scope, null);
        Keyboard.ClearFocus();
        UpdateFocus();
    }

    void TextChangedOutside()
    {
        if (_syncing) return;
        if (_editor.Text != (Text ?? "")) { _syncing = true; try { _editor.Document.Text = Text ?? ""; } finally { _syncing = false; } }
        UpdatePlaceholder();
    }

    // ---- Formatting bar ----

    FrameworkElement Toolbar()
    {
        var p = new DockPanel { LastChildFill = false, Margin = new Thickness(6, 4, 6, 4) };
        var host = new Border { Child = p };
        host.SetResourceReference(Border.BackgroundProperty, "FieldBrush");

        // Toolbar buttons are not focusable, so a click keeps the focus (and the selection) in the editor.
        var heading = Tool("H", "Heading", null, weight: FontWeights.SemiBold);
        var menu = _headingMenu = new ContextMenu { PlacementTarget = heading, Placement = PlacementMode.Bottom };
        for (int level = 1; level <= 3; level++)
        {
            int l = level;
            var item = new MenuItem { Header = $"Heading {l}" };
            item.Click += (_, _) => Lines(MarkdownLineStyle.Heading, l);
            menu.Items.Add(item);
        }
        menu.Closed += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, UpdateFocus);
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

        var done = new Button { Content = "Done", ToolTip = "Leave the field (Esc)", Focusable = false };
        done.SetResourceReference(StyleProperty, "Aya.Button.Secondary");
        Ui.SetSize(done, ControlSize.Small);
        done.Click += (_, _) => Leave();
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
                Leave(); break;
            case Key.B when mods == ModifierKeys.Control: Wrap("**"); break;
            case Key.I when mods == ModifierKeys.Control: Wrap("*"); break;
            case Key.E when mods == ModifierKeys.Control: Wrap("`"); break;
            case Key.X when mods == (ModifierKeys.Control | ModifierKeys.Shift): Wrap("~~"); break;
            default: handled = false; break;
        }
        if (r is not null) Apply(r);
        if (handled) e.Handled = true;
    }

    public static double HeadingSize(int level) => level switch { 1 => BaseSize + 5, 2 => BaseSize + 3, 3 => BaseSize + 1.5, _ => BaseSize };

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
