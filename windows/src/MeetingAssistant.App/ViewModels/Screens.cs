using CommunityToolkit.Mvvm.ComponentModel;

namespace MeetingAssistant.App.ViewModels;

/// Base of every feature screen's view model. Each one maps to a view through a DataTemplate in
/// Views/ViewTemplates.xaml, so navigation is "set a ContentControl's Content to a view model".
public abstract class ScreenViewModel : ObservableObject
{
    /// The screen's name in the spec (for placeholders and diagnostics).
    public abstract string ScreenName { get; }
}

// Scribe screens (RecorderViewModel, SessionDetailViewModel) live in ViewModels/Scribe/.

// The Assist screens (Connect, Prepare, Live, Review, Meetings, Import Recording, Person) live in ViewModels/Assist/.
