namespace MeetingAssistant.Core.Pipeline;

/// A time range in seconds (the Mac's `TimeSpan`; renamed to avoid System.TimeSpan).
public readonly record struct TimeRange(double Start, double End)
{
    public double Duration => End - Start;

    /// Seconds both ranges share (0 when they do not touch).
    public double Overlap(TimeRange o) => Math.Max(0, Math.Min(End, o.End) - Math.Max(Start, o.Start));
}

/// Merges VAD speech regions into ASR chunks (spec 01-recording, "Chunking").
public static class Chunking
{
    /// Default gap (s) under which neighbouring regions are joined.
    public const double MaxGap = 1.2;
    /// Default padding (s) added on each side of a region.
    public const double Padding = 0.15;
    /// Regions this short (s) or shorter are ignored.
    public const double MinRegion = 0.05;

    /// Neighbours closer than `maxGap` are joined while the chunk stays ≤ `maxLength`, so the
    /// model sees whole sentences. Chunks longer than `maxLength` are split into
    /// ceil(len/max) equal parts.
    public static List<TimeRange> Chunks(IEnumerable<TimeRange> regions, double maxLength, double totalDuration,
                                         double maxGap = MaxGap, double padding = Padding)
    {
        var output = new List<TimeRange>();
        foreach (var r in regions.OrderBy(r => r.Start))
        {
            if (r.Duration <= MinRegion) continue;
            var padded = new TimeRange(Math.Max(0, r.Start - padding), Math.Min(totalDuration, r.End + padding));
            if (output.Count > 0)
            {
                var last = output[^1];
                if (padded.Start - last.End <= maxGap && padded.End - last.Start <= maxLength)
                {
                    output[^1] = last with { End = Math.Max(last.End, padded.End) };
                    continue;
                }
            }
            output.Add(padded);
        }
        var split = new List<TimeRange>();
        foreach (var c in output)
        {
            if (c.Duration <= maxLength) { split.Add(c); continue; }
            int n = (int)Math.Ceiling(c.Duration / maxLength);
            double step = c.Duration / n;
            for (int i = 0; i < n; i++) split.Add(new TimeRange(c.Start + i * step, c.Start + (i + 1) * step));
        }
        return split;
    }
}
