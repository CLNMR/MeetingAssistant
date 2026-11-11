using System.Text;
using MeetingAssistant.Core.Model;

namespace MeetingAssistant.Core.Text;

/// <summary>
/// Applies a vocabulary to text (dictation) or to timed words (final transcripts), following
/// spec 04 §5.3: replacements and exact term spellings, optionally sound-alike (fuzzy) names.
/// Correctly spelled dictionary words are never turned into a name.
/// </summary>
public sealed class VocabularyMatcher
{
    sealed record Entry(string Text, string Key, string Sound, int Words);

    readonly Dictionary<string, string> _exact = new();    // letters → term
    readonly Dictionary<string, string> _spoken = new();   // letters of a replacement's spoken form → written
    readonly List<Entry> _fuzzy = new();
    readonly Func<string, bool> _isKnownWord;
    readonly int _maxWindow;

    /// Minimum Jaro-Winkler similarity of the sound keys for a fuzzy correction.
    public const double Threshold = 0.9;
    /// Terms with fewer letters than this are only matched exactly.
    public const int FuzzyMinLetters = 6;

    /// Builds the matcher. `isKnownWord` defaults to <see cref="SpellCheck.IsKnown"/>.
    public VocabularyMatcher(IEnumerable<string> terms, IEnumerable<Vocabulary.Replacement> replacements, bool fuzzy = true,
                             Func<string, bool>? isKnownWord = null)
    {
        int maxWords = 1;
        foreach (var t in terms)
        {
            var term = t.Trim();
            var key = Letters(term);
            if (key.Length == 0 || _exact.ContainsKey(key)) continue;
            _exact[key] = term;
            var words = TextUtil.SplitWhere(term, c => c is ' ' or '-').Count;
            maxWords = Math.Max(maxWords, words);
            if (fuzzy && key.Length >= FuzzyMinLetters) _fuzzy.Add(new Entry(term, key, Sound(term), words));
        }
        foreach (var r in replacements)
        {
            var key = Letters(r.Spoken);
            if (key.Length == 0) continue;
            _spoken[key] = r.Written;
            maxWords = Math.Max(maxWords, TextUtil.SplitWhere(r.Spoken, c => c is ' ' or '-').Count);
        }
        _isKnownWord = isKnownWord ?? SpellCheck.IsKnown;
        _maxWindow = Math.Min(4, maxWords + 1);
    }

    /// No terms and no replacements.
    public bool IsEmpty => _exact.Count == 0 && _spoken.Count == 0;

