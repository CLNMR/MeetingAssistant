using System.Text.RegularExpressions;

namespace MeetingAssistant.Core.Assist;

/// <summary>
/// Removes deadlines a model added to next steps or to-dos although nobody said them (spec 02 §7.7):
/// "- Angebot schicken (bis Freitag)" when no Friday came up in the meeting. In doubt a deadline stays:
/// when a word of it was said (German or English), when it contains a digit, or when it names no time.
/// </summary>
public static class DeadlineCheck
{
    /// <summary>The checked text and the deadlines that were removed (e.g. "bis Freitag").</summary>
    public sealed record Result(string Text, List<string> Removed);

    /// Words that carry no time of their own.
    public static readonly HashSet<string> Fillers = new()
    {
        "bis", "spätestens", "spaetestens", "zum", "zur", "am", "an", "im", "in", "ende", "anfang", "mitte", "des", "der",
        "die", "das", "dem", "den", "nächste", "nächsten", "nächster", "kommende", "kommenden", "diese", "dieser", "diesen",
        "vor", "nach", "ca", "etwa", "frist", "deadline", "termin", "fällig", "by", "until", "till", "due", "the", "end", "of",
        "next", "this", "start", "beginning", "middle", "before", "after", "on", "at", "eod", "asap", "latest", "at the latest",
    };

    /// Time words and their meaning across German and English.
    public static readonly IReadOnlyDictionary<string, string> Canonical = BuildCanonical();

    static Dictionary<string, string> BuildCanonical()
    {
        var groups = new (string Key, string[] Words)[]
        {
            ("mon", new[] { "montag", "monday" }), ("tue", new[] { "dienstag", "tuesday" }), ("wed", new[] { "mittwoch", "wednesday" }),
            ("thu", new[] { "donnerstag", "thursday" }), ("fri", new[] { "freitag", "friday" }), ("sat", new[] { "samstag", "sonnabend", "saturday" }),
            ("sun", new[] { "sonntag", "sunday" }), ("tomorrow", new[] { "morgen", "tomorrow" }), ("dayafter", new[] { "übermorgen" }),
            ("today", new[] { "heute", "today", "tonight" }), ("week", new[] { "woche", "week", "kw", "kalenderwoche" }),
            ("weekend", new[] { "wochenende", "weekend" }), ("month", new[] { "monat", "month", "monatsende" }),
            ("quarter", new[] { "quartal", "quarter", "quartalsende" }), ("year", new[] { "jahr", "jahresende", "year" }),
            ("jan", new[] { "januar", "jänner", "january" }), ("feb", new[] { "februar", "february" }), ("mar", new[] { "märz", "march" }),
            ("apr", new[] { "april" }), ("may", new[] { "mai", "may" }), ("jun", new[] { "juni", "june" }), ("jul", new[] { "juli", "july" }),
            ("aug", new[] { "august" }), ("sep", new[] { "september" }), ("oct", new[] { "oktober", "october" }), ("nov", new[] { "november" }),
            ("dec", new[] { "dezember", "december" }), ("christmas", new[] { "weihnachten", "christmas" }), ("easter", new[] { "ostern", "easter" }),
        };
        var m = new Dictionary<string, string>();
        foreach (var (k, words) in groups) foreach (var w in words) m[w] = k;
        return m;
    }

    /// A bullet ending in a parenthesis.
    static readonly Regex TrailingParenthesis = new(@"^(\s*(?:[-*•–]|\d+\.)\s+.*?)\s*\(([^()]{2,60})\)([.;]?)\s*$");
    /// A bullet ending in "bis/by …" after a dash, a comma or a space.
    static readonly Regex TrailingBy = new(@"^(\s*(?:[-*•–]|\d+\.)\s+.*?)(?:\s*[,–—-]\s*|\s+)((?:bis|spätestens|by|until|due)\s+[^,.;()]{2,40})([.;]?)\s*$",
                                           RegexOptions.IgnoreCase);

    public static Result Filter(string text, string transcript)
    {
        var spoken = Words(transcript).ToHashSet();
        var spokenCanonical = spoken.Select(w => Canonical.TryGetValue(w, out var c) ? c : PrefixCanonical(w)).Where(c => c is not null).ToHashSet();
        var removed = new List<string>();
        var lines = text.Split('\n').Select(line =>
        {
            foreach (var re in new[] { TrailingParenthesis, TrailingBy })
            {
                var m = re.Match(line);
                if (!m.Success) continue;
                var deadline = m.Groups[2].Value;
                if (IsUnsupported(deadline, spoken, spokenCanonical!))
                {
                    removed.Add(deadline);
                    return m.Groups[1].Value + m.Groups[3].Value;
                }
                return line;
            }
            return line;
        }).ToList();
        return new Result(string.Join("\n", lines), removed);
    }

    /// <summary>True when the deadline names a time and none of its time words was said (spec 02 §7.7).</summary>
    public static bool IsUnsupported(string deadline, ISet<string> spoken, ISet<string> spokenCanonical)
    {
        if (deadline.Any(char.IsDigit)) return false;
        var content = Words(deadline).Where(w => !Fillers.Contains(w)).ToList();
        var timeWords = content.Where(w => Canonical.ContainsKey(w) || PrefixCanonical(w) is not null).ToList();
        // No time word at all ("(Max)", "(optional)"): not a deadline.
        if (timeWords.Count == 0) return false;
        foreach (var w in content)
        {
            if (spoken.Contains(w)) return false;
            if (spoken.Any(s => s.Length >= 5 && w.Length >= 5 && (s.StartsWith(w, StringComparison.Ordinal) || w.StartsWith(s, StringComparison.Ordinal))))
                return false;
            var c = Canonical.TryGetValue(w, out var x) ? x : PrefixCanonical(w);
            if (c is not null && spokenCanonical.Contains(c)) return false;
        }
        return true;
    }

    /// <summary>"freitags", "Freitagabend" → "fri": the longest time word of at least 5 letters the word starts with.</summary>
    public static string? PrefixCanonical(string w)
    {
        if (w.Length < 5) return null;
        return Canonical.Where(kv => kv.Key.Length >= 5 && w.StartsWith(kv.Key, StringComparison.Ordinal))
            .OrderByDescending(kv => kv.Key.Length).Select(kv => kv.Value).FirstOrDefault();
    }

    /// Lower-cased words, split on every non-letter.
    public static List<string> Words(string text) => AssistLanguage.Words(text);
}
