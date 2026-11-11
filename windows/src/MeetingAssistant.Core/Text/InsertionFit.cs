namespace MeetingAssistant.Core.Text;

/// <summary>
/// Fits dictated text into the text around the cursor (spec 04 §3.3): a space where words would
/// touch, a capital at the start of a sentence, a lowercase function word in the middle of one,
/// and no final period when the sentence goes on after the cursor. Only the edges change.
/// </summary>
public static class InsertionFit
{
    /// The text fitted between `before` and `after` (the field's text around the cursor).
    public static string Fit(string text, string before, string after)
    {
        var t = text.Trim();
        if (t.Length == 0) return text;
        var b = before.Replace('\u00A0', ' ');
        var trimmedBefore = b.Trim(' ', '\t');

        // Capitalization at the start.
        if (StartsSentence(trimmedBefore)) t = UppercasingFirst(t);
        else if (ContinuesSentence(trimmedBefore)) t = LowercasingFunctionWord(t);

        // Final period when the sentence goes on after the cursor.
        if (SentenceContinues(after) && t.EndsWith('.') && !t.EndsWith("..", StringComparison.Ordinal) && EndsWithWord(t[..^1]))
            t = t[..^1];

        // Spaces.
        if (b.Length > 0 && !char.IsWhiteSpace(b[^1]) && !"([{„“‚‘«\"'/-".Contains(b[^1])
            && t.Length > 0 && !",.;:!?)]}…".Contains(t[0]))
            t = " " + t;
        if (after.Length > 0 && (TextUtil.IsAlnum(after[0]) || "„(\"".Contains(after[0]))
            && t.Length > 0 && !char.IsWhiteSpace(t[^1]) && !"(/-".Contains(t[^1]))
            t += " ";
        return t;
    }

    static bool StartsSentence(string before)
    {
        if (before.Length == 0) return true;
        return ".!?\n\r•".Contains(before[^1]) || before.EndsWith(". ", StringComparison.Ordinal) || before.EndsWith('\n');
    }

    static bool ContinuesSentence(string before)
    {
        if (before.Length == 0) return false;
        var last = before[^1];
        return TextUtil.IsAlnum(last) || ",;–-(".Contains(last);
    }

    /// Lowercase letter first on the same line right after the cursor.
    static bool SentenceContinues(string after)
    {
        var nl = after.IndexOf('\n');
        var line = TextUtil.TrimSpaces(nl < 0 ? after : after[..nl]);
        if (line.Length == 0) return false;
        return char.IsLetter(line[0]) && char.IsLower(line[0]);
    }

    /// The last word has four letters or more, all letters, so "usw." and "z. B." keep their period.
    static bool EndsWithWord(string text)
    {
        var parts = TextUtil.SplitNonEmpty(text, ' ');
        var word = parts.Length > 0 ? parts[^1] : "";
        return TextUtil.Length(word) >= 4 && word.All(char.IsLetter);
    }

    static string UppercasingFirst(string t)
    {
        int i = 0;
        while (i < t.Length && !char.IsLetter(t[i])) i++;
        if (i >= t.Length || !char.IsLower(t[i])) return t;
        return t[..i] + char.ToUpperInvariant(t[i]).ToString() + t[(i + 1)..];
    }

    /// "Und dann" → "und dann" mid-sentence. Nouns keep their capital, and so do "Sie", "Ihr", "I".
    static string LowercasingFunctionWord(string t)
    {
        int n = 0;
        while (n < t.Length && char.IsLetter(t[n])) n++;
        var word = t[..n];
        if (word.Length == 0 || !char.IsUpper(word[0]) || !FunctionWords.Contains(word.ToLowerInvariant())
            || word.Skip(1).Any(char.IsUpper)) return t;
        return word.ToLowerInvariant() + t[n..];
    }

    /// German and English function words lowercased mid-sentence.
    public static readonly IReadOnlySet<string> FunctionWords = new HashSet<string>
    {
        // German
        "und", "oder", "aber", "denn", "doch", "sondern", "dass", "weil", "wenn", "als", "ob", "damit", "obwohl", "dann",
        "also", "auch", "noch", "schon", "nur", "sogar", "eben", "halt", "ja", "nein", "nicht", "kein", "keine", "der", "die",
        "das", "den", "dem", "des", "ein", "eine", "einen", "einem", "einer", "ich", "du", "er", "es", "wir", "mir", "mich",
        "dir", "dich", "uns", "euch", "ihm", "ihn", "in", "im", "an", "am", "auf", "aus", "bei", "mit", "nach", "von", "vom",
        "zu", "zum", "zur", "für", "über", "unter", "vor", "hinter", "neben", "zwischen", "bis", "wie", "was", "wer", "wo",
        "warum", "welche", "welcher", "welches", "so", "da", "hier", "dort", "jetzt", "heute", "morgen", "bitte", "danke",
        "mal", "man", "sich", "sein", "ist", "sind", "war", "hat", "habe", "haben", "wird", "werden", "kann", "können",
        // English
        "and", "or", "but", "so", "because", "if", "when", "that", "which", "who", "then", "also", "the", "a", "an", "to",
        "of", "in", "on", "at", "for", "with", "from", "by", "as", "is", "are", "was", "it", "we", "you", "he", "she", "they",
        "this", "these", "those", "there", "here", "not", "please", "thanks",
    };
}
