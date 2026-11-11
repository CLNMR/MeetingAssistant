using System.Globalization;
using MeetingAssistant.Core.Model;
using MeetingAssistant.Core.Storage;

namespace MeetingAssistant.Core.Scribe;

/// <summary>
/// UI rules of the Scribe screens that do not need a UI toolkit (spec 01 "Scribe window, menus and
/// shortcuts", "Recorder screen", "Session detail", "Speakers", "Pauses", "Editing"). The WPF view
/// models call these; the tests pin the numbers and texts.
/// </summary>
public static class ScribeRules
{
    // ---- Recordings panel ----

    /// Sessions shown in the list: not the one being recorded, title contains the trimmed filter (case-insensitive).
    public static List<SessionInfo> VisibleSessions(IEnumerable<SessionInfo> sessions, string? recordingId, string? filter)
    {
        var f = (filter ?? "").Trim();
        return sessions.Where(s => s.Id != recordingId &&
                                   (f.Length == 0 || s.Title.Contains(f, StringComparison.CurrentCultureIgnoreCase)))
                       .ToList();
    }

    /// "No recordings yet." or "Nothing matches.".
    public static string EmptyListText(string? filter) =>
        string.IsNullOrEmpty(filter) ? "No recordings yet." : "Nothing matches.";

    /// Row subtitle date: "d MMM, HH:mm" (e.g. "10 Nov, 14:03").
    public static string RowDate(DateTime startedAtUtc, CultureInfo? culture = null) =>
        startedAtUtc.ToLocalTime().ToString("d MMM, HH:mm", culture ?? CultureInfo.CurrentCulture);

    /// Index of the session one step up (-1) or down (+1) from the selection; no selection gives the
    /// first (down) or last (up) row. -1 when the list is empty.
    public static int MoveSelection(int count, int current, int delta)
    {
        if (count == 0) return -1;
        if (current < 0 || current >= count) return delta > 0 ? 0 : count - 1;
        return Math.Clamp(current + delta, 0, count - 1);
    }

    /// Delete confirmation heading: "Delete “<title>”?".
    public static string DeleteHeading(string title) => $"Delete “{title}”?";

    /// Delete confirmation message; Assist sessions add that Aya keeps its copy.
    public static string DeleteMessage(bool hasAssistRecord) =>
        "The recording, the transcript and the notes on this PC are deleted." + (hasAssistRecord ? " What was saved in Aya stays there." : "");

    // ---- Recorder screen ----

    /// Participants picker: (0, "Detect automatically"), (2, "2 people") … (10, "10 people").
    public static IReadOnlyList<(int Value, string Title)> ParticipantOptions { get; } =
        new[] { (0, "Detect automatically") }.Concat(Enumerable.Range(2, 9).Select(n => (n, $"{n} people"))).ToArray();

    /// Mode hint under the recorder settings.
    public static string ModeHint(MeetingMode mode, bool hasVoiceprint, string myName) => mode switch
    {
        MeetingMode.Call => "Use headphones if you can. With speakers, your mic also hears the other side; MeetingAssistant removes most of that echo, but headphones give the cleanest result.",
        MeetingMode.InPerson => hasVoiceprint
            ? $"Your voice sample is set up, so you will be labelled “{myName}”. For bigger rooms an external conference microphone helps a lot."
            : $"Tip: record a voice sample in Settings so MeetingAssistant can label you as “{myName}”. For bigger rooms an external conference microphone helps a lot.",
        _ => "Remote and in-room speakers are separated on both tracks. Setting the number of participants is not used in this mode.",
    };

    /// Elapsed recording time as published to the UI: in 0.5 s steps (spec 01 "Recording lifecycle" step 6).
    public static double PublishedElapsed(double elapsed) => Math.Floor(elapsed * 2) / 2;

    // ---- Stop Earlier ----

    /// "Last speech": end of the last live line + 2 s, capped at the elapsed time; null without lines.
    public static double? LastSpeech(IReadOnlyList<LiveLine> lines, double elapsed) =>
        lines.Count == 0 ? null : Math.Min(Math.Max(1, elapsed), lines[^1].End + 2);

    /// Quick buttons "−5 min" … "−60 min" shown only when the recording is longer: (label, keep-until).
    public static IReadOnlyList<(string Title, double KeepUntil)> StopEarlierOptions(double elapsed)
    {
        var max = Math.Max(1, elapsed);
        return new[] { 5, 15, 30, 60 }.Where(m => max > m * 60).Select(m => ($"−{m} min", max - m * 60.0)).ToList();
    }

