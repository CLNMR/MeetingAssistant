using MeetingAssistant.Core.Model;

namespace MeetingAssistant.Core.Storage;

/// Where a pause lies in the recording.
public enum PauseKind { Leading, Inner, Trailing }

/// <summary>A stretch of a recording in which nobody said anything substantial (spec 01 "Pauses").</summary>
public readonly record struct Pause(double Start, double End, PauseKind Kind)
{
    public double Duration => End - Start;
    /// Long enough that two separate meetings probably share the recording (inner, ≥ 180 s).
    public bool SuggestsSplit => Kind == PauseKind.Inner && Duration >= Pauses.MeetingBreak;
    /// Where to split: shortly before speech resumes.
    public double SplitPoint => Math.Max(Start, End - Pauses.Margin);
    /// Where part 1 should end when the pause itself is removed.
    public double KeepUntil => Math.Min(End, Start + Pauses.Margin);
}

/// <summary>
/// Finds long pauses from the transcript's speech timings (spec 01 "Pauses and splitting at
/// pauses"). Turns of at most 3 words are ignored, so stray words in a break do not end it.
/// </summary>
public static class Pauses
{
    /// Shortest pause that is marked (s).
    public const double Minimum = 60;
    /// Pauses from this length on are offered as a split between two meetings (s).
    public const double MeetingBreak = 180;
    /// Seconds of the pause kept on each side when cutting.
    public const double Margin = 2;
    /// Turns with at most this many words are ignored.
    public const int MinorWords = 3;
    /// A split at a chosen time within this distance of a pause's split point counts as a split at that pause (s).
    public const double SnapDistance = 0.5;

    /// Leading (only ≥ 180 s), inner (≥ 60 s) and trailing (≥ 60 s) pauses.
    public static List<Pause> Find(Transcript transcript, double duration, double minimum = Minimum)
    {
        var speech = transcript.Turns.Where(t => !IsMinor(t)).Select(t => (t.Start, t.End)).OrderBy(s => s.Start).ToList();
        var output = new List<Pause>();
        if (speech.Count == 0)
        {
            if (duration >= Math.Max(minimum, MeetingBreak)) output.Add(new Pause(0, duration, PauseKind.Leading));
            return output;
        }
        // A quiet start is usually greetings and waiting for people, so only a long one is marked.
        if (speech[0].Start >= Math.Max(minimum, MeetingBreak)) output.Add(new Pause(0, speech[0].Start, PauseKind.Leading));
        var lastEnd = speech[0].End;
        foreach (var s in speech.Skip(1))
        {
            if (s.Start - lastEnd >= minimum) output.Add(new Pause(lastEnd, s.Start, PauseKind.Inner));
            lastEnd = Math.Max(lastEnd, s.End);
        }
        if (duration - lastEnd >= minimum) output.Add(new Pause(lastEnd, duration, PauseKind.Trailing));
        return output;
    }

    /// The longest pause that suggests a split (for the split hint banner), or null.
    public static Pause? LongestSplitSuggestion(IEnumerable<Pause> pauses) =>
        pauses.Where(p => p.SuggestsSplit).OrderByDescending(p => p.Duration).Cast<Pause?>().FirstOrDefault();

    /// Turns of at most <see cref="MinorWords"/> words (counted from the words, or the text when there are none).
    public static bool IsMinor(Turn t)
    {
        var words = t.Words.Count == 0 ? t.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length : t.Words.Count;
        return words <= MinorWords;
    }

    /// Length text for a pause: "&lt;n&gt; s" below 120 s, else "&lt;n&gt; min" (both rounded).
    public static string LengthText(double seconds) =>
        seconds < 120 ? $"{(long)Math.Round(seconds, MidpointRounding.AwayFromZero)} s"
                      : $"{(long)Math.Round(seconds / 60, MidpointRounding.AwayFromZero)} min";
}
