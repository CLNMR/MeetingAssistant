using System.Globalization;
using System.Text;

namespace MeetingAssistant.Core.Assist;

/// <summary>Small text helpers matching Swift's Foundation behaviour (whitespace sets, grapheme counts, folding).</summary>
public static class AssistText
{
    /// Swift's <c>CharacterSet.whitespaces</c>: tab and the Unicode space separators (no newlines).
    public static bool IsWs(char c) => c == '\t' || char.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator;

    /// Swift's <c>CharacterSet.newlines</c>.
    public static bool IsNewline(char c) => c is '\n' or '\r' or '\u000B' or '\u000C' or '\u0085' or (char)0x2028 or (char)0x2029;

    /// Trims <c>.whitespaces</c> (not newlines).
    public static string TrimWs(string s)
    {
        int a = 0, b = s.Length;
        while (a < b && IsWs(s[a])) a++;
        while (b > a && IsWs(s[b - 1])) b--;
        return s[a..b];
    }

    /// Trims <c>.whitespacesAndNewlines</c>.
    public static string TrimAll(string s)
    {
        int a = 0, b = s.Length;
        while (a < b && (IsWs(s[a]) || IsNewline(s[a]) || char.IsWhiteSpace(s[a]))) a++;
        while (b > a && (IsWs(s[b - 1]) || IsNewline(s[b - 1]) || char.IsWhiteSpace(s[b - 1]))) b--;
        return s[a..b];
    }

    /// Splits like Swift's <c>components(separatedBy: .newlines)</c>: every newline character separates.
    public static string[] Lines(string s) => s.Split(new[] { '\n', '\r', '\u000B', '\u000C', '\u0085', (char)0x2028, (char)0x2029 });

    /// Number of user-perceived characters (Swift's <c>String.count</c>).
    public static int Count(string s) => new StringInfo(s).LengthInTextElements;

    /// The first <paramref name="n"/> user-perceived characters.
    public static string Prefix(string s, int n)
    {
        var si = new StringInfo(s);
        return n >= si.LengthInTextElements ? s : si.SubstringByTextElements(0, n);
    }

    /// Case- and diacritic-insensitive folding, whitespace collapsed (for name matching).
    public static string Fold(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in d) if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        var lowered = sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
        return string.Join(" ", lowered.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// Equal ignoring case and diacritics.
    public static bool SameName(string a, string b) =>
        CultureInfo.InvariantCulture.CompareInfo.Compare(a, b, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) == 0;

    /// Words split on single spaces, empty parts dropped (Swift's <c>split(separator: " ")</c>).
    public static int WordCount(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
}
