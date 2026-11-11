using System.Text.Json.Serialization;

namespace MeetingAssistant.Core.Model;

/// Meeting mode (spec 01-recording, "Meeting modes"). JSON: "call", "inPerson", "hybrid".
public enum MeetingMode { Call, InPerson, Hybrid }

/// Audio track. JSON: "mic", "system".
public enum Track { Mic, System }

public enum SessionStatus { Recording, Recorded, Queued, Transcribing, Done, Failed }

public static class ModelText
{
    public static string Raw(this Track t) => t == Track.Mic ? "mic" : "system";
    public static Track ParseTrack(string s) => s == "system" ? Track.System : Track.Mic;

    public static string Raw(this MeetingMode m) => m switch
    {
        MeetingMode.Call => "call",
        MeetingMode.InPerson => "inPerson",
        _ => "hybrid",
    };

    public static string Title(this MeetingMode m) => m switch
    {
        MeetingMode.Call => "Call",
        MeetingMode.InPerson => "In person",
        _ => "Hybrid",
    };

    public static string Explanation(this MeetingMode m) => m switch
    {
        MeetingMode.Call => "Online meeting. Records your microphone and the call audio separately. Your voice is labelled “Me”; other people at your microphone are told apart too.",
        MeetingMode.InPerson => "Everyone is in the room. Records the microphone only and separates speakers from it.",
        _ => "Call with several people in the room. Records both and separates speakers on both tracks.",
    };

    public static bool RecordsSystemAudio(this MeetingMode m) => m != MeetingMode.InPerson;

    public static string Raw(this SessionStatus s) => s.ToString().ToLowerInvariant();
}

/// `session.json`.
public sealed class SessionInfo
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public double DurationSec { get; set; }
    public MeetingMode Mode { get; set; }
    public List<Track> Tracks { get; set; } = new();
    public int? ExpectedSpeakers { get; set; }
    /// "auto", "german", "english"; null = use the preference at transcription time.
    public string? Language { get; set; }
    public SessionStatus Status { get; set; }

    public SessionInfo Clone() => (SessionInfo)MemberwiseClone() is var c ? WithTracks(c) : this;
    static SessionInfo WithTracks(SessionInfo c) { c.Tracks = new List<Track>(c.Tracks); return c; }
}

/// One recognised token, with punctuation. JSON: `w`, `start`, `end`. Value equality (used as a set key).
public readonly record struct Word(
    [property: JsonPropertyName("w")] string W,
    [property: JsonPropertyName("start")] double Start,
    [property: JsonPropertyName("end")] double End);

/// A line of the final transcript.
public sealed class Turn
{
    public int Id { get; set; }
    public string Speaker { get; set; } = "";
    public double Start { get; set; }
    public double End { get; set; }
    public string Text { get; set; } = "";
    public Track Track { get; set; }
    public List<Word> Words { get; set; } = new();

    public Turn Clone() => new()
    {
        Id = Id, Speaker = Speaker, Start = Start, End = End, Text = Text, Track = Track, Words = new List<Word>(Words),
    };
}

public sealed class Speaker
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public Track Track { get; set; }
    public float[]? Embedding { get; set; }
    public bool IsMe { get; set; }

    public Speaker Clone() => new() { Id = Id, Label = Label, Track = Track, Embedding = Embedding?.ToArray(), IsMe = IsMe };
}

