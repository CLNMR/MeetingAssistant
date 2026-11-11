using System.Text;
using MeetingAssistant.Core.Model;

namespace MeetingAssistant.Core.Text;

/// <summary>
/// Text speech models write without anyone saying it (spec 04 §4.1, §4.2): subtitle credits from
/// their training data, and short phrases on a recording that holds no real speech. Used by
/// dictation (<see cref="IsInvented"/>) and by the meeting pipeline (<see cref="IsCredit(string)"/>,
/// <see cref="WithoutCredits"/>).
/// </summary>
public static class InventedText
{
    /// Credits and sign-offs nobody dictates or says in a meeting (normalized, without years).
    public static readonly IReadOnlySet<string> Credits = new HashSet<string>
    {
        "untertitel im auftrag des zdf", "untertitel im auftrag des zdf für funk", "untertitelung im auftrag des zdf",
        "untertitel des zdf", "untertitelung des zdf", "untertitel der amara org community", "untertitel von amara org",
        "untertitel von der amara org community", "copyright wdr", "copyright swr", "copyright zdf",
        "vielen dank fürs zuschauen", "danke fürs zuschauen", "vielen dank für ihre aufmerksamkeit und bis zum nächsten mal",
        "abonniert den kanal", "bitte abonnieren", "abonniere meinen kanal",
        "subtitles by the amara org community", "subtitles by amara org", "thanks for watching", "thank you for watching",
        "thank you so much for watching", "please subscribe", "like and subscribe", "please like and subscribe",
    };

    /// Phrases people do say; invented only when the recording has hardly any voice.
    public static readonly IReadOnlySet<string> Doubtful = new HashSet<string>
    {
        "danke", "vielen dank", "danke schön", "dankeschön", "tschüss", "tschüs", "bis bald", "bis dann", "ende",
        "thank you", "thanks", "thank you very much", "you", "bye", "bye bye", "okay", "ok", "ja", "nein", "hmm", "hm",
        "mhm", "oh", "moment", "musik", "music", "applaus", "applause", "lachen", "so", "ne", "äh", "ähm",
    };

    /// Shorter than this, the longest voiced run is no word (a click or bump).
    public const double MinimumVoicedSeconds = 0.15;
    /// Below this longest voiced run, a doubtful phrase counts as invented.
    public const double DoubtfulBelowSeconds = 0.35;
    /// Dictations held shorter than this are checked for speech before the model runs.
    public const double AccidentalSeconds = 3.0;

    /// Normalization key: lowercase, every non-letter non-whitespace character becomes a space,
    /// spaces collapsed. "Untertitel im Auftrag des ZDF, 2021." → "untertitel im auftrag des zdf".
    public static string Key(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.ToLowerInvariant()) sb.Append(char.IsLetter(c) || char.IsWhiteSpace(c) ? c : ' ');
        return string.Join(" ", TextUtil.SplitNonEmpty(sb.ToString(), ' '));
    }

    /// The whole text is a known subtitle credit.
    public static bool IsCredit(string text) => Credits.Contains(Key(text));

    /// The words, joined with spaces, are a known subtitle credit (meeting turns).
    public static bool IsCredit(IEnumerable<Word> words) => IsCredit(string.Join(" ", words.Select(w => w.W)));

    /// The whole text is a credit, or a doubtful phrase on a recording with hardly any voice.
    public static bool IsInvented(string text, double longestVoicedRun)
    {
        var k = Key(text);
        if (k.Length == 0) return false;
        if (Credits.Contains(k)) return true;
        return longestVoicedRun < DoubtfulBelowSeconds && Doubtful.Contains(k);
    }

    /// Turns without those whose whole text is a credit (meeting transcripts, spec 01 "Invented-text
    /// cleanup"). Ids are not changed.
    public static List<Turn> WithoutCredits(IEnumerable<Turn> turns) => turns.Where(t => !IsCredit(t.Text)).ToList();

    /// Longest stretch (seconds) in which the 16 kHz signal is clearly above its own noise floor
    /// (spec 04 §4.2): 30 ms frames, floor = 20th percentile frame RMS, threshold = max(0.008, 4 × floor),
    /// one quiet frame inside a run is bridged.
    public static double LongestVoicedRun(ReadOnlySpan<float> samples, double sampleRate = 16_000)
    {
        int frame = (int)(sampleRate * 0.03);
        if (frame <= 0 || samples.Length < frame) return 0;
        var rms = new List<float>(samples.Length / frame);
        for (int i = 0; i + frame <= samples.Length; i += frame)
        {
            float sum = 0;
            for (int j = i; j < i + frame; j++) sum += samples[j] * samples[j];
            rms.Add(MathF.Sqrt(sum / frame));
        }
        var sorted = rms.ToArray();
        Array.Sort(sorted);
        float floor = sorted[sorted.Length / 5];
        float threshold = Math.Max(0.008f, floor * 4);
        int best = 0, run = 0, gap = 0;
        foreach (var v in rms)
        {
            if (v >= threshold) { run += 1 + gap; gap = 0; best = Math.Max(best, run); }
            else if (run > 0 && gap == 0) gap = 1;
            else { run = 0; gap = 0; }
        }
        return best * 0.03;
    }
}
