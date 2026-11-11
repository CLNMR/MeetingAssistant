using System.ComponentModel;
using System.Runtime.CompilerServices;
using MeetingAssistant.Core.Model;

namespace MeetingAssistant.Core.Assist;

/// <summary>Progress of one step (an Aya or Claude call) for the UI: idle, working(text), done(text?), failed(text).</summary>
public abstract record StepState
{
    public sealed record Idle : StepState;
    public sealed record Working(string Message) : StepState;
    public sealed record Done(string? Message = null) : StepState;
    public sealed record Failed(string Message) : StepState;

    public bool IsWorking => this is Working;
    /// The text to show (null for idle and a silent done).
    public string? Text => this switch { Working w => w.Message, Done d => d.Message, Failed f => f.Message, _ => null };

    public static readonly StepState IdleState = new Idle();
}

/// <summary>Base class for the controllers: <see cref="INotifyPropertyChanged"/> with a setter helper.</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// What the Assist controllers need from the rest of the app (recording, sessions, preferences, speaker
/// library). The WPF app implements it on top of its recording and storage code; tests use fakes.
/// </summary>
public interface IAssistHost
{
    /// The user's name from the preferences ("" when not set).
    string MyName { get; }

    /// A recording is running.
    bool IsRecording { get; }

    /// <summary>
    /// Starts a recording for Assist (spec 03 §5 start flow step 3): sets mode and title, forces live
    /// transcription on, forwards finished live lines to <paramref name="liveLines"/> and calls
    /// <paramref name="stopped"/> once with the session id (empty on failure) when the recording ends.
    /// The session must be queued for final transcription whatever the auto-transcribe preference says.
    /// Returns the session id, or the error text when it did not start.
    /// </summary>
    Task<(string? SessionId, string? Error)> StartRecordingAsync(MeetingMode mode, string title,
                                                                 Action<IReadOnlyList<LiveLine>> liveLines, Action<string> stopped);

    /// Stops the running recording (the stopped callback then fires).
    Task StopRecordingAsync();

    /// A session's info, or null when it is gone.
    SessionInfo? Session(string id);

    /// Transcription progress 0…1 of a session, when it is being transcribed.
    double? Progress(string id);

    /// All sessions, newest first.
    IReadOnlyList<SessionInfo> Sessions { get; }

    /// Re-reads the sessions from disk.
    void ReloadSessions();

    /// Queues a session for final transcription.
    void Enqueue(string id);

    /// Deletes a session folder.
    void DeleteSession(string id);

    /// Copies one or two audio files in as a new session (file 1 = mic, file 2 = system).
    Task<SessionInfo> ImportFilesAsync(string mic, string? system, MeetingMode mode, string title, DateTime startedAt);

    /// Teaches the speaker library a name for a voice (review rename).
    void LearnSpeaker(string name, float[] embedding);

    /// Switches the app to Assist mode (opening a review always does).
    void ShowAssist();
}
