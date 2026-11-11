using System.ComponentModel;
using MeetingAssistant.App.Services;
using MeetingAssistant.App.ViewModels;
using MeetingAssistant.App.Views;
using MeetingAssistant.Core;
using MeetingAssistant.Core.Assist;
using MeetingAssistant.Core.Aya;
using MeetingAssistant.Core.Model;
using MeetingAssistant.Core.Storage;
using WinShell = MeetingAssistant.Platform.Windows.SystemServices.Shell;

namespace MeetingAssistant.App;

/// The Assist area (spec 03): the Core <see cref="AssistController"/> with the Aya key from the credential store,
/// the app's <see cref="IAssistHost"/> on top of Scribe's recording controller, the screens, the rail logo link,
/// "Suggest now", "Assist Recap" from Scribe, and the recap draft flush on quit.
public partial class App
{
    /// Assist mode's controller (also found by the dictation for its Claude binary and Aya names).
    public AssistController Assist { get; private set; } = null!;

    AppAssistHost? _assistHost;

    partial void StartAssist()
    {
        var host = _assistHost = new AppAssistHost(this);
        var assist = Assist = new AssistController(host, Services.Secrets);
        var shell = Shell;

        void ShowTranscript(string id)
        {
            shell.Mode = AppMode.Scribe;
            shell.Scribe.Select(id);
        }

        var meetings = new AssistMeetingsViewModel(assist, host, ShowTranscript,
            id => WinShell.ShowInExplorer(AppPaths.Session(id)), () => ImportRecordingView.Show(assist));
        var screens = new AssistScreens(
            new AssistConnectViewModel(assist),
            new AssistPrepareViewModel(assist),
            meetings,
            live => new AssistLiveViewModel(assist, live, UiState, () => (Scribe.Elapsed, Scribe.MicLevel, Scribe.SystemLevel)),
            id => new AssistReviewViewModel(assist, host, id, ShowTranscript));
        shell.Assist.Attach(assist, screens);

        // Rail logo: the meetings list in Aya, else the organization.
        void SyncLogo()
        {
            var cfg = assist.Aya.Config;
            shell.AyaUrlIsMeetings = cfg.WebUrl(AyaSlot.Meetings) is not null;
            shell.AyaUrl = cfg.LogoUrl;
        }
        SyncLogo();
        AssistUiHelpers.Observe(assist.Aya, p => { if (p == nameof(AssistConnection.Config)) SyncLogo(); });

        // Recording state and session list changes.
        Scribe.PropertyChanged += (_, e) => AppServices.Ui(() =>
        {
            if (e.PropertyName == nameof(ScribeController.IsRecording)) assist.RecordingStateChanged();
            if (e.PropertyName is nameof(ScribeController.Sessions) or nameof(ScribeController.IsRecording)) meetings.Refresh();
        });
        Scribe.ProgressChanged += _ => AppServices.Ui(meetings.Refresh);

        // Ctrl+Shift+Space in the live view; "Assist Recap" in Scribe's session detail.
        shell.SuggestNowRequested += () => _ = assist.Live?.SuggestNowAsync();
        ScribeController.AssistReviewRequested += id => AppServices.Ui(() => assist.OpenReview(id));

        // The pending recap draft is written before the app goes away. A running meeting was just
        // stopped by Scribe's quit handler (registered first): give it a moment to stop Claude and save
        // its notes into `assist.json`, which happens in the background.
        Quitting += () =>
        {
            WaitForLiveMeeting(assist, TimeSpan.FromSeconds(5));
            assist.OnQuit();
        };

        _ = assist.StartupAsync();
    }

    partial void StopAssist() => Assist?.OnQuit();

    /// Runs the dispatcher until the running Assist meeting has finished (notes saved) or the timeout passed.
    static void WaitForLiveMeeting(AssistController assist, TimeSpan timeout)
    {
        if (assist.Live is not { } live) return;
        var frame = new System.Windows.Threading.DispatcherFrame();
        live.Finished += _ => frame.Continue = false;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = timeout };
        timer.Tick += (_, _) => frame.Continue = false;
        timer.Start();
        if (assist.Live is not null) System.Windows.Threading.Dispatcher.PushFrame(frame);
        timer.Stop();
    }
}

/// <summary>
/// <see cref="IAssistHost"/> on top of Scribe's <see cref="ScribeController"/>: recording with the Assist hooks,
/// sessions, progress, import, deletion and the speaker library.
/// </summary>
sealed class AppAssistHost(App app) : IAssistHost
{
    ScribeController Scribe => app.Scribe;

    public string MyName => app.Services.Preferences.MyName;
    public bool IsRecording => Scribe.IsRecording;

    public async Task<(string? SessionId, string? Error)> StartRecordingAsync(MeetingMode mode, string title,
        Action<IReadOnlyList<LiveLine>> liveLines, Action<string> stopped) =>
        await Scribe.StartRecordingAsync(mode, title, liveLines, stopped);

    public Task StopRecordingAsync() => Scribe.StopRecordingAsync();
    public SessionInfo? Session(string id) => Scribe.Session(id);
    public double? Progress(string id) => Scribe.ProgressOf(id)?.Fraction;
    public IReadOnlyList<SessionInfo> Sessions => Scribe.Sessions;
    public void ReloadSessions() => Scribe.Reload();
    public void Enqueue(string id) => Scribe.Enqueue(id);
    public void DeleteSession(string id) => Scribe.Delete(id);

    public Task<SessionInfo> ImportFilesAsync(string mic, string? system, MeetingMode mode, string title, DateTime startedAt) =>
        Task.Run(() => SessionImport.ImportFiles(mic, system, mode, title, startedAt: startedAt));

    public void LearnSpeaker(string name, float[] embedding) => Scribe.LearnSpeaker(name, embedding);

    public void ShowAssist() => app.Shell.Mode = AppMode.Assist;
}
