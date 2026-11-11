using System.Windows.Controls;
using MeetingAssistant.App.ViewModels;

namespace MeetingAssistant.App.Views;

/// Settings › General (spec 04 §6.1).
public partial class GeneralSettingsView : UserControl
{
    public GeneralSettingsView()
    {
        InitializeComponent();
        Loaded += (_, _) => (DataContext as GeneralSettingsViewModel)?.RefreshDevicesCommand.Execute(null);
    }

    void Devices_DropDownOpened(object? sender, EventArgs e) => (DataContext as GeneralSettingsViewModel)?.RefreshDevicesCommand.Execute(null);
}
