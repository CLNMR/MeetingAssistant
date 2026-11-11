using System.Windows.Controls;
using System.Windows.Input;
using MeetingAssistant.App.ViewModels;

namespace MeetingAssistant.App.Views;

/// View of <see cref="TimeChooserViewModel"/> (Mac TimeChooser).
public partial class TimeChooser : UserControl
{
    public TimeChooser() => InitializeComponent();

    void Field_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => (DataContext as TimeChooserViewModel)?.Apply();
}