    /// Whether Stop Earlier really trims: `keepUntil &lt; duration − 0.5`.
    public static bool ShouldTrimAfterStop(double keepUntil, double duration) => keepUntil < duration - 0.5;

    /// Parses "1:02:03", "62:03" or "45" into seconds; null when it is not a time.
    public static double? ParseTime(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var parts = s.Split(':');
        if (parts.Length > 3) return null;
        double total = 0;
        foreach (var p in parts)
        {
            if (!double.TryParse(p.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return null;
            total = total * 60 + v;
        }
        return total;
    }

    // ---- Trim / split ----

    /// Trim is allowed when at least 1 s remains and something changes.
    public static bool CanTrim(double start, double end, double duration) =>
        end - start >= SessionEdit.MinimumPart && !(start < 0.5 && end > duration - 0.5);

    /// "End after last speech": last turn end + 2 s, capped at the duration.
    public static double? TrimLastSpeech(Transcript? t, double duration) =>
        t is null || t.Turns.Count == 0 ? null : Math.Min(duration, t.Turns.Max(x => x.End) + Pauses.Margin);

    /// Split needs ≥ 1 s on both sides.
    public static bool CanSplit(double at, double duration) => at >= 1 && duration - at >= 1;

    /// Initial split point: the pause's split point, else the playhead when past 1 s, else the middle.
    public static double InitialSplit(Pause? pause, double playhead, double duration) =>
        pause?.SplitPoint ?? (playhead > 1 ? playhead : duration / 2);

    /// The pause the split uses when the chosen time is within 0.5 s of its split point.
    public static Pause? SplitAtPause(Pause? pause, double at) =>
        pause is { } p && Math.Abs(at - p.SplitPoint) < Pauses.SnapDistance ? p : null;

    /// The "Remove the pause" option is offered when it removes at least 1 s.
    public static bool CanRemovePause(Pause p) => p.SplitPoint - p.KeepUntil >= 1;

    /// Length of part 1 shown in the split sheet.
    public static double SplitPart1(Pause? atPause, bool removePause, double at) =>
        atPause is { } p && removePause && CanRemovePause(p) ? p.KeepUntil : at;

    // ---- Session detail ----

    /// Talk share of a speaker: summed turn durations / all turn durations, rounded %; "" without speech.
    public static string TalkShare(Transcript t, string speakerId)
    {
        var total = t.Turns.Sum(x => x.End - x.Start);
        if (total <= 0) return "";
        var mine = t.Turns.Where(x => x.Speaker == speakerId).Sum(x => x.End - x.Start);
        return $"{(int)Math.Round(mine / total * 100, MidpointRounding.AwayFromZero)}%";
    }

    /// Turns matching the search (text or speaker label, case-insensitive); all turns for an empty search.
    public static List<Turn> FilterTurns(Transcript t, string? search)
    {
        var q = (search ?? "").Trim();
        if (q.Length == 0) return t.Turns.ToList();
        return t.Turns.Where(x => x.Text.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                                  || t.Label(x.Speaker).Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList();
    }

    /// The current line while playing: the last turn with `start ≤ t + 0.05`.
    public static int? CurrentTurn(IReadOnlyList<Turn> turns, double time) =>
        turns.LastOrDefault(x => x.Start <= time + 0.05)?.Id;

    /// Rows of the transcript list: pauses are inserted where each pause begins (none while searching).
    public static List<object> Rows(IReadOnlyList<Turn> turns, IReadOnlyList<Pause> pauses, bool searching)
    {
        if (searching || pauses.Count == 0) return turns.Cast<object>().ToList();
        var output = new List<object>();
        var pending = new Queue<Pause>(pauses.OrderBy(p => p.Start));
        foreach (var turn in turns)
        {
            while (pending.Count > 0 && pending.Peek().Start <= turn.Start) output.Add(pending.Dequeue());
            output.Add(turn);
        }
        while (pending.Count > 0) output.Add(pending.Dequeue());
        return output;
    }

    /// Pause row text: "Pause · 3 min" / "Quiet for the first …" / "Quiet for the last …".
    public static string PauseText(Pause p) => p.Kind switch
    {
        PauseKind.Leading => $"Quiet for the first {Pauses.LengthText(p.Duration)}",
        PauseKind.Trailing => $"Quiet for the last {Pauses.LengthText(p.Duration)}",
        _ => $"Pause · {Pauses.LengthText(p.Duration)}",
    };

    /// Pause row action button: "Split Here…", "Trim Start…", "Trim End…".
    public static string PauseAction(Pause p) => p.Kind switch
    {
        PauseKind.Leading => "Trim Start…",
        PauseKind.Trailing => "Trim End…",
        _ => "Split Here…",
    };

    /// Split hint text: "No conversation for 4 min, from 12:00 to 16:00.".
    public static string SplitHintText(Pause p) =>
        $"No conversation for {Pauses.LengthText(p.Duration)}, from {Exporter.Clock(p.Start)} to {Exporter.Clock(p.End)}.";

    /// The split hint to show: the longest split-suggesting pause unless dismissed for this session.
    public static Pause? SplitHint(IEnumerable<Pause> pauses, string sessionId, ISet<string> dismissed) =>
        dismissed.Contains(sessionId) ? null : Pauses.LongestSplitSuggestion(pauses);

    /// Timeline speech blocks: turns sorted by start, merged when the gap is below max(2 s, duration/600).
    public static List<(double Start, double End)> TimelineBlocks(IEnumerable<Turn> turns, double duration)
    {
        var gap = Math.Max(2, duration / 600);
        var output = new List<(double Start, double End)>();
        foreach (var t in turns.OrderBy(x => x.Start))
        {
            if (output.Count > 0 && t.Start - output[^1].End < gap)
                output[^1] = (output[^1].Start, Math.Max(output[^1].End, t.End));
            else output.Add((t.Start, t.End));
        }
        return output;
    }

    /// Speaker rename: pre-filled name ("" when the label is still "Speaker …").
    public static string RenamePrefill(string label) => label.StartsWith("Speaker ", StringComparison.Ordinal) ? "" : label;

    /// Header date "Tuesday, 10 November 2026, 14:03".
    public static string HeaderDate(DateTime startedAtUtc, CultureInfo? culture = null)
    {
        var c = culture ?? CultureInfo.CurrentCulture;
        var local = startedAtUtc.ToLocalTime();
        return $"{local.ToString("dddd, d MMMM yyyy", c)}, {local.ToString("HH:mm", c)}";
    }

    /// "· N speakers".
    public static string SpeakersText(int count) => $"· {count} speakers";

    /// Id for "New speaker": `manual-` + 6 hex characters.
    public static string NewManualSpeakerId() => "manual-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    /// Empty state of a session without transcript.
    public static string EmptyStateText(SessionStatus status) => status switch
    {
        SessionStatus.Failed => "Transcription failed.",
        SessionStatus.Queued => "Waiting to be transcribed…",
        _ => "Not transcribed yet.",
    };

    // ---- Voices ----

    /// Enrollment button while recording: "Keep reading… (n s)" until 15 s, then "Finish".
    public static string EnrollmentButton(double seconds) =>
        seconds < Pipeline.VoiceprintStore.RecordingSeconds
            ? $"Keep reading… ({(int)Math.Ceiling(Pipeline.VoiceprintStore.RecordingSeconds - seconds)} s)"
            : "Finish";

    /// "from N meeting(s)".
    public static string LibraryCount(int count) => $"from {count} meeting{(count == 1 ? "" : "s")}";

    /// "<n> s recorded <date>".
    public static string VoiceprintText(Voiceprint v, CultureInfo? culture = null) =>
        $"{v.Seconds:0} s recorded {v.CreatedAt.ToLocalTime().ToString("d MMM yyyy", culture ?? CultureInfo.CurrentCulture)}";
}

/// Stable speaker colours (spec 01 "Speaker colours"): "me" = accent, others = 10-colour palette by
/// 64-bit FNV-1a of the id's UTF-8 bytes.
public static class SpeakerPalette
{
    /// Palette order: orange, purple, green, pink, teal, brown, indigo, mint, red, cyan (RGB as on the Mac, light mode).
    public static readonly IReadOnlyList<string> Colors = new[]
    {
        "#FF9500", "#AF52DE", "#34C759", "#FF2D55", "#30B0C7", "#A2845E", "#5856D6", "#00C7BE", "#FF3B30", "#32ADE6",
    };

    /// FNV-1a 64-bit hash of the UTF-8 bytes, with the Mac's offset basis 1469598103934665603
    /// (the standard basis has one more digit); kept identical so colours match across platforms.
    public static ulong Fnv1a(string id)
    {
        ulong h = 1469598103934665603;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(id)) h = (h ^ b) * 1099511628211;
        return h;
    }

    /// Palette index 0…9 of a speaker id.
    public static int Index(string id) => (int)(Fnv1a(id) % (ulong)Colors.Count);

    /// Hex colour for a speaker; null means "the accent colour" (me).
    public static string? Color(string id, bool isMe) => isMe ? null : Colors[Index(id)];
}
