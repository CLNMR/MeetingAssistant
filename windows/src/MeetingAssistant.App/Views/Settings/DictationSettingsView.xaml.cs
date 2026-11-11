using System.Windows.Controls;
using MeetingAssistant.App.Dictation;

namespace MeetingAssistant.App.Views;

/// Settings › Dictation (spec 04 §6.2). The view model lives while the page is shown.
public partial class DictationSettingsView : UserControl
{
    public DictationSettingsView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (DataContext is DictationSettingsViewModel || App.Current?.Services is null) return;
            DataContext = new DictationSettingsViewModel(App.Current.Services, App.Current.Dictation);
        };
        Unloaded += (_, _) =>
        {
            (DataContext as DictationSettingsViewModel)?.Dispose();
            DataContext = null;
        };
    }
}
