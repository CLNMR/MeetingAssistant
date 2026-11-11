using System.Windows.Controls;
using System.Windows.Input;
using MeetingAssistant.App.ViewModels;

namespace MeetingAssistant.App.Views;

/// Scribe mode: recordings panel and main area (spec 01 "Scribe window, menus and shortcuts").
/// Up/Down in the list move the selection (ListBox behaviour; from the recorder they select the first/last row).
public partial class ScribeView : UserControl
{
    public ScribeView()
    {
        InitializeComponent();
        List.PreviewKeyDown += (_, e) =>
        {
            if (DataContext is not ScribeViewModel vm || vm.SelectedSession is not null) return;
            if (e.Key == Key.Down) { vm.Move(1); e.Handled = true; }
            else if (e.Key == Key.Up) { vm.Move(-1); e.Handled = true; }
        };
        List.SelectionChanged += (_, _) => { if (List.SelectedItem is { } item) List.ScrollIntoView(item); };
    }
}