/// `transcript.json`.
public sealed class Transcript
{
    public List<Turn> Turns { get; set; } = new();
    public List<Speaker> Speakers { get; set; } = new();
    public string Engine { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public double ProcessingSeconds { get; set; }

    public Transcript Clone() => new()
    {
        Turns = Turns.Select(t => t.Clone()).ToList(),
        Speakers = Speakers.Select(s => s.Clone()).ToList(),
        Engine = Engine, CreatedAt = CreatedAt, ProcessingSeconds = ProcessingSeconds,
    };

    /// Display label of a speaker id; falls back to the id itself.
    public string Label(string speakerId) => Speakers.FirstOrDefault(s => s.Id == speakerId)?.Label ?? speakerId;

    /// Move one turn to another speaker.
    public void Reassign(int turnId, string speaker)
    {
        var t = Turns.FirstOrDefault(x => x.Id == turnId);
        if (t is null) return;
        t.Speaker = speaker;
        Normalize();
    }

    /// Merge speaker `from` into `into` (fixes a voice split over two labels).
    public void Merge(string from, string into)
    {
        if (from == into) return;
        foreach (var t in Turns.Where(t => t.Speaker == from)) t.Speaker = into;
        var a = Speakers.FirstOrDefault(s => s.Id == from);
        var b = Speakers.FirstOrDefault(s => s.Id == into);
        if (a?.Embedding is { } ea && b?.Embedding is { } eb) b.Embedding = SpeakerMath.Mean(new[] { ea, eb });
        Speakers.RemoveAll(s => s.Id == from);
        Normalize();
    }

    /// The lowest "Speaker N" no speaker is called yet.
    public string UnusedSpeakerLabel()
    {
        var used = Speakers.Select(s => s.Label.ToLowerInvariant()).ToHashSet();
        var n = 1;
        while (used.Contains($"speaker {n}")) n++;
        return $"Speaker {n}";
    }

    /// Make `id` the user ("This is me"): it gets the user's name (or "Me"); whoever was "me"
    /// before gets the lowest unused "Speaker N".
    public void MarkMe(string id, string name)
    {
        var n = name.Trim();
        foreach (var s in Speakers.Where(s => s.IsMe && s.Id != id))
        {
            s.IsMe = false;
            s.Label = UnusedSpeakerLabel();
        }
        var me = Speakers.FirstOrDefault(s => s.Id == id);
        if (me is not null) { me.IsMe = true; me.Label = n.Length == 0 ? "Me" : n; }
    }

    /// Join consecutive turns of the same speaker (gap below `maxGap` seconds), renumber, and
    /// drop speakers without turns.
    public void Normalize(double maxGap = 1.5)
    {
        var output = new List<Turn>();
        foreach (var t in Turns.OrderBy(t => t.Start))
        {
            var last = output.Count > 0 ? output[^1] : null;
            if (last is not null && last.Speaker == t.Speaker && t.Start - last.End < maxGap)
            {
                last.Text += " " + t.Text;
                last.Words.AddRange(t.Words);
                last.End = Math.Max(last.End, t.End);
            }
            else output.Add(t.Clone());
        }
        for (int i = 0; i < output.Count; i++) output[i].Id = i;
        Turns = output;
        var used = Turns.Select(t => t.Speaker).ToHashSet();
        Speakers.RemoveAll(s => !used.Contains(s.Id));
    }
}

/// `me-voiceprint.json`.
public sealed class Voiceprint
{
    public string Name { get; set; } = "";
    public float[] Embedding { get; set; } = Array.Empty<float>();
    public double Seconds { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// `speaker-library.json`.
public sealed class SpeakerLibrary
{
    public List<SpeakerLibraryEntry> Entries { get; set; } = new();
}

public sealed class SpeakerLibraryEntry
{
    public string Name { get; set; } = "";
    public float[] Embedding { get; set; } = Array.Empty<float>();
    public int Count { get; set; }
}

/// Vector helpers for speaker embeddings.
public static class SpeakerMath
{
    public static float[] Normalize(float[] v)
    {
        double n = Math.Sqrt(v.Sum(x => (double)x * x));
        if (n <= 0) return v.ToArray();
        return v.Select(x => (float)(x / n)).ToArray();
    }

    /// Normalised mean of vectors (empty input gives an empty vector).
    public static float[] Mean(IEnumerable<float[]> vectors)
    {
        var list = vectors.Where(v => v.Length > 0).ToList();
        if (list.Count == 0) return Array.Empty<float>();
        var dim = list[0].Length;
        var sum = new double[dim];
        foreach (var v in list) for (int i = 0; i < Math.Min(dim, v.Length); i++) sum[i] += v[i];
        return Normalize(sum.Select(x => (float)(x / list.Count)).ToArray());
    }

    public static double Cosine(float[] a, float[] b)
    {
        if (a.Length == 0 || a.Length != b.Length) return 0;
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return na <= 0 || nb <= 0 ? 0 : dot / Math.Sqrt(na * nb);
    }
}

/// One line of the live transcript (spec 01-recording, "Live transcription").
/// `Speaker` is a raw live label (`me`, `mic-2`, `system-1`) or null while the diarizer has not caught up.
public sealed record LiveLine(string Id, Track Track, double Start, double End, string Text, string? Speaker, bool IsFinal);
