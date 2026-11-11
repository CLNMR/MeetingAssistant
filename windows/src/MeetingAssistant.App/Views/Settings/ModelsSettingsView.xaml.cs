using System.Windows.Controls;
using MeetingAssistant.App.ViewModels;

namespace MeetingAssistant.App.Views;

/// Settings › Models (spec 04 §6.5).
public partial class ModelsSettingsView : UserControl
{
    public ModelsSettingsView()
    {
        InitializeComponent();
        Loaded += (_, _) => (DataContext as ModelsSettingsViewModel)?.Refresh();
    }
}
