using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using MeetingAssistant.App.ViewModels;
using MeetingAssistant.Core.Assist;

namespace MeetingAssistant.App.Views;

/// Spec 03 §8 · Review screen. Opens the person popovers (speakers with rename, attendees without).
public partial class AssistReviewView : UserControl
{
    public AssistReviewView()
    {
        InitializeComponent();
        AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(ButtonClicked));
    }

    void ButtonClicked(object sender, RoutedEventArgs e)
    {
        if (DataContext is not AssistReviewViewModel vm || e.OriginalSource is not Button b) return;
        switch (b.Tag as string, b.DataContext)
        {
            case ("speaker", ReviewSpeakerRow row):
                PersonPopup.Show(b, vm.PersonFor(row), PlacementMode.Right);
                break;
            case ("person", AttendeeChip chip):
                PersonPopup.Show(b, vm.PersonFor(chip), PlacementMode.Bottom);
                break;
        }
    }
}
