using MeetingAssistant.Core.Model;

namespace MeetingAssistant.Core.Pipeline;

/// A diarized segment of one track with its global speaker id (e.g. `mic-1`).
public readonly record struct SpeakerSegment(string Speaker, TimeRange Span);

/// Words of one track plus who spoke when on that track.
/// `Segments` null or empty means the whole track is `FixedSpeaker` (e.g. `system-0`).
public sealed record TrackResult(Track Track, IReadOnlyList<Word> Words, IReadOnlyList<SpeakerSegment>? Segments, string? FixedSpeaker);

/// Word → speaker assignment, echo removal and turns (spec 01-recording, "Word → speaker
/// assignment", "Echo removal", "Turns").
public static class Merger
{
    /// Words are widened to at least this many seconds when measuring overlap.
    public const double MinWordSpan = 0.02;
    /// Segments within this tolerance (s) of the word are considered.
    public const double SegmentTolerance = 0.01;
    /// A word in a gap goes to the nearest segment when its midpoint is closer than this (s).
    public const double NearestSegmentMax = 1.5;
    /// A single flipped word is folded back when its neighbours are less than this apart (s).
    public const double SingletonMaxGap = 1.0;
    /// Default gap (s) that starts a new turn of the same speaker.
    public const double TurnMaxGap = 1.5;

    /// Assign each word to the segment with the most overlap; words in gaps go to the nearest
    /// segment (within 1.5 s), else the previous word's speaker, else the first segment's.
    public static List<(Word Word, string Speaker)> AssignSpeakers(TrackResult r)
    {
        if (r.Segments is not { Count: > 0 } raw)
            return r.Words.Select(w => (w, r.FixedSpeaker ?? "unknown")).ToList();
        var segs = raw.OrderBy(s => s.Span.Start).ToList();
        var output = new List<(Word, string)>(r.Words.Count);
        string? previous = null;
        foreach (var w in r.Words)
        {
            var ws = new TimeRange(w.Start, Math.Max(w.End, w.Start + MinWordSpan));
            string? bestSpeaker = null;
            double bestOverlap = -1;
            foreach (var s in segs)
            {
                if (!(s.Span.End > ws.Start - SegmentTolerance && s.Span.Start < ws.End + SegmentTolerance)) continue;
                var o = s.Span.Overlap(ws);
                if (o > bestOverlap) { bestSpeaker = s.Speaker; bestOverlap = o; }
            }
            if (bestSpeaker is null || bestOverlap <= 0)
            {
                double mid = (ws.Start + ws.End) / 2;
                SpeakerSegment? nearest = null;
                double nearestDist = double.MaxValue;
                foreach (var s in segs)
                {
                    var d = Dist(mid, s.Span);
                    if (d < nearestDist) { nearestDist = d; nearest = s; }
                }
                if (nearest is { } n && nearestDist < NearestSegmentMax) bestSpeaker = n.Speaker;
            }
            var spk = bestSpeaker ?? previous ?? segs[0].Speaker;
            output.Add((w, spk));
            previous = spk;
        }
        return SmoothSingletons(output);
    }

    static double Dist(double t, TimeRange s) => t < s.Start ? s.Start - t : (t > s.End ? t - s.End : 0);

    /// A single word whose two neighbours share another speaker moves to that speaker when the
    /// neighbours are less than 1 s apart (boundary errors).
    public static List<(Word Word, string Speaker)> SmoothSingletons(List<(Word Word, string Speaker)> a)
    {
        if (a.Count < 3) return a;
        var r = a.ToList();
        for (int i = 1; i < a.Count - 1; i++)
        {
            if (a[i - 1].Speaker == a[i + 1].Speaker && a[i].Speaker != a[i - 1].Speaker
                && a[i + 1].Word.Start - a[i - 1].Word.End < SingletonMaxGap)
                r[i] = (a[i].Word, a[i - 1].Speaker);
        }
        return r;
    }

    /// Drop mic words that echo the system track (user on loudspeakers). Output is sorted by start.
    public static List<(Word Word, string Speaker)> RemoveEcho(IReadOnlyList<(Word Word, string Speaker)> mic, IReadOnlyList<Word> system)
    {
        var order = Enumerable.Range(0, mic.Count).OrderBy(i => mic[i].Word.Start).ToList();
        var mask = EchoMask(order.Select(i => mic[i].Word).ToList(), system);
        var kept = new List<(Word, string)>();
        for (int k = 0; k < order.Count; k++) if (!mask[k]) kept.Add(mic[order[k]]);
        return kept;
    }

    /// A system word may start this long (s) before a matching mic word.
    public const double EchoBefore = 2.0;
    /// … or this long after it.
    public const double EchoAfter = 1.0;
    /// Two matches closer than this (s, end to start) mark the 1–2 words between them as echo.
    public const double EchoGarbledGap = 2.0;

