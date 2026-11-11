using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MeetingAssistant.App.ViewModels;

namespace MeetingAssistant.App.Views;

/// Session detail (spec 01). Space plays/pauses (unless typing), the current line is kept centred
/// while playing, speaker chips and lines open their menus here.
public partial class SessionDetailView : UserControl
{
    SessionDetailViewModel? _vm;

    public SessionDetailView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.CurrentRowChanged -= OnCurrentRow;
            _vm = DataContext as SessionDetailViewModel;
            if (_vm is not null) _vm.CurrentRowChanged += OnCurrentRow;
        };
        PreviewKeyDown += OnPreviewKeyDown;
        Focusable = true;
        Loaded += (_, _) => { if (Keyboard.FocusedElement is not TextBox) Focus(); };
    }

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || Keyboard.Modifiers != ModifierKeys.None) return;
        if (Keyboard.FocusedElement is TextBox { IsReadOnly: false }) return;
        _vm?.TogglePlay();
        e.Handled = true;
    }

    void OnCurrentRow(TurnRowViewModel row)
    {
        Lines.ScrollIntoView(row);
        if (Lines.ItemContainerGenerator.ContainerFromItem(row) is FrameworkElement c)
            c.BringIntoView(new Rect(0, -Lines.ActualHeight / 2 + c.ActualHeight / 2, c.ActualWidth, Lines.ActualHeight));
    }

    void TitleBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) { if (_vm is not null) _vm.IsTitleFocused = true; }

    void TitleBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_vm is null) return;
        _vm.IsTitleFocused = false;
        _vm.CommitTitle();
    }

    /// Opens a button's drop-down menu (Export, Edit).
    void Menu_Click(object sender, RoutedEventArgs e) => OpenMenu((Button)sender, ((Button)sender).ContextMenu);

    static void OpenMenu(FrameworkElement target, ContextMenu menu)
    {
        menu.PlacementTarget = target;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.DataContext = target.DataContext;
        menu.IsOpen = true;
    }

    static MenuItem Item(string header, System.Windows.Input.ICommand? command, bool isChecked = false) =>
        new() { Header = header, Command = command, IsCheckable = false, IsChecked = isChecked };

    /// Transcribe Again: "Parakeet Ultra (fast)" with the three languages (Windows has no Qwen3).
    void Again_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "Parakeet Ultra (fast)", IsEnabled = false });
        foreach (var c in _vm.RetranscribeChoices) menu.Items.Add(Item(c.Title, c.Command, c.IsChecked));
        OpenMenu((Button)sender, menu);
    }

    /// Speaker chip menu: Rename…, This is me, Merge into ▸.
    void Chip_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null || ((FrameworkElement)sender).Tag is not SpeakerChip chip) return;
        var menu = new ContextMenu();
        menu.Items.Add(Item("Rename…", _vm.RenameSpeakerCommand));
        ((MenuItem)menu.Items[0]).CommandParameter = chip;
        if (!chip.IsMe)
        {
            var me = Item("This is me", _vm.MarkMeCommand);
            me.CommandParameter = chip;
            menu.Items.Add(me);
        }
        var merge = new MenuItem { Header = "Merge into", IsEnabled = chip.MergeTargets.Count > 0 };
        foreach (var t in chip.MergeTargets) merge.Items.Add(Item(t.Title, t.Command));
        menu.Items.Add(merge);
        OpenMenu((FrameworkElement)sender, menu);
    }

    /// Line menu: Change speaker to ▸ (speakers, New speaker), Copy.
    void Turn_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_vm is null || sender is not FrameworkElement { DataContext: TurnRowViewModel row } fe || fe.ContextMenu is not { } menu) return;
        menu.Items.Clear();
        var change = new MenuItem { Header = "Change speaker to" };
        foreach (var c in _vm.ReassignChoices(row)) change.Items.Add(Item(c.Title, c.Command));
        menu.Items.Add(change);
        var copy = Item("Copy", _vm.CopyTurnCommand);
        copy.CommandParameter = row;
        menu.Items.Add(copy);
    }
}
