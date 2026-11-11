using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MeetingAssistant.App.Infrastructure;
using MeetingAssistant.App.ViewModels;

namespace MeetingAssistant.App.Shell;

/// Floating live transcript window (spec 01). Always on top and non-activating, so clicking it does not
/// take focus from the call app. Remembers its frame (`floatingPanel.frame`, Windows only) and whether it
/// was open (`floatingPanel.open`): closing it by the user clears the flag, quitting the app does not.
public partial class FloatingTranscriptWindow : Window
{
    readonly IUiState _ui;
    readonly FloatingTranscriptViewModel _vm;

    /// Set by the app while quitting so the open flag survives for the next launch.
    public bool AppQuitting { get; set; }

    public FloatingTranscriptWindow(IUiState ui, FloatingTranscriptViewModel vm)
    {
        _ui = ui;
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        PlaceInitially();
        SourceInitialized += (_, _) => Native.AddExStyle(this, Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
        // The view model outlives the window (it is reopened often): unsubscribe when closed.
        NotifyCollectionChangedEventHandler paragraphs = (_, _) => OnParagraphsChanged();
        System.ComponentModel.PropertyChangedEventHandler following = (_, e) =>
        {
            if (e.PropertyName == nameof(vm.Following) && vm.Following) Scroller.ScrollToEnd();
        };
        ((INotifyCollectionChanged)vm.Paragraphs).CollectionChanged += paragraphs;
        vm.PropertyChanged += following;
        Closed += (_, _) =>
        {
            ((INotifyCollectionChanged)vm.Paragraphs).CollectionChanged -= paragraphs;
            vm.PropertyChanged -= following;
        };
        InputBindings.Add(new KeyBinding(vm.LargerCommand, Key.OemPlus, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(vm.SmallerCommand, Key.OemMinus, ModifierKeys.Control));
        OnParagraphsChanged();
    }

    void PlaceInitially()
    {
        if (FrameParser.TryParse(_ui.Get<string?>(UiKeys.FloatingFrame, null), out var r, out _) && FrameParser.IsOnScreen(r))
        {
            Left = r.Left; Top = r.Top; Width = Math.Max(MinWidth, r.Width); Height = Math.Max(MinHeight, r.Height);
        }
        else
        {
            // Near the top-right of the main screen (Mac: x = maxX − 440, y = 20 below the top).
            var wa = SystemParameters.WorkArea;
            Left = wa.Right - 440;
            Top = wa.Top + 20;
        }
    }

    void OnParagraphsChanged()
    {
        Empty.Visibility = _vm.Paragraphs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_vm.Following) Dispatcher.BeginInvoke(() => Scroller.ScrollToEnd());
    }

    void Scroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Only the user's own scrolling changes "following": content growth keeps it as it is.
        if (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0) return;
        _vm.Following = Scroller.VerticalOffset >= Scroller.ScrollableHeight - 4;
    }

    void Jump_Click(object sender, RoutedEventArgs e) => Scroller.ScrollToEnd();

    void Background_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Movable by its background (buttons and the scroll bar handle their own clicks).
        if (e.ButtonState != MouseButtonState.Pressed) return;
        try { DragMove(); } catch (InvalidOperationException) { }
    }

    void More_Click(object sender, RoutedEventArgs e)
    {
        var menu = MoreButton.ContextMenu;
        menu.PlacementTarget = MoreButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.DataContext = DataContext;
        menu.IsOpen = true;
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        var s = string.Join(",", new[] { Left, Top, Width, Height }.Select(v => v.ToString("0", CultureInfo.InvariantCulture)));
        _ui.Set(UiKeys.FloatingFrame, s);
        if (!AppQuitting) _ui.Set(UiKeys.FloatingOpen, false);
        base.OnClosing(e);
    }
}
