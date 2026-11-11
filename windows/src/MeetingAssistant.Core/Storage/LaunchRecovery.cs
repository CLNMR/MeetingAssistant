using MeetingAssistant.Core.Model;

namespace MeetingAssistant.Core.Storage;


/// Launch recovery (spec 01 "Crash recovery at launch"): sessions still `recording` become
/// `recorded` with their length taken from the audio files; sessions `queued` or `transcribing`
/// are handed to `enqueue` (the speech queue, which uses the engine preference).
/// Returns the ids queued again. The queue itself is `MeetingAssistant.Speech.TranscriptionQueue`.
public static class LaunchRecovery
{
    public static List<string> Run(Action<string> enqueue, Action<string, SessionStatus>? statusChanged = null)
    {
        var requeued = new List<string>();
        foreach (var s in SessionStore.List())
        {
            if (s.Status == SessionStatus.Recording)
            {
                SessionStore.Save(SessionStore.RecoverInterrupted(s));
                statusChanged?.Invoke(s.Id, SessionStatus.Recorded);
            }
            else if (s.Status is SessionStatus.Queued or SessionStatus.Transcribing)
            {
                SessionStore.SetStatus(s.Id, SessionStatus.Recorded);
                enqueue(s.Id);
                requeued.Add(s.Id);
            }
        }
        return requeued;
    }
}

/// UI facts about session statuses (spec 01 statuses table).
public static class SessionStatusInfo
{
    /// Being recorded, waiting or being transcribed: no trim, split, delete or "Transcribe Again".
    public static bool IsBusy(this SessionStatus s) => SessionEdit.IsBusy(s);

    /// Waiting or transcribing (detail view shows progress and Cancel).
    public static bool IsProcessing(this SessionStatus s) => s is SessionStatus.Queued or SessionStatus.Transcribing;

    /// Empty-state text of the detail view when there is no transcript.
    public static string EmptyStateText(this SessionStatus s) => s switch
    {
        SessionStatus.Queued => "Waiting to be transcribed…",
        SessionStatus.Failed => "Transcription failed.",
        _ => "Not transcribed yet.",
    };
}
