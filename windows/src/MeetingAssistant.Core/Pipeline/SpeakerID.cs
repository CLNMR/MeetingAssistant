using MeetingAssistant.Core.Json;
using MeetingAssistant.Core.Model;

namespace MeetingAssistant.Core.Pipeline;

/// Cluster merging and voice matching (spec 01-recording, "Cluster merging", "Speakers").
public static class SpeakerClusters
{
    /// Cosine like the Mac's `SpeakerMath.cosine`: -1 for empty or mismatched vectors.
    public static float Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return -1;
        float d = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++) { d += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return d / Math.Max(1e-9f, MathF.Sqrt(na * nb));
    }

    /// The track part of a cluster id (`mic-2` → `mic`); ids without a dash share one group.
    public static string TrackOf(string id)
    {
        var dash = id.IndexOf('-');
        return dash < 0 ? "" : id[..dash];
    }

    /// Greedily merge the most similar pair of clusters of the same track while the total count
    /// is above `target`, or while the pair's cosine is ≥ `autoMergeAbove`. The cluster with more
    /// speech survives; its centroid becomes the weighted average (renormalised). Clusters of
    /// different tracks are never merged. Returns old id → surviving id for every input id.
    public static Dictionary<string, string> Merge(IReadOnlyDictionary<string, float[]> centroids,
                                                   IReadOnlyDictionary<string, double> weights,
                                                   int? target, float autoMergeAbove)
    {
        var c = centroids.ToDictionary(kv => kv.Key, kv => kv.Value);
        var w = weights.ToDictionary(kv => kv.Key, kv => kv.Value);
        var map = centroids.Keys.ToDictionary(k => k, k => k);
        while (c.Count > 1)
        {
            (string A, string B, float S)? best = null;
            var keys = c.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
            for (int i = 0; i < keys.Count; i++)
                for (int j = i + 1; j < keys.Count; j++)
                {
                    if (TrackOf(keys[i]) != TrackOf(keys[j])) continue;
                    var s = Cosine(c[keys[i]], c[keys[j]]);
                    if (s > (best?.S ?? -2)) best = (keys[i], keys[j], s);
                }
            if (best is not { } b) break;
            bool overTarget = target is int t && c.Count > t;
            if (!overTarget && b.S < autoMergeAbove) break;
            var (keep, drop) = w.GetValueOrDefault(b.A) >= w.GetValueOrDefault(b.B) ? (b.A, b.B) : (b.B, b.A);
            double wk = w.TryGetValue(keep, out var x) ? x : 1, wd = w.TryGetValue(drop, out var y) ? y : 1;
            var ck = c[keep]; var cd = c[drop];
            var mixed = new float[ck.Length];
            for (int i = 0; i < ck.Length; i++) mixed[i] = (float)((ck[i] * wk + cd[i] * wd) / (wk + wd));
            c[keep] = Normalize(mixed);
            w[keep] = wk + wd;
            c.Remove(drop); w.Remove(drop);
            foreach (var k in map.Keys.ToList()) if (map[k] == drop) map[k] = keep;
        }
        return map;
    }

    /// L2-normalise (floor 1e-9 like the Mac).
    public static float[] Normalize(float[] v)
    {
        double n = Math.Max(1e-9, Math.Sqrt(v.Sum(x => (double)x * x)));
        return v.Select(x => (float)(x / n)).ToArray();
    }
}

/// Voice matching with a threshold and a margin over the runner-up.
public static class SpeakerMatch
{
    /// Best-matching candidate for `target`, or null when the top cosine is below `threshold` or
    /// does not beat the runner-up by `margin`. The runner-up is the next candidate or, if
    /// larger, the most similar vector of `reference` that is not the winner.
    public static string? Best(float[] target, IReadOnlyList<(string Id, float[] Vector)> candidates,
                               float threshold, float margin, IEnumerable<float[]> reference)
    {
        var scored = candidates.Select(c => (c.Id, c.Vector, S: SpeakerClusters.Cosine(c.Vector, target)))
                               .OrderByDescending(x => x.S).ToList();
        if (scored.Count == 0 || scored[0].S < threshold) return null;
        var winner = scored[0].Vector;
        float runnerUp = scored.Count > 1 ? scored[1].S : -1;
        foreach (var r in reference)
            if (!r.AsSpan().SequenceEqual(winner)) runnerUp = Math.Max(runnerUp, SpeakerClusters.Cosine(r, target));
        return scored[0].S - runnerUp >= margin ? scored[0].Id : null;
    }
}

