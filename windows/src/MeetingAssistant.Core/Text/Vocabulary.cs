using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MeetingAssistant.Core.Json;

namespace MeetingAssistant.Core.Text;

/// <summary>
/// Names and terms spelled the user's way (spec 04 §5): the user's terms, fixed replacements
/// and names from Aya. Stored as `vocabulary.json` in the app data folder, in exactly the Mac
/// format (all keys optional on read).
/// </summary>
public sealed class Vocabulary : IJsonOnDeserialized
{
    /// A fixed rule `spoken → written` ("n acht n" → "n8n").
    public sealed record Replacement(
        [property: JsonPropertyName("spoken")] string Spoken,
        [property: JsonPropertyName("written")] string Written);

    /// The user's own terms.
    public List<string> Terms { get; set; } = new();
    public List<Replacement> Replacements { get; set; } = new();
    /// Names from Aya, refreshed with the lists.
    public List<string> AyaTerms { get; set; } = new();
    public DateTime? AyaFetchedAt { get; set; }
    /// How the Aya names were cleaned; lists of an older version are rebuilt.
    public int AyaTermsVersion { get; set; }
    public bool UseAya { get; set; } = true;
    /// Also correct words that sound like a term.
    public bool Fuzzy { get; set; } = true;

    /// Bump on both platforms together whenever <see cref="TermsFromAya"/> changes.
    public const int CurrentAyaTermsVersion = 2;
    /// Aya names older than this are fetched again (spec 04 §5.1 Refresh).
    public static readonly TimeSpan AyaRefreshInterval = TimeSpan.FromHours(12);

    void IJsonOnDeserialized.OnDeserialized()
    {
        Terms ??= new(); Replacements ??= new(); AyaTerms ??= new();
    }

    /// Your terms + Aya terms (when enabled).
    [JsonIgnore] public List<string> ActiveTerms => UseAya ? Terms.Concat(AyaTerms).ToList() : Terms.ToList();
    [JsonIgnore] public bool IsEmpty => ActiveTerms.Count == 0 && Replacements.Count == 0;

    public Vocabulary Clone() => new()
    {
        Terms = Terms.ToList(), Replacements = Replacements.ToList(), AyaTerms = AyaTerms.ToList(), AyaFetchedAt = AyaFetchedAt,
        AyaTermsVersion = AyaTermsVersion, UseAya = UseAya, Fuzzy = Fuzzy,
    };

    // MARK: storage

    /// `<AppData>/vocabulary.json`.
    public static string FilePath => AppPaths.VocabularyFile;

    /// The stored vocabulary, or an empty one when the file is missing or unreadable.
    public static Vocabulary Load(string? path = null)
    {
        path ??= FilePath;
        try { return File.Exists(path) ? JsonIO.Read<Vocabulary>(path) : new Vocabulary(); }
        catch { return new Vocabulary(); }
    }

    /// Writes the file atomically (pretty, sorted keys).
    public void Save(string? path = null) => JsonIO.Write(this, path ?? FilePath);

    /// The matcher for the active terms.
    public VocabularyMatcher Matcher(Func<string, bool>? isKnownWord = null) =>
        new(ActiveTerms, Replacements, Fuzzy, isKnownWord);

    // MARK: editing (Settings › Vocabulary)

    /// Adds a term (trimmed; blank or duplicate terms are ignored). Returns whether it was added.
    public bool AddTerm(string term)
    {
        var t = term.Trim();
        if (t.Length == 0 || Terms.Contains(t)) return false;
        Terms.Add(t);
        return true;
    }

    /// Adds a replacement (both sides trimmed and required; duplicates ignored).
    public bool AddReplacement(string spoken, string written)
    {
        var r = new Replacement(spoken.Trim(), written.Trim());
        if (r.Spoken.Length == 0 || r.Written.Length == 0 || Replacements.Contains(r)) return false;
        Replacements.Add(r);
        return true;
    }

    // MARK: terms from Aya

    /// Whether the Aya names should be fetched again after the Aya lists were loaded: names
    /// enabled and older than 12 h, or stored with another <see cref="AyaTermsVersion"/>.
    /// (Mac `AssistController`: a version mismatch triggers a refresh even with names off.)
    public bool NeedsAyaRefresh(DateTime now) =>
        (UseAya && now - (AyaFetchedAt ?? DateTime.MinValue) > AyaRefreshInterval) || AyaTermsVersion != CurrentAyaTermsVersion;

    /// Stores freshly built Aya terms. An empty list does not overwrite the stored terms.
    /// Returns whether anything was stored.
    public bool SetAyaTerms(IReadOnlyList<string> terms, DateTime now)
    {
        if (terms.Count == 0) return false;
        AyaTerms = terms.ToList();
        AyaFetchedAt = DateTime.SpecifyKind(now.ToUniversalTime(), DateTimeKind.Utc);
        AyaTermsVersion = CurrentAyaTermsVersion;
        return true;
    }

