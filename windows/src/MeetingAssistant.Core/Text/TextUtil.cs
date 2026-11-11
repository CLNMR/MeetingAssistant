using System.Globalization;
using System.Text;

namespace MeetingAssistant.Core.Text;

/// Small string helpers that follow Swift's `Character` semantics where the Mac code counts
/// characters (grapheme clusters), so lengths and cuts match the Mac app.
internal static class TextUtil
{
    /// Letter or digit (Swift `isLetter || isNumber`).
    public static bool IsAlnum(char c) => char.IsLetter(c) || char.IsNumber(c);

    /// Number of user-perceived characters (Swift `String.count`).
    public static int Length(string s) => s.Length == 0 ? 0 : new StringInfo(s).LengthInTextElements;

    /// The last `n` characters (Swift `suffix(n)`).
    public static string Suffix(string s, int n)
    {
        var si = new StringInfo(s);
        var len = si.LengthInTextElements;
        return len <= n ? s : si.SubstringByTextElements(len - n);
    }

    /// The first `n` characters (Swift `prefix(n)`).
    public static string Prefix(string s, int n)
    {
        var si = new StringInfo(s);
        return si.LengthInTextElements <= n ? s : si.SubstringByTextElements(0, n);
    }

    /// Trim spaces and tabs only (Swift `CharacterSet.whitespaces`: whitespace without line breaks).
    public static string TrimSpaces(string s)
    {
        int a = 0, b = s.Length;
        while (a < b && IsSpaceNotNewline(s[a])) a++;
        while (b > a && IsSpaceNotNewline(s[b - 1])) b--;
        return s.Substring(a, b - a);
    }

    static bool IsSpaceNotNewline(char c) => char.IsWhiteSpace(c) && c is not ('\n' or '\r' or '\u2028' or '\u2029' or '\u0085' or '\u000B' or '\u000C');

    /// Split on one separator character, omitting empty parts (Swift `split(separator:)`).
    public static string[] SplitNonEmpty(string s, char sep) => s.Split(sep, StringSplitOptions.RemoveEmptyEntries);

    /// Split where `isSeparator` holds, omitting empty parts (Swift `split(whereSeparator:)`).
    public static List<string> SplitWhere(string s, Func<char, bool> isSeparator)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            if (isSeparator(c)) { if (sb.Length > 0) { parts.Add(sb.ToString()); sb.Clear(); } }
            else sb.Append(c);
        }
        if (sb.Length > 0) parts.Add(sb.ToString());
        return parts;
    }
}