/// Load, save and teach the speaker library (`speaker-library.json`; spec 01-recording, "Speaker library").
public static class SpeakerLibraryStore
{
    public static SpeakerLibrary Load() =>
        JsonIO.TryRead<SpeakerLibrary>(AppPaths.SpeakerLibraryFile, out var lib) && lib is not null ? lib : new SpeakerLibrary();

    public static void Save(this SpeakerLibrary library)
    {
        AppPaths.Ensure();
        JsonIO.Write(library, AppPaths.SpeakerLibraryFile);
    }

    static bool SameName(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// Add or refine a named voice: case-insensitive name match (trimmed), running average
    /// (old×count + new)/(count+1) renormalised, count+1.
    public static void Learn(this SpeakerLibrary library, string name, float[] embedding)
    {
        var n = name.Trim();
        if (n.Length == 0 || embedding.Length == 0) return;
        var e = library.Entries.FirstOrDefault(x => SameName(x.Name, n));
        if (e is null)
        {
            library.Entries.Add(new SpeakerLibraryEntry { Name = n, Embedding = embedding.ToArray(), Count = 1 });
            return;
        }
        float c = e.Count;
        var avg = new float[Math.Min(e.Embedding.Length, embedding.Length)];
        for (int i = 0; i < avg.Length; i++) avg[i] = (e.Embedding[i] * c + embedding[i]) / (c + 1);
        e.Embedding = SpeakerClusters.Normalize(avg);
        e.Count += 1;
    }

    /// Remove every entry with this name (ignoring case and surrounding spaces).
    public static void Forget(this SpeakerLibrary library, string name)
    {
        var n = name.Trim();
        library.Entries.RemoveAll(x => SameName(x.Name, n));
    }

    /// Rename a speaker of a transcript and, when it has an embedding and is not "me", teach the
    /// library (spec 01-recording, "Rename…"). Empty names are ignored. Returns true when renamed.
    /// The caller saves the transcript; the library is saved here.
    public static bool RenameAndLearn(Transcript transcript, string speakerId, string name)
    {
        var n = name.Trim();
        var s = transcript.Speakers.FirstOrDefault(x => x.Id == speakerId);
        if (n.Length == 0 || s is null) return false;
        s.Label = n;
        if (s.Embedding is { Length: > 0 } e && !s.IsMe)
        {
            var lib = Load();
            lib.Learn(n, e);
            lib.Save();
        }
        return true;
    }
}

/// The user's voiceprint (`me-voiceprint.json`; spec 01-recording, "Voiceprint").
public static class VoiceprintStore
{
    /// Seconds of detected speech a voice sample needs.
    public const double MinimumSpeechSeconds = 8;
    /// Seconds the user records before "Finish" is enabled.
    public const double RecordingSeconds = 15;
    /// Length of one embedding window during enrollment (s); a window is used when at least half remains.
    public const double WindowSeconds = 6;

    public static Voiceprint? Load() =>
        JsonIO.TryRead<Voiceprint>(AppPaths.VoiceprintFile, out var v) ? v : null;

    public static void Save(Voiceprint v)
    {
        AppPaths.Ensure();
        JsonIO.Write(v, AppPaths.VoiceprintFile);
    }

    public static void Delete()
    {
        try { File.Delete(AppPaths.VoiceprintFile); } catch (IOException) { }
    }

    /// The error text when a sample has too little speech.
    public static string TooShortMessage(double seconds) =>
        $"Only {seconds:F0} s of speech detected; at least {MinimumSpeechSeconds:F0} s are needed. Please record again and keep reading without long pauses.";

    /// Windows (sample ranges) of `speechSamples` that enrollment embeds: consecutive 6 s
    /// windows, each used while at least 3 s remain.
    public static List<(int Start, int End)> EnrollmentWindows(int speechSamples)
    {
        int win = (int)(WindowSeconds * 16_000);
        var list = new List<(int, int)>();
        for (int i = 0; i + win / 2 < speechSamples; i += win) list.Add((i, Math.Min(speechSamples, i + win)));
        return list;
    }
}

/// Errors of the transcription pipeline and enrollment, with the spec's messages.
public sealed class PipelineException(string message) : Exception(message)
{
    public static PipelineException NoAudio() => new("The recording contains no audio.");
    public static PipelineException EnrollmentTooShort(double seconds) => new(VoiceprintStore.TooShortMessage(seconds));
}
