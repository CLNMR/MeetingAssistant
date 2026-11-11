using CommunityToolkit.Mvvm.ComponentModel;
using ObservableObject = CommunityToolkit.Mvvm.ComponentModel.ObservableObject;
using MeetingAssistant.Core.Assist;
using CorePhase = MeetingAssistant.Core.Assist.AssistPhase;

namespace MeetingAssistant.App.ViewModels;

/// The three Assist phases (spec 03 intro).
public enum AssistPhase { Prepare, Running, Review }

/// Assist mode layout and routing (spec 03 §2): the Meetings panel (270 px) plus the router, except
/// while a meeting runs or before Aya is connected, when the router fills the whole area.
/// <see cref="Attach"/> connects it to the Core <see cref="AssistController"/> (App.Assist.cs).
public sealed partial class AssistViewModel : ObservableObject
{
    AssistController? _assist;
    AssistScreens? _screens;

    /// Current phase.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMeetingsPanel), nameof(Router), nameof(IsRunning))]
    AssistPhase _phase = AssistPhase.Prepare;

    /// Session shown in the review (phase Review).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Router))]
    string? _reviewSessionId;

    /// API key stored and all required lists (clients, contacts, templates, meetings) set.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMeetingsPanel), nameof(Router))]
    bool _isConnected;

    /// The Meetings panel on the left (spec 03 §9.1); null until attached.
    [ObservableProperty] AssistMeetingsViewModel? _meetings;

    /// The Core controller (null until attached).
    public AssistController? Controller => _assist;

    /// A meeting is running (rail dot, floating banner).
    public bool IsRunning => Phase == AssistPhase.Running;

    /// Meetings panel visible: not running and connected.
    public bool ShowMeetingsPanel => Phase != AssistPhase.Running && IsConnected && Meetings is not null;

    AssistReviewViewModel? _review;
    AssistLiveViewModel? _live;

    /// What fills the router: Prepare/Connect, Live or Review.
    public ScreenViewModel? Router
    {
        get
        {
            if (_assist is null || _screens is null) return null;
            return Phase switch
            {
                AssistPhase.Running when _assist.Live is { } live =>
                    _live?.Session == live ? _live : (_live = _screens.Live(live)),
                AssistPhase.Review when ReviewSessionId is not null =>
                    _review?.SessionId == ReviewSessionId ? _review : (_review = _screens.Review(ReviewSessionId)),
                _ => IsConnected ? _screens.Prepare : _screens.Connect,
            };
        }
    }

    /// The running meeting's live view model (floating window banner), else null.
    public AssistLiveViewModel? Live => Phase == AssistPhase.Running ? Router as AssistLiveViewModel : null;

    /// Connects the view model to the controller; <paramref name="screens"/> creates the screen view models.
    public void Attach(AssistController assist, AssistScreens screens)
    {
        _assist = assist;
        _screens = screens;
        Meetings = screens.Meetings;
        AssistUiHelpers.Observe(assist, p => { if (p is nameof(AssistController.Phase) or nameof(AssistController.Live)) Sync(); });
        AssistUiHelpers.Observe(assist.Aya, p => { if (p is nameof(AssistConnection.HasApiKey) or nameof(AssistConnection.Config)) Sync(); });
        Sync();
        OnPropertyChanged(nameof(ShowMeetingsPanel));
    }

    void Sync()
    {
        if (_assist is null) return;
        IsConnected = _assist.Aya.IsConnected;
        ReviewSessionId = _assist.ReviewSessionId;
        Phase = _assist.Phase switch
        {
            CorePhase.Running => AssistPhase.Running,
            CorePhase.Review => AssistPhase.Review,
            _ => AssistPhase.Prepare,
        };
        if (Phase != AssistPhase.Running) _live = null;
        OnPropertyChanged(nameof(Router));
        OnPropertyChanged(nameof(Live));
    }

    /// Opens the review of a session.
    public void OpenReview(string sessionId) => _assist?.OpenReview(sessionId);
}

/// Creates the Assist screen view models with the app's services (App.Assist.cs).
public sealed class AssistScreens(
    AssistConnectViewModel connect,
    AssistPrepareViewModel prepare,
    AssistMeetingsViewModel meetings,
    Func<AssistLiveSession, AssistLiveViewModel> live,
    Func<string, AssistReviewViewModel> review)
{
    public AssistConnectViewModel Connect { get; } = connect;
    public AssistPrepareViewModel Prepare { get; } = prepare;
    public AssistMeetingsViewModel Meetings { get; } = meetings;
    public AssistLiveViewModel Live(AssistLiveSession s) => live(s);
    public AssistReviewViewModel Review(string id) => review(id);
}
