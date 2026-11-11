using System.Text.RegularExpressions;

namespace MeetingAssistant.Core.Text;

/// <summary>
/// Spoken forms that speech recognition writes down literally (spec 04 §4.3, dictation only):
/// hesitations, e-mail addresses, German times. There is no spoken-punctuation conversion.
/// </summary>
public static class SpokenForms
{
    /// Everything below, in this order: hesitations → e-mail addresses → times.
    public static string Apply(string text) => Times(EmailAddresses(WithoutHesitations(text)));

    // MARK: hesitations

    static readonly HashSet<string> Hesitations = new() { "äh", "ähm", "äähm", "öh", "öhm", "ehm", "hmm", "uh", "uhm", "erm" };

    /// "Ähm, also wir …" → "Also wir …". A lone "M," at the very start is a swallowed "ähm" too.
    public static string WithoutHesitations(string text)
    {
        var words = text.Split(' ').ToList();
        bool removedAtStart = false;
        int i = 0;
        while (i < words.Count)
        {
            var p = VocabularyMatcher.Split(words[i]);
            var core = p.Core.ToLowerInvariant();
            bool isFiller = Hesitations.Contains(core) || (i == 0 && core == "m" && p.Trailing.StartsWith(','));
            if (isFiller && p.Leading.Length == 0 && (p.Trailing.Length == 0 || p.Trailing == "," || p.Trailing == "…"))
            {
                if (i == 0) removedAtStart = true;
                // "Wir, ähm, gehen" → "Wir gehen": the comma before it belonged to the pause.
                if (p.Trailing == "," && i > 0 && words[i - 1].EndsWith(',')) words[i - 1] = words[i - 1][..^1];
                words.RemoveAt(i);
                continue;
            }
            i++;
        }
        var output = TextUtil.TrimSpaces(string.Join(" ", words));
        if (removedAtStart && output.Length > 0 && char.IsLower(output[0]))
            output = char.ToUpperInvariant(output[0]) + output[1..];
        return output;
    }

    // MARK: times

    static readonly Regex HourMinute = new(@"\b([01]?\d|2[0-3]) Uhr ([0-5]?\d)\b(?=\s*(?:$|[.,;:!?)]|\s[a-zäöüß]))");
    static readonly Regex RangeSameUnit = new(@"\b(\d{1,2}:\d{2}) Uhr (bis|und|-|–) (\d{1,2}:\d{2}) Uhr\b");

    /// German "16 Uhr 45" → "16:45 Uhr", and "16:45 Uhr bis 17:05 Uhr" → "16:45 bis 17:05 Uhr".
    /// Only when the minutes end the clause or a lowercase word follows ("um 10 Uhr 3 Personen" stays).
    public static string Times(string text)
    {
        var s = HourMinute.Replace(text, m =>
        {
            var minutes = m.Groups[2].Value;
            return $"{m.Groups[1].Value}:{(minutes.Length == 1 ? "0" + minutes : minutes)} Uhr";
        });
        return RangeSameUnit.Replace(s, "$1 $2 $3 Uhr");
    }

    // MARK: e-mail addresses

    static readonly HashSet<string> AtWords = new() { "at", "ät", "@" };
    static readonly HashSet<string> DotWords = new() { "punkt", "dot" };
    static readonly Dictionary<string, string> JoinWords = new()
    {
        ["minus"] = "-", ["bindestrich"] = "-", ["dash"] = "-", ["hyphen"] = "-", ["unterstrich"] = "_", ["underscore"] = "_",
    };
    /// Top-level domains that end an address.
    public static readonly IReadOnlySet<string> TopLevel = new HashSet<string>
    {
        "de", "com", "org", "net", "io", "ai", "eu", "ch", "at", "info", "biz", "co", "uk",
        "fr", "nl", "it", "es", "dev", "app", "online", "email", "cloud", "tech", "gmbh", "berlin",
    };

    /// "max punkt mustermann at firma punkt de" → "max.mustermann@firma.de". Only when the part after
    /// "at" has a "punkt"/"dot" followed by a known top-level domain, and a local part precedes it.
    public static string EmailAddresses(string text)
    {
        var words = text.Split(' ').ToList();
        VocabularyMatcher.Parts Parts(int i) => VocabularyMatcher.Split(words[i]);
        // A plain word of letters/digits; punctuation before it only with `leading`, after it only with `last`.
        string? Word(int i, bool last = false, bool leading = false)
        {
            if (i < 0 || i >= words.Count) return null;
            var p = Parts(i);
            if (!(p.Leading.Length == 0 || leading) || p.Core.Length == 0 || !p.Core.All(TextUtil.IsAlnum)
                || !(p.Trailing.Length == 0 || last)) return null;
            return p.Core.ToLowerInvariant();
        }
        string? Separator(int i)
        {
            var w = Word(i);
            if (w is null) return null;
            if (DotWords.Contains(w)) return ".";
            return JoinWords.TryGetValue(w, out var s) ? s : null;
        }

        int i = 0;
        while (i < words.Count)
        {
            var at = Word(i);
            // (As on the Mac, a bare "@" token has no letter core and is not taken.)
            if (at is null || !(AtWords.Contains(at) || words[i] == "@")) { i++; continue; }
            // Domain after "at".
            int j = i + 1;
            var first = Word(j, last: true);
            if (first is null) { i++; continue; }
            var domain = first;
            (int index, string domain)? end = null;
            while (Separator(j + 1) is { } sep && Word(j) is not null && Word(j + 2, last: true) is { } next)
            {
                domain += sep + next;
                j += 2;
                if (sep == "." && TopLevel.Contains(next)) end = (j, domain);
                if (Word(j) is null) break;                // punctuation after it ends the address
            }
            if (end is not { } e) { i++; continue; }
            // Local part before "at".
            int k = i - 1;
            var local = Word(k, leading: true);
            if (local is null) { i++; continue; }
            while (Parts(k).Leading.Length == 0 && Separator(k - 1) is { } sep2 && Word(k - 2, leading: true) is { } prev)
            {
                local = prev + sep2 + local;
                k -= 2;
            }
            var address = Parts(k).Leading + local + "@" + e.domain + Parts(e.index).Trailing;
            words.RemoveRange(k, e.index - k + 1);
            words.Insert(k, address);
            i = k + 1;
        }
        return string.Join(" ", words);
    }
}
