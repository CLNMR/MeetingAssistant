using System.Windows.Controls;
using MeetingAssistant.App.ViewModels;

namespace MeetingAssistant.App.Views;

/// Spec 03 §11, 04 §6.6 · Settings › Assist. On appear: check Claude if unknown, load the list catalogue if needed.
public partial class AssistSettingsView : UserControl
{
    public AssistSettingsView()
    {
        InitializeComponent();
        if (App.Current?.Assist is { } assist) DataContext = new AssistSettingsViewModel(assist);
        Loaded += (_, _) => (DataContext as AssistSettingsViewModel)?.Appeared();
    }
}