    /// Text with the vocabulary applied. Only changed words are rewritten; spacing and line
    /// breaks between words stay exactly as they were.
    public string Apply(string text)
    {
        if (IsEmpty || text.Length == 0) return text;
        // gaps[i] comes before tokens[i]; the last gap trails.
        var tokens = new List<string>();
        var gaps = new List<string>();
        var cur = new StringBuilder();
        var gap = new StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (cur.Length > 0) { tokens.Add(cur.ToString()); cur.Clear(); }
                gap.Append(ch);
            }
            else
            {
                if (cur.Length == 0) { gaps.Add(gap.ToString()); gap.Clear(); }
                cur.Append(ch);
            }
        }
        if (cur.Length > 0) tokens.Add(cur.ToString());
        gaps.Add(gap.ToString());
        var edits = Edits(tokens);
        if (edits.Count == 0) return text;
        var output = new StringBuilder();
        int i = 0, e = 0;
        while (i < tokens.Count)
        {
            output.Append(gaps[i]);
            if (e < edits.Count && edits[e].Start == i)
            {
                output.Append(edits[e].Text);
                i = edits[e].End;
                e++;
            }
            else
            {
                output.Append(tokens[i]);
                i++;
            }
        }
        output.Append(gaps[tokens.Count]);
        return output.ToString();
    }

    /// Timed words with the vocabulary applied; words joined into one term keep the first
    /// word's start and the last word's end.
    public List<Word> Apply(IReadOnlyList<Word> words)
    {
        if (IsEmpty || words.Count == 0) return words.ToList();
        var edits = Edits(words.Select(w => w.W).ToList());
        if (edits.Count == 0) return words.ToList();
        var output = new List<Word>();
        int i = 0, e = 0;
        while (i < words.Count)
        {
            if (e < edits.Count && edits[e].Start == i)
            {
                var ed = edits[e];
                output.Add(new Word(ed.Text, words[ed.Start].Start, words[ed.End - 1].End));
                i = ed.End; e++;
            }
            else { output.Add(words[i]); i++; }
        }
        return output;
    }

    internal readonly record struct Edit(int Start, int End, string Text);

    static bool InnerPunctuation(IReadOnlyList<Parts> window)
    {
        for (int k = 0; k < window.Count - 1; k++) if (window[k].Trailing.Length > 0) return true;
        for (int k = 1; k < window.Count; k++) if (window[k].Leading.Length > 0) return true;
        return false;
    }

    /// Non-overlapping replacements, left to right; at each position the longest match wins.
    internal List<Edit> Edits(IReadOnlyList<string> tokens)
    {
        var parts = tokens.Select(Split).ToList();
        var output = new List<Edit>();
        int i = 0;
        while (i < tokens.Count)
        {
            Edit? found = null;
            for (int n = Math.Min(_maxWindow, tokens.Count - i); n >= 1; n--)
            {
                var window = parts.GetRange(i, n);
                if (n > 1 && InnerPunctuation(window)) continue;
                var cores = window.Select(p => p.Core).ToList();
                if (cores.Any(c => c.Length == 0)) continue;
                var key = Letters(string.Concat(cores));
                if (key.Length == 0) continue;
                var original = string.Join(" ", cores);
                if (_spoken.TryGetValue(key, out var w))
                {
                    found = new Edit(i, i + n, window[0].Leading + w + window[n - 1].Trailing);
                    break;
                }
                if (_exact.TryGetValue(key, out var term))
                {
                    if (term == original) { found = new Edit(i, i + n, string.Join(" ", tokens.Skip(i).Take(n))); break; }
                    // Only the case differs and these are ordinary words: leave them ("klein" / Klein).
                    var caseOnly = term.ToLowerInvariant() == original.ToLowerInvariant();
                    if (caseOnly && !HasInnerCapital(term) && cores.All(_isKnownWord)) continue;
                    found = new Edit(i, i + n, window[0].Leading + term + window[n - 1].Trailing);
                    break;
                }
            }
            // No exact match: the window that sounds most like a term, the shorter one on a tie.
            if (found is null && _fuzzy.Count > 0)
            {
                (int n, string term, double score)? best = null;
                for (int n = 1; n <= Math.Min(_maxWindow, tokens.Count - i); n++)
                {
                    var window = parts.GetRange(i, n);
                    if (n > 1 && InnerPunctuation(window)) break;
                    var cores = window.Select(p => p.Core).ToList();
                    if (cores.Any(c => c.Length == 0)) break;
                    var m = FuzzyMatch(cores, Letters(string.Concat(cores)));
                    if (m is { } mm && mm.score > (best?.score ?? 0)) best = (n, mm.term, mm.score);
                }
                if (best is { } b)
                    found = new Edit(i, i + b.n, parts[i].Leading + b.term + parts[i + b.n - 1].Trailing);
            }
            if (found is { } f)
            {
                // A match that changes nothing is consumed but not reported.
                if (string.Join(" ", tokens.Skip(f.Start).Take(f.End - f.Start)) != f.Text) output.Add(f);
                i = f.End;
            }
            else i++;
        }
        return output;
    }

    (string term, double score)? FuzzyMatch(List<string> cores, string key)
    {
        if (_fuzzy.Count == 0 || key.Length < FuzzyMinLetters - 1) return null;
        // Ordinary words are never "corrected" into a name.
        if (cores.All(_isKnownWord)) return null;
        // Digits mean something else ("A4", "2025").
        if (key.Any(char.IsNumber)) return null;
        var s = Sound(string.Concat(cores));
        var known = cores.Count > 1 ? cores.Where(_isKnownWord).Select(c => c.ToLowerInvariant()).ToList() : new List<string>();
        (string term, double score)? best = null;
        foreach (var e in _fuzzy)
        {
            if (cores.Count > e.Words + 1) continue;
            if (Math.Abs(e.Key.Length - key.Length) > Math.Max(1, e.Key.Length / 5)) continue;
            char? s0 = s.Length > 0 ? s[0] : null, e0 = e.Sound.Length > 0 ? e.Sound[0] : null;
            if (s0 != e0) continue;
            // In a window of several words, ordinary words must be part of the term.
            if (known.Count > 0)
            {
                var termWords = TextUtil.SplitWhere(e.Text.ToLowerInvariant(), c => !TextUtil.IsAlnum(c)).ToHashSet();
                if (!known.All(termWords.Contains)) continue;
            }
            var score = JaroWinkler(s, e.Sound);
            if (score >= Threshold && score > (best?.score ?? 0)) best = (e.Text, score);
        }
        return best;
    }

    // MARK: keys

    /// "HubSpot", "iPhone": an uppercase letter right after a lowercase one.
    public static bool HasInnerCapital(string s)
    {
        for (int i = 0; i + 1 < s.Length; i++) if (char.IsLower(s[i]) && char.IsUpper(s[i + 1])) return true;
        return false;
    }

    /// Lowercased letters and digits only: "Hub-Spot" → "hubspot" (spec 04 §5.3 letters(s)).
    public static string Letters(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s.ToLowerInvariant()) if (TextUtil.IsAlnum(c)) sb.Append(c);
        return sb.ToString();
    }

    static readonly (string, string)[] SoundRules =
    {
        ("ä", "e"), ("ö", "o"), ("ü", "u"), ("ß", "ss"), ("é", "e"), ("è", "e"),
        ("sch", "s"), ("ph", "f"), ("th", "t"), ("ck", "k"), ("qu", "kv"), ("dt", "t"),
        ("ch", "k"), ("c", "k"), ("aw", "au"), ("ou", "u"), ("ey", "ai"), ("ay", "ai"), ("ei", "ai"),
        ("ie", "i"), ("y", "i"), ("w", "v"), ("v", "f"), ("z", "ts"), ("x", "ks"),
    };

    /// Rough German/English sound key: "Claude" and "Clawd" both become "klaud" (spec 04 §5.3).
    public static string Sound(string s)
    {
        var t = Letters(s);
        foreach (var (a, b) in SoundRules) t = t.Replace(a, b, StringComparison.Ordinal);
        var output = new StringBuilder();
        foreach (var ch in t)
        {
            if (ch == 'h' && output.Length > 0 && "aeiou".Contains(output[^1])) continue;   // silent h after a vowel
            if (output.Length > 0 && ch == output[^1]) continue;                              // double letters
            output.Append(ch);
        }
        if (output.Length > 3 && output[^1] == 'e') output.Length--;
        return output.ToString();
    }

    /// Standard Jaro-Winkler similarity (prefix up to 4, p = 0.1).
    public static double JaroWinkler(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return a.Length == 0 && b.Length == 0 ? 1 : 0;
        if (a == b) return 1;
        int range = Math.Max(0, Math.Max(a.Length, b.Length) / 2 - 1);
        var m1 = new bool[a.Length];
        var m2 = new bool[b.Length];
        int matches = 0;
        for (int i = 0; i < a.Length; i++)
        {
            int lo = Math.Max(0, i - range), hi = Math.Min(b.Length - 1, i + range);
            for (int j = lo; j <= hi; j++)
            {
                if (m2[j] || a[i] != b[j]) continue;
                m1[i] = true; m2[j] = true; matches++;
                break;
            }
        }
        if (matches == 0) return 0;
        int t = 0, k = 0;
        for (int i = 0; i < a.Length; i++)
        {
            if (!m1[i]) continue;
            while (!m2[k]) k++;
            if (a[i] != b[k]) t++;
            k++;
        }
        double m = matches;
        double jaro = (m / a.Length + m / b.Length + (m - t / 2.0) / m) / 3;
        int prefix = 0;
        for (int i = 0; i < Math.Min(4, Math.Min(a.Length, b.Length)); i++) { if (a[i] == b[i]) prefix++; else break; }
        return jaro + prefix * 0.1 * (1 - jaro);
    }

    /// A token split into leading punctuation, core (first to last letter/digit) and trailing
    /// punctuation: "„Hub-Spot,“" → "„", "Hub-Spot", ",“".
    public readonly record struct Parts(string Leading, string Core, string Trailing);

    /// See <see cref="Parts"/>.
    public static Parts Split(string token)
    {
        int a = 0, b = token.Length;
        while (a < b && !TextUtil.IsAlnum(token[a])) a++;
        while (b > a && !TextUtil.IsAlnum(token[b - 1])) b--;
        return new Parts(token[..a], token[a..b], token[b..]);
    }
}
