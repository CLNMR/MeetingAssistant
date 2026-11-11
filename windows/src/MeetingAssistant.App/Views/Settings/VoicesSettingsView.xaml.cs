using System.Windows.Controls;
using MeetingAssistant.App.ViewModels;

namespace MeetingAssistant.App.Views;

/// Settings › Voices (spec 04 §6.4). Closing Settings stops a running sample recording.
public partial class VoicesSettingsView : UserControl
{
    public VoicesSettingsView()
    {
        InitializeComponent();
        Unloaded += (_, _) => (DataContext as VoicesSettingsViewModel)?.Cancel();
    }
}
