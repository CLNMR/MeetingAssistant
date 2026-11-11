using System.Windows.Controls;
using MeetingAssistant.App.Dictation;

namespace MeetingAssistant.App.Views;

/// Settings › Vocabulary (spec 04 §6.3). The view model lives while the page is shown.
public partial class VocabularySettingsView : UserControl
{
    public VocabularySettingsView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (DataContext is VocabularySettingsViewModel || App.Current?.Services is null) return;
            DataContext = new VocabularySettingsViewModel(App.Current.Services, App.Current.Dictation);
        };
        Unloaded += (_, _) =>
        {
            (DataContext as VocabularySettingsViewModel)?.Dispose();
            DataContext = null;
        };
    }
}