    /// Distinctive names from Aya record titles (spec 04 §5.1): company names without the legal
    /// form, full names of people and their last names, short project names. The caller passes
    /// people titles without `@` already removed or not; titles containing `@` are dropped here too.
    public static List<string> TermsFromAya(IEnumerable<string> companies, IEnumerable<string> people, IEnumerable<string> projects)
    {
        var output = new List<string>();
        foreach (var c in companies)
            foreach (var part in NameParts(c))
            {
                var name = StripLegalForm(part);
                if (name.Length > 0) output.Add(name);
            }
        foreach (var p in people)
        {
            if (p.Contains('@')) continue;
            foreach (var name in NameParts(p))
            {
                output.Add(name);
                var parts = TextUtil.SplitNonEmpty(name, ' ');
                if (parts.Length > 1 && TextUtil.Length(parts[^1]) >= 4) output.Add(parts[^1]);
            }
        }
        foreach (var p in projects)
            output.AddRange(NameParts(p).Where(x => TextUtil.SplitNonEmpty(x, ' ').Length <= 3));

        var seen = new HashSet<string>();
        return output.Where(t =>
        {
            var k = VocabularyMatcher.Letters(t);
            if (k.Length < 3 || TextUtil.SplitNonEmpty(t, ' ').Length > 4 || t.Contains('@') || seen.Contains(k)) return false;
            seen.Add(k);
            return true;
        }).ToList();
    }

    static readonly Regex CodePattern = new(@"^[A-Za-z]{0,3}\d{2,}$", RegexOptions.CultureInvariant);
    static readonly string[] AlternativeSeparators = { " – ", " — ", " - ", " / ", "/", ";", "|", "," };

    /// "🤝 Muster GmbH (Notiz) – Werk Nord / Muster Süd" → ["Muster GmbH", "Werk Nord", "Muster Süd"]:
    /// no emoji or symbols in front, no notes in parentheses, alternatives split apart, codes such
    /// as "P0016" dropped.
    public static List<string> NameParts(string title)
    {
        var t = title;
        while (true)
        {
            int open = t.IndexOfAny(new[] { '(', '[' });
            if (open < 0) break;
            int close = t.IndexOfAny(new[] { ')', ']' }, open);
            t = close < 0 ? t[..open] : t.Remove(open, close - open + 1);
        }
        foreach (var sep in AlternativeSeparators) t = t.Replace(sep, "\u001F", StringComparison.Ordinal);
        var result = new List<string>();
        foreach (var raw in t.Split('\u001F', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = raw.Trim();
            while (p.Length > 0)
            {
                var r = Rune.GetRuneAt(p, 0);
                if (IsAlphanumeric(r)) break;
                p = p[r.Utf16SequenceLength..];
            }
            p = p.Trim();
            if (p.Length == 0 || CodePattern.IsMatch(p)) continue;
            result.Add(p);
        }
        return result;
    }

    /// Like Foundation's `CharacterSet.alphanumerics`: letters, marks and numbers.
    static bool IsAlphanumeric(Rune r)
    {
        var cat = Rune.GetUnicodeCategory(r);
        return Rune.IsLetterOrDigit(r) || cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.EnclosingMark or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber;
    }

    /// Legal forms removed from company names (spec 04 §5.1).
    public static readonly IReadOnlyList<string> LegalForms = new[]
    {
        "gmbh & co. kg", "gmbh & co kg", "gmbh & co. kgaa", "ag & co. kg", "ug (haftungsbeschränkt)", "gmbh", "mbh", "ag", "kg",
        "kgaa", "ohg", "gbr", "ug", "se", "e.v.", "ev", "e.k.", "eg", "ltd.", "ltd", "inc.", "inc", "llc", "s.a.", "b.v.", "plc", "co.",
    };

    static readonly char[] LegalTrim = { ' ', ',', '-', '–' };

    /// "MUSTER SOLAR GmbH" → "MUSTER SOLAR" (repeatedly, case-insensitive).
    public static string StripLegalForm(string name)
    {
        var s = name.Trim();
        bool changed = true;
        while (changed)
        {
            changed = false;
            var lower = s.ToLowerInvariant();
            foreach (var f in LegalForms)
            {
                if (!(lower.EndsWith(" " + f, StringComparison.Ordinal) || lower == f)) continue;
                s = s[..(s.Length - f.Length)].Trim(LegalTrim);
                changed = true;
                break;
            }
        }
        return s;
    }
}
