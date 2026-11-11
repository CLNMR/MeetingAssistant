using MeetingAssistant.Core.Model;

namespace MeetingAssistant.Core.Pipeline;

/// Clean-up applied to the turns of a final transcript (spec 01-recording, steps 9:
/// "Invented-text cleanup" and "Vocabulary"). Implemented in Core/Text; the pipeline only calls it.
public interface ITranscriptTextCleanup
{
    /// Returns the turns to keep, with corrected words/text. Ids are renumbered by the caller.
    IReadOnlyList<Turn> Clean(IReadOnlyList<Turn> turns);
}

/// Cleanup built from two functions, matching the Mac's `TranscriptionPipeline.cleaned`:
/// drop turns whose whole text is a subtitle credit, then apply the vocabulary to each turn's
/// words (text rebuilt from the words when they changed).
public sealed class TranscriptTextCleanup(Func<string, bool>? isCredit, Func<IReadOnlyList<Word>, IReadOnlyList<Word>>? applyVocabulary)
    : ITranscriptTextCleanup
{
    /// Does nothing (no credits list, no vocabulary).
    public static readonly TranscriptTextCleanup None = new(null, null);

    public IReadOnlyList<Turn> Clean(IReadOnlyList<Turn> turns)
    {
        var output = new List<Turn>();
        foreach (var turn in turns)
        {
            if (isCredit?.Invoke(turn.Text) == true) continue;
            var t = turn.Clone();
            if (applyVocabulary is not null)
            {
                var words = applyVocabulary(t.Words);
                if (!words.SequenceEqual(t.Words))
                {
                    t.Words = words.ToList();
                    t.Text = string.Join(" ", t.Words.Select(w => w.W));
                }
            }
            t.Id = output.Count;
            output.Add(t);
        }
        return output;
    }
}

/// Per-track output of ASR + diarization, before the transcript is assembled.
public sealed class TrackAnalysis
{
    public required Track Track { get; init; }
    public required IReadOnlyList<Word> Words { get; init; }
    /// Diarized segments with global ids (`mic-0`…); null when the track was not diarized or
    /// the diarizer returned nothing (then every word is `<track>-0`).
    public IReadOnlyList<SpeakerSegment>? Segments { get; init; }
    /// CAM++ centroid per cluster id (only clusters with ≥ 1 s of clean speech).
    public Dictionary<string, float[]> Centroids { get; init; } = new();
    /// Total speech seconds per cluster id (merge weights).
    public Dictionary<string, double> SpeechSeconds { get; init; } = new();
}

