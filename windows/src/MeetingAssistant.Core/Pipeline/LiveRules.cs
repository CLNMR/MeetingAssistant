using MeetingAssistant.Core.Model;

namespace MeetingAssistant.Core.Pipeline;

/// Friendly names for live speaker ids, in order of first appearance (spec 01-recording,
/// "Live transcript view"): `me` → the user's name (or "Me"); in Call mode `system-*` ids are
/// "Remote n", all others "Speaker n", with separate counters.
public sealed class LiveSpeakerLabels
{
    readonly Dictionary<string, string> _labels = new();
    int _speakers, _remotes;

    public MeetingMode Mode { get; }
    public string MyName { get; }

    public LiveSpeakerLabels(MeetingMode mode, string myName)
    {
        Mode = mode;
        var n = myName.Trim();
        MyName = n.Length == 0 ? "Me" : n;
    }

    /// Label for a raw id; null (speaker not known yet) gives "…".
    public string Label(string? id)
    {
        if (id is null) return "…";
        if (id == "me") return MyName;
        if (_labels.TryGetValue(id, out var l)) return l;
        l = Mode == MeetingMode.Call && id.StartsWith("system", StringComparison.Ordinal)
            ? $"Remote {++_remotes}"
            : $"Speaker {++_speakers}";
        _labels[id] = l;
        return l;
    }
}

/// Live diarizer output for one track: per 10 ms frame a weight for each of 8 speaker slots
/// (spec 01-recording, "Live diarization"). Frames not yet covered mean "lagging".
public static class LiveSlots
{
    public const int SlotCount = 8;
    public const int FramesPerSecond = 100;
    /// Frames added on each side of a word.
    public const int Widen = 5;
    /// The best slot needs a summed weight above this × frame count.
    public const float MinShare = 0.1f;

    /// Dominant slot over the word's frames (widened by 5 each side), or null when the diarizer
    /// has not reached the word's end yet (`frames` covered so far) or no slot is strong enough.
    public static int? SlotFor(Word w, IReadOnlyList<float> probs, int frames)
    {
        int a = Math.Max(0, (int)(w.Start * FramesPerSecond) - Widen);
        int b = Math.Max(a + 1, (int)(w.End * FramesPerSecond) + Widen);
        if (b > frames) return null;
        var sums = new float[SlotCount];
        for (int f = a; f < b; f++)
            for (int k = 0; k < SlotCount; k++) sums[k] += probs[f * SlotCount + k];
        int best = 0;
        for (int k = 1; k < SlotCount; k++) if (sums[k] > sums[best]) best = k;
        return sums[best] > MinShare * (b - a) ? best : null;
    }
}

/// The Call-mode "me" slot rule (spec 01-recording): the mic slot with the most non-echo words
/// is `me`; locked once it has ≥ 25 words and at least twice the runner-up's.
public sealed class MeSlotTracker
{
    public const int LockWords = 25;
    public const int LockRatio = 2;

    public int? MeSlot { get; private set; }
    public bool Locked { get; private set; }

    /// Update from the slots of all non-echo mic words (null slots are ignored).
    public void Update(IEnumerable<int?> slots)
    {
        if (Locked) return;
        var ranked = slots.Where(s => s is not null).GroupBy(s => s!.Value)
                          .Select(g => (Slot: g.Key, Count: g.Count()))
                          .OrderByDescending(x => x.Count).ToList();
        if (ranked.Count == 0) return;
        MeSlot = ranked[0].Slot;
        int second = ranked.Count > 1 ? ranked[1].Count : 0;
        if (ranked[0].Count >= LockWords && ranked[0].Count >= LockRatio * second) Locked = true;
    }

    /// Live id of a mic word in Call mode: `me` when its slot is the me slot or the me slot is
    /// still unknown, else `mic-<slot>`; null when the slot is unknown (diarizer lagging).
    public string? MicSpeaker(int? slot)
    {
        if (slot is not int s) return null;
        return MeSlot is null || s == MeSlot ? "me" : $"mic-{s}";
    }
}

/// One live utterance (`<track>-<counter>`) with its words.
public sealed record LiveUtterance(string Id, Track Track, IReadOnlyList<Word> Words, bool IsFinal);

/// Builds live lines from utterances (spec 01-recording, "Line building" and "Live echo removal").
public static class LiveLineBuilder
{
    /// Mic words that echo the system track, over all live words of both tracks.
    public static HashSet<Word> EchoWords(IEnumerable<LiveUtterance> utterances)
    {
        var all = utterances.ToList();
        var sys = all.Where(u => u.Track == Track.System).SelectMany(u => u.Words).ToList();
        var mic = all.Where(u => u.Track == Track.Mic).SelectMany(u => u.Words).OrderBy(w => w.Start).ToList();
        var mask = Merger.EchoMask(mic, sys);
        var echo = new HashSet<Word>();
        for (int i = 0; i < mic.Count; i++) if (mask[i]) echo.Add(mic[i]);
        return echo;
    }

    /// Split each utterance at speaker changes (unknown speakers inherit the previous word's,
    /// except the first word). Line id `<utteranceId>.<part>`. Hidden words are left out.
    /// Lines are sorted by start.
    public static List<LiveLine> Build(IEnumerable<LiveUtterance> utterances, Func<Word, Track, string?> speaker,
                                       ISet<Word>? hidden = null)
    {
        var output = new List<LiveLine>();
        foreach (var u in utterances)
        {
            var words = u.Track == Track.Mic && hidden is { Count: > 0 } ? u.Words.Where(w => !hidden.Contains(w)).ToList() : u.Words.ToList();
            var tagged = words.Select(w => (Word: w, Speaker: speaker(w, u.Track))).ToList();
            for (int i = 1; i < tagged.Count; i++)
                if (tagged[i].Speaker is null) tagged[i] = (tagged[i].Word, tagged[i - 1].Speaker);
            int part = 0;
            LiveLine? current = null;
            foreach (var (w, spk) in tagged)
            {
                if (current is not null && current.Speaker == spk)
                    current = current with { Text = current.Text + " " + w.W, End = w.End };
                else
                {
                    if (current is not null) output.Add(current);
                    current = new LiveLine($"{u.Id}.{part}", u.Track, w.Start, w.End, w.W, spk, u.IsFinal);
                    part++;
                }
            }
            if (current is not null) output.Add(current);
        }
        return output.OrderBy(l => l.Start).ToList();
    }
}
