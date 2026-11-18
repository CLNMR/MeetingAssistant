using MeetingAssistant.App.Services;
using MeetingAssistant.App.ViewModels;
using MeetingAssistant.Core.Assist;

namespace MeetingAssistant.App;

/// The global meeting shortcut (spec 04 §3.4, default Ctrl+Alt+Win+R) and the first-open question
/// "Open MeetingAssistant at startup?" (spec 04 §3.5).
public partial class App
{
    bool _meetingShortcutBusy;

    /// Stops the running recording, or starts a meeting: in Assist when Assist is the current mode and
    /// ready, otherwise a Scribe recording. The app always comes to the front.
    async Task MeetingShortcutPressedAsync()
    {
        if (_meetingShortcutBusy) return;
        _meetingShortcutBusy = true;
        try
        {
            ShowMain();
            if (Scribe.IsRecording) { await Scribe.StopRecordingAsync(); return; }
            if (Shell.Mode == AppMode.Assist && Assist is { } assist)
            {
                if (assist.Phase is Core.Assist.AssistPhase.Review) assist.FinishReview();
                if (assist.CanStart) { await assist.StartAsync(); return; }
            }
            Shell.Mode = AppMode.Scribe;
            Shell.Scribe.ShowRecorderCommand.Execute(null);
            await Scribe.StartRecordingAsync();
        }
        catch (Exception e)
        {
            Shell.ErrorMessage = e.Message;
        }
        finally
        {
            _meetingShortcutBusy = false;
        }
    }

    /// Hooks the meeting shortcut up to the dictation's keyboard hook (which owns all global shortcuts).
    void ConnectMeetingShortcut()
    {
        if (Dictation is null) return;
        Dictation.MeetingShortcutPressed += () => _ = MeetingShortcutPressedAsync();
    }

    /// The first time the main window opens: ask whether to open at login (either answer is remembered).
    void AskLaunchAtLogin(string[] args)
    {
        if (!LaunchAtLogin.ShouldAsk(args)) return;
        var choice = ShowConfirm(LaunchAtLogin.Question, LaunchAtLogin.QuestionMessage,
            new AlertButton("Open at Startup", AlertButtonKind.Primary, IsDefault: true),
            new AlertButton("Not Now", IsCancel: true));
        LaunchAtLogin.Shared.Answer(choice == 0);
    }
}