/// Steps 4–10 of the offline pipeline (spec 01-recording, "Offline transcription pipeline"):
/// cluster merging, word → speaker, echo removal, final centroids, "me", turns, cleanup, labels.
/// Pure: no models, no files.
public static class TranscriptAssembly
{
    public static Transcript Build(IReadOnlyList<TrackAnalysis> tracks, MeetingMode mode, int? expectedSpeakers,
                                   PipelineSettings settings, Voiceprint? voiceprint, SpeakerLibrary library,
                                   ITranscriptTextCleanup? cleanup)
    {
        var centroids = new Dictionary<string, float[]>();
        var speech = new Dictionary<string, double>();
        foreach (var t in tracks)
        {
            foreach (var kv in t.Centroids) centroids[kv.Key] = kv.Value;
            foreach (var kv in t.SpeechSeconds) speech[kv.Key] = kv.Value;
        }

        // Only same-track clusters merge; Hybrid has no participants target.
        int? target = mode == MeetingMode.Hybrid ? null : expectedSpeakers;
        var mapping = SpeakerClusters.Merge(centroids, speech, target, settings.AutoMergeThreshold);
        string Canonical(string id) => mapping.TryGetValue(id, out var m) ? m : id;

        var perTrack = new Dictionary<Track, List<(Word Word, string Speaker)>>();
        foreach (var t in tracks)
        {
            var r = new TrackResult(t.Track, t.Words, t.Segments, $"{t.Track.Raw()}-0");
            perTrack[t.Track] = Merger.AssignSpeakers(r).Select(x => (x.Word, Canonical(x.Speaker))).ToList();
        }
        if (mode != MeetingMode.InPerson && perTrack.TryGetValue(Track.Mic, out var mic)
            && tracks.FirstOrDefault(t => t.Track == Track.System) is { } sys)
            perTrack[Track.Mic] = Merger.RemoveEcho(mic, sys.Words);

        var tagged = new List<(Word Word, string Speaker, Track Track)>();
        foreach (var (track, words) in perTrack) tagged.AddRange(words.Select(w => (w.Word, w.Speaker, track)));

        var final = centroids.GroupBy(kv => Canonical(kv.Key))
                             .ToDictionary(g => g.Key, g => SpeakerMath.Mean(g.Select(kv => kv.Value)));

        var meId = FindMe(final, tagged, mode, settings, voiceprint);

        IReadOnlyList<Turn> turns = Merger.Turns(tagged);
        if (cleanup is not null) turns = cleanup.Clean(turns);
        var turnList = turns.Select((t, i) => { t.Id = i; return t; }).ToList();

        var speakers = Label(turnList, final, meId, settings, library);
        return new Transcript { Turns = turnList, Speakers = speakers, Engine = AsrEngine.ParakeetUltra.Raw() };
    }

    /// Step 8: the voiceprint's best match among `mic-*` clusters (runner-up also over all final
    /// centroids); otherwise, in Call mode only, the mic speaker with the most (non-echo) words.
    public static string? FindMe(IReadOnlyDictionary<string, float[]> finalCentroids,
                                 IEnumerable<(Word Word, string Speaker, Track Track)> tagged,
                                 MeetingMode mode, PipelineSettings settings, Voiceprint? voiceprint)
    {
        string? meId = null;
        if (voiceprint is { Embedding.Length: > 0 })
        {
            var candidates = finalCentroids.Where(kv => kv.Key.StartsWith("mic-", StringComparison.Ordinal))
                                           .Select(kv => (kv.Key, kv.Value)).ToList();
            meId = SpeakerMatch.Best(voiceprint.Embedding, candidates, settings.MeThreshold, settings.MatchMargin,
                                     finalCentroids.Values);
        }
        if (meId is null && mode == MeetingMode.Call)
        {
            meId = tagged.Where(t => t.Track == Track.Mic).GroupBy(t => t.Speaker)
                         .OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
        }
        return meId;
    }

    /// Step 10: labels in order of first appearance: "me" → myName; else a library match whose
    /// name is not used yet; else "Speaker n" (n counts unnamed speakers only).
    public static List<Speaker> Label(IReadOnlyList<Turn> turns, IReadOnlyDictionary<string, float[]> finalCentroids,
                                      string? meId, PipelineSettings settings, SpeakerLibrary library)
    {
        var speakers = new List<Speaker>();
        var used = new HashSet<string>();
        int counter = 0;
        var entries = library.Entries.Select(e => (e.Name, e.Embedding)).ToList();
        foreach (var t in turns)
        {
            if (speakers.Any(s => s.Id == t.Speaker)) continue;
            finalCentroids.TryGetValue(t.Speaker, out var emb);
            bool isMe = t.Speaker == meId;
            string label;
            string? libName = null;
            if (!isMe && emb is not null)
                libName = SpeakerMatch.Best(emb, entries, settings.LibraryThreshold, settings.MatchMargin, Array.Empty<float[]>());
            if (isMe) label = settings.EffectiveName;
            else if (libName is not null && !used.Contains(libName)) label = libName;
            else label = $"Speaker {++counter}";
            used.Add(label);
            speakers.Add(new Speaker { Id = t.Speaker, Label = label, Track = t.Track, Embedding = emb, IsMe = isMe });
        }
        return speakers;
    }
}