    /// For mic words sorted by start: true where the word echoes the system track. A mic word
    /// matches a similar system word starting between 2 s before and 1 s after it; 1–2 words
    /// between two matches (≤ 3 positions apart, &lt; 2 s apart) are garbled echo; a run with
    /// fewer than 2 real matches whose first word is shorter than 4 characters is kept.
    public static bool[] EchoMask(IReadOnlyList<Word> mic, IReadOnlyList<Word> system)
    {
        var echo = new bool[mic.Count];
        if (mic.Count == 0 || system.Count == 0) return echo;
        var sys = system.OrderBy(w => w.Start).ToList();
        var sysStarts = sys.Select(w => w.Start).ToArray();
        var sysNorm = sys.Select(w => WordAlignment.Norm(w.W)).ToArray();
        int LowerBound(double t)
        {
            int lo = 0, hi = sysStarts.Length;
            while (lo < hi) { int mid = (lo + hi) / 2; if (sysStarts[mid] < t) lo = mid + 1; else hi = mid; }
            return lo;
        }
        var norms = mic.Select(w => WordAlignment.Norm(w.W)).ToArray();
        var matched = new bool[mic.Count];
        for (int i = 0; i < mic.Count; i++)
        {
            if (norms[i].Length == 0) continue;
            for (int j = LowerBound(mic[i].Start - EchoBefore); j < sys.Count && sysStarts[j] < mic[i].Start + EchoAfter; j++)
                if (Similar(norms[i], sysNorm[j])) { matched[i] = true; break; }
        }
        Array.Copy(matched, echo, matched.Length);
        var hits = Enumerable.Range(0, mic.Count).Where(i => matched[i]).ToList();
        for (int h = 0; h + 1 < hits.Count; h++)
        {
            int a = hits[h], b = hits[h + 1];
            if (b - a > 1 && b - a <= 3 && mic[b].Start - mic[a].End < EchoGarbledGap)
                for (int k = a + 1; k < b; k++) echo[k] = true;
        }
        int x = 0;
        while (x < echo.Length)
        {
            if (!echo[x]) { x++; continue; }
            int y = x;
            while (y + 1 < echo.Length && echo[y + 1]) y++;
            int count = 0;
            for (int k = x; k <= y; k++) if (matched[k]) count++;
            if (count < 2 && norms[x].Length < 4) for (int k = x; k <= y; k++) echo[k] = false;
            x = y + 1;
        }
        return echo;
    }

    /// Equal, or (both ≥ 4 characters, length difference ≤ ¼ of the longer) Levenshtein
    /// distance ≤ max(1, longer/4).
    public static bool Similar(string a, string b)
    {
        if (a == b) return true;
        int longest = Math.Max(a.Length, b.Length);
        if (Math.Min(a.Length, b.Length) < 4 || Math.Abs(a.Length - b.Length) > longest / 4) return false;
        int limit = Math.Max(1, longest / 4);
        var prev = Enumerable.Range(0, b.Length + 1).ToArray();
        for (int i = 0; i < a.Length; i++)
        {
            var cur = new int[b.Length + 1];
            cur[0] = i + 1;
            for (int j = 0; j < b.Length; j++)
                cur[j + 1] = Math.Min(Math.Min(prev[j + 1] + 1, cur[j] + 1), prev[j] + (a[i] == b[j] ? 0 : 1));
            if (cur.Min() > limit) return false;
            prev = cur;
        }
        return prev[b.Length] <= limit;
    }

    /// Build turns from speaker-tagged words of all tracks: per speaker, a new turn starts when
    /// the gap to that speaker's previous word is ≥ `maxGap`; turns are sorted by start and
    /// numbered 0…n-1. A turn's track is the track of its first word.
    public static List<Turn> Turns(IEnumerable<(Word Word, string Speaker, Track Track)> tagged, double maxGap = TurnMaxGap)
    {
        var runs = new List<Turn>();
        foreach (var group in tagged.GroupBy(t => t.Speaker))
        {
            Turn? current = null;
            foreach (var (w, spk, track) in group.OrderBy(t => t.Word.Start))
            {
                if (current is not null && w.Start - current.End < maxGap)
                {
                    current.Words.Add(w);
                    current.End = Math.Max(current.End, w.End);
                    current.Text += " " + w.W;
                }
                else
                {
                    if (current is not null) runs.Add(current);
                    current = new Turn { Id = 0, Speaker = spk, Start = w.Start, End = w.End, Text = w.W, Track = track, Words = new() { w } };
                }
            }
            if (current is not null) runs.Add(current);
        }
        runs = runs.OrderBy(t => t.Start).ToList();   // stable, like a sort by start
        for (int i = 0; i < runs.Count; i++) runs[i].Id = i;
        return runs;
    }
}
