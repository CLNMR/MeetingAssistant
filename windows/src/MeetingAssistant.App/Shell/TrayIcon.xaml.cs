using H.NotifyIcon;

namespace MeetingAssistant.App.Shell;

/// The notification-area icon and its menu (spec 01 "Menu-bar (tray) menu", 04 §6.7). DataContext: ShellViewModel.
/// The dictation phase fills <c>UnfinishedSlot</c> with the "Unfinished Dictations (N)" submenu.
public partial class TrayIcon : TaskbarIcon
{
    public TrayIcon() => InitializeComponent();
}
