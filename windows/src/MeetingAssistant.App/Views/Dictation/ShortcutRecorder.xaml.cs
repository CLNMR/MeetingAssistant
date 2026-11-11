using System.Windows.Controls;

namespace MeetingAssistant.App.Views.Dictation;

/// Records a dictation shortcut (spec 04 §3.4); bound to a <see cref="App.Dictation.ShortcutSlotViewModel"/>.
public partial class ShortcutRecorder : UserControl
{
    public ShortcutRecorder() => InitializeComponent();
}
