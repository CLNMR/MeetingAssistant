using System.Collections.Concurrent;

namespace MeetingAssistant.Core.Text;

/// <summary>
/// Whether a word is a correctly spelled German or English word (spec 04 §5.3 "Known word").
/// Used by the vocabulary to protect ordinary words. The check itself is platform specific: the
/// Windows app sets <see cref="Checker"/> to a Windows Spell Checking API (de-DE, en-US) or
/// Hunspell implementation at start-up. Without a checker no word counts as known.
/// </summary>
public static class SpellCheck
{
    static readonly ConcurrentDictionary<string, bool> Cache = new();
    static Func<string, bool>? _checker;

    /// The platform's raw check ("is this word spelled correctly in German or English?").
    /// Setting it clears the cache.
    public static Func<string, bool>? Checker
    {
        get => _checker;
        set { _checker = value; Cache.Clear(); }
    }

    /// Known word: length &gt; 1, no digits, accepted by the checker (cached). Punctuation around
    /// the word is ignored.
    public static bool IsKnown(string word)
    {
        var w = word.Trim(PunctuationChars(word));
        if (w.Length <= 1 || w.Any(char.IsNumber)) return false;
        var checker = _checker;
        if (checker is null) return false;
        return Cache.GetOrAdd(w, x =>
        {
            try { return checker(x); } catch { return false; }
        });
    }

    static char[] PunctuationChars(string s) => s.Where(char.IsPunctuation).Distinct().ToArray();
}

/// <summary>
/// A spell checker from plain word lists (one word per line) or Hunspell `.dic` files (first line
/// a count, `word/FLAGS` per line; affix rules are not applied, so only listed forms are known).
/// A fallback when the Windows spell checker lacks German or English, and used by msctl.
/// Matching ignores case.
/// </summary>
public sealed class WordListSpellChecker
{
    readonly HashSet<string> _words = new(StringComparer.OrdinalIgnoreCase);

    /// Loads every file that exists.
    public WordListSpellChecker(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            if (!File.Exists(p)) continue;
            foreach (var line in File.ReadLines(p))
            {
                var w = line.Trim();
                var slash = w.IndexOf('/');
                if (slash >= 0) w = w[..slash];
                if (w.Length > 0 && !w.All(char.IsDigit)) _words.Add(w);
            }
        }
    }

    public int Count => _words.Count;

    /// Whether the word is listed.
    public bool IsKnown(string word) => _words.Contains(word);
}
