using System.Globalization;
using MeetingAssistant.Core.Model;
using MeetingAssistant.Core.Pipeline;

namespace MeetingAssistant.Core.Scribe;

/// One stretch of live transcript with its own time origin (spec 01 "Floating live transcript
/// window"): a listening segment (rotated every ~10 minutes) or a recording.
public sealed class TranscriptSegment(DateTime start, MeetingMode mode, bool isRecording)
{
    /// Wall-clock start (line times are seconds from here).
    public DateTime Start { get; } = start;
    public MeetingMode Mode { get; } = mode;
    /// The segment belongs to a recording (paragraphs get the red ⏺ marker).
    public bool IsRecording { get; } = isRecording;
    /// The latest full list of lines from the live transcriber.
    public IReadOnlyList<LiveLine> Lines { get; set; } = Array.Empty<LiveLine>();
    /// The transcriber still runs (finished segments can be dropped).
    public bool IsActive { get; set; } = true;
}

/// A paragraph of the floating window: consecutive lines of one speaker.
public sealed record PanelParagraph(string Id, DateTime Date, string Speaker, string ColorKey, bool IsMe,
                                    string FinalText, string PendingText, bool Recorded);

/// Paragraph building, rotation and trimming of the floating live transcript (spec 01 "Floating live
/// transcript window").
public static class FloatingTranscript
{
    /// A segment is rotated after 10 minutes…
    public static readonly TimeSpan SegmentLength = TimeSpan.FromMinutes(10);
    /// …when the last line ended more than 3 s ago, or at the latest after 12 minutes.
    public const double QuietBeforeRotate = 3;
    public static readonly TimeSpan RotateAnyway = TimeSpan.FromMinutes(12);
    /// Lines of a speaker join a paragraph that started less than 120 s earlier.
    public const double ParagraphSpan = 120;
    /// Finished segments are dropped while the total exceeds 4000 lines.
    public const int MaxLines = 4000;
    /// Tick of the rotation and level timer.
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(0.25);
    /// UI updates are coalesced.
    public static readonly TimeSpan Coalesce = TimeSpan.FromSeconds(0.4);

    /// Whether the current segment should be replaced by a fresh transcriber.
    public static bool ShouldRotate(TranscriptSegment seg, DateTime now)
    {
        var age = (now - seg.Start).TotalSeconds;
        var lastEnd = seg.Lines.Count > 0 ? seg.Lines[^1].End : 0;
        return age > SegmentLength.TotalSeconds && (age - lastEnd > QuietBeforeRotate || age > RotateAnyway.TotalSeconds);
    }

    /// Drops the oldest finished segments while all segments together hold more than <see cref="MaxLines"/> lines.
    public static void Trim(List<TranscriptSegment> segments, TranscriptSegment? current)
    {
        var total = segments.Sum(s => s.Lines.Count);
        while (total > MaxLines && segments.Count > 0 && segments[0] != current && !segments[0].IsActive)
        {
            total -= segments[0].Lines.Count;
            segments.RemoveAt(0);
        }
    }

    /// Builds the paragraphs of all segments; lines starting before <paramref name="hiddenBefore"/> are left out (Clear).
    public static List<PanelParagraph> Build(IReadOnlyList<TranscriptSegment> segments, string myName, DateTime hiddenBefore)
    {
        var output = new List<PanelParagraph>();
        for (int si = 0; si < segments.Count; si++)
        {
            var seg = segments[si];
            var labels = new LiveSpeakerLabels(seg.Mode, myName);
            foreach (var l in seg.Lines)
            {
                var date = seg.Start.AddSeconds(l.Start);
                if (date < hiddenBefore) continue;
                var key = l.Speaker ?? "?";
                var name = labels.Label(l.Speaker);
                var colorKey = $"{si}-{key}";
                if (output.Count > 0 && output[^1] is var last && last.Speaker == name && last.ColorKey == colorKey
                    && (date - last.Date).TotalSeconds < ParagraphSpan)
                {
                    output[^1] = l.IsFinal && last.PendingText.Length == 0
                        ? last with { FinalText = last.FinalText + " " + l.Text }
                        : last with { PendingText = last.PendingText + (last.PendingText.Length == 0 ? "" : " ") + l.Text };
                }
                else
                {
                    var stamp = (seg.Start.ToUniversalTime() - DateTime.UnixEpoch).TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
                    output.Add(new PanelParagraph($"{stamp}-{l.Id}", date, name, colorKey, key == "me",
                                                  l.IsFinal ? l.Text : "", l.IsFinal ? "" : l.Text, seg.IsRecording));
                }
            }
        }
        return output;
    }

    /// Copy Transcript: one line per paragraph, `[HH:mm:ss] Speaker: final pending` (trimmed).
    public static string PlainText(IEnumerable<PanelParagraph> paragraphs) =>
        string.Join("\n", paragraphs.Select(p =>
            $"[{p.Date.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}] {p.Speaker}: {p.FinalText} {p.PendingText}".Trim()));
}
