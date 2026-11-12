using System.Text;

namespace MeetingAssistant.Core.Text;

/// Inline style of a run of text.
[Flags]
public enum MarkdownStyle { None = 0, Bold = 1, Italic = 2, Strike = 4, Code = 8 }

/// A piece of inline text with one style; <c>Link</c> is the target of a <c>[text](url)</c> link.
public sealed record MarkdownRun(string Text, MarkdownStyle Style = MarkdownStyle.None, string? Link = null);

public enum MarkdownMarkerKind { Bullet, Number, Task }

public readonly record struct MarkdownMarker(MarkdownMarkerKind Kind, int Number = 0, bool Done = false)
{
    public static readonly MarkdownMarker Bullet = new(MarkdownMarkerKind.Bullet);
    public static MarkdownMarker Num(int n) => new(MarkdownMarkerKind.Number, n);
    public static MarkdownMarker Task(bool done) => new(MarkdownMarkerKind.Task, Done: done);
}

/// One block of the preview. Paragraph, quote and item texts keep their line breaks ("\n").
public abstract record MarkdownBlock;
public sealed record MarkdownHeading(int Level, string Text) : MarkdownBlock;
public sealed record MarkdownParagraph(string Text) : MarkdownBlock;
public sealed record MarkdownItem(int Depth, MarkdownMarker Marker, string Text) : MarkdownBlock;
public sealed record MarkdownQuote(string Text) : MarkdownBlock;
public sealed record MarkdownCode(string Text) : MarkdownBlock;
public sealed record MarkdownTable(IReadOnlyList<string> Header, IReadOnlyList<IReadOnlyList<string>> Rows) : MarkdownBlock;
public sealed record MarkdownRule : MarkdownBlock;

public enum MarkdownSpanKind { Heading, Quote, CodeBlock, Table, Syntax, ListMarker, Styled }

/// A highlighted range of the editor text. Line kinds cover the whole line; the renderer applies the spans in
/// order, so inline styles and syntax colouring come after the line they are on.
public readonly record struct MarkdownSpan(int Start, int Length, MarkdownSpanKind Kind, int Level = 0,
                                           MarkdownStyle Style = MarkdownStyle.None, bool Link = false);

/// Text plus selection after an editing command.
public readonly record struct MarkdownEditResult(string Text, int SelectionStart, int SelectionLength);

public enum MarkdownLineStyle { Heading, Bullet, Numbered, Task, Quote }

/// <summary>
/// Markdown as the recap fields use it (a GitHub-flavoured subset): the blocks and inline runs the preview shows,
/// the highlighting of the editor, and the editing commands of the Markdown editor (spec 03 §8.2). Positions are
/// UTF-16 offsets, the same as on the Mac.
/// </summary>
public static class Markdown
{
    // ---- Lines ----

    internal enum LineKind { Blank, Text, Heading, Bullet, Number, Task, Quote, Fence, Rule, Table }

    /// How one line starts. <c>Indent</c> and <c>Prefix</c> are UTF-16 lengths; <c>Column</c> counts a tab as 4.
    internal readonly record struct Line(LineKind Kind, int Indent, int Column, int Prefix, int ContentEnd,
                                         int Level = 0, int Number = 0, bool Done = false, char Delimiter = '\0')
    {
        public bool IsListItem => Kind is LineKind.Bullet or LineKind.Number or LineKind.Task;
    }

    static bool IsSpace(char c) => c is ' ' or '\t' or '\n' or '\r';
    static bool IsDigit(char c) => c is >= '0' and <= '9';
    static bool IsAlnum(char c) => c < 128 ? char.IsAsciiLetterOrDigit(c) : char.IsSurrogate(c) || char.IsLetterOrDigit(c);
    static bool IsPunct(char c) => c is (>= '!' and <= '/') or (>= ':' and <= '@') or (>= '[' and <= '`') or (>= '{' and <= '~');

    /// Classifies <c>s[b..e)</c>.
    internal static Line Classify(string s, int b, int e)
    {
        int i = b, col = 0;
        while (i < e && (s[i] == ' ' || s[i] == '\t')) { col += s[i] == '\t' ? 4 : 1; i++; }
        int ind = i - b;
        Line L(LineKind k, int p, int? end = null, int level = 0, int num = 0, bool done = false, char d = '\0') =>
            new(k, ind, col, p, end ?? e - b, level, num, done, d);
        if (i >= e) return L(LineKind.Blank, ind);
        char c = s[i];
        char? At(int k) => i + k < e ? s[i + k] : null;
        bool SpaceOrEnd(int k) => At(k) is null or ' ' or '\t';

        if ((c == '`' || c == '~') && At(1) == c && At(2) == c) return L(LineKind.Fence, ind);
        if (c == '#' && col < 4)
        {
            int n = 0;
            while (At(n) == '#') n++;
            if (n <= 6 && SpaceOrEnd(n))
            {
                int p = Math.Min(i + n + 1, e) - b;
                int end = e;
                while (end > b + p && IsSpace(s[end - 1])) end--;
                int h = end;
                while (h > b + p && s[h - 1] == '#') h--;
                if (h < end && (h == b + p || s[h - 1] == ' ')) { end = h; while (end > b + p && IsSpace(s[end - 1])) end--; }
                return L(LineKind.Heading, p, end - b, level: n);
            }
        }
        if (c is '-' or '*' or '_')
        {
            int n = 0; bool other = false;
            for (int k = i; k < e; k++) { if (s[k] == c) n++; else if (s[k] != ' ' && s[k] != '\t') { other = true; break; } }
            if (!other && n >= 3) return L(LineKind.Rule, ind);
        }
        if (c is '-' or '*' or '+' && SpaceOrEnd(1))
        {
            if (At(2) == '[' && At(3) is ' ' or 'x' or 'X' && At(4) == ']' && SpaceOrEnd(5))
                return L(LineKind.Task, Math.Min(i + 6, e) - b, done: At(3) != ' ', d: c);
            return L(LineKind.Bullet, Math.Min(i + 2, e) - b, d: c);
        }
        if (IsDigit(c))
        {
            int n = 0;
            while (At(n) is char d0 && IsDigit(d0) && n < 9) n++;
            if (At(n) is char d && (d == '.' || d == ')') && SpaceOrEnd(n + 1))
                return L(LineKind.Number, Math.Min(i + n + 2, e) - b, num: int.Parse(s.AsSpan(i, n)), d: d);
        }
        if (c == '>') return L(LineKind.Quote, (At(1) == ' ' ? i + 2 : i + 1) - b);
        if (c == '|') return L(LineKind.Table, ind);
        return L(LineKind.Text, ind);
    }

    /// The text split into lines: where each starts and ends (before "\n", and a "\r" before it).
    sealed class Lines
    {
        public readonly string S;
        public readonly List<int> Starts = new(), Ends = new();
        public Lines(string text)
        {
            S = text;
            int st = 0;
            for (int k = 0; k < text.Length; k++)
                if (text[k] == '\n') { Starts.Add(st); Ends.Add(k > st && text[k - 1] == '\r' ? k - 1 : k); st = k + 1; }
            Starts.Add(st); Ends.Add(text.Length);
        }
        public int Count => Starts.Count;
        public string Str(int i) => S[Starts[i]..Ends[i]];
        public string Str(int a, int b) => a < b ? S[a..b] : "";
        public Line Classify(int i) => Markdown.Classify(S, Starts[i], Ends[i]);
        /// Index of the line containing offset <paramref name="o"/>.
        public int LineAt(int o)
        {
            int lo = 0, hi = Count - 1;
            while (lo < hi) { int m = (lo + hi + 1) / 2; if (Starts[m] <= o) lo = m; else hi = m - 1; }
            return lo;
        }
    }

    // ---- Blocks ----

    /// The blocks of a Markdown text for the preview.
    public static List<MarkdownBlock> Blocks(string text)
    {
        var L = new Lines(text);
        var output = new List<MarkdownBlock>();
        var para = new List<string>();
        var listColumns = new List<int>();
        bool lastWasBlank = false;
        void Flush() { if (para.Count > 0) { output.Add(new MarkdownParagraph(string.Join("\n", para))); para.Clear(); } }

        for (int i = 0; i < L.Count; i++)
        {
            var ln = L.Classify(i);
            int s = L.Starts[i];
            switch (ln.Kind)
            {
                case LineKind.Blank:
                    Flush(); lastWasBlank = true; continue;
                case LineKind.Fence:
                {
                    Flush(); listColumns.Clear();
                    char open = L.S[s + ln.Indent];
                    var body = new List<string>();
                    i++;
                    while (i < L.Count)
                    {
                        var l2 = L.Classify(i);
                        if (l2.Kind == LineKind.Fence && L.S[L.Starts[i] + l2.Indent] == open) break;
                        body.Add(L.Str(i)); i++;
                    }
                    output.Add(new MarkdownCode(string.Join("\n", body)));
                    break;
                }
                case LineKind.Heading:
                    Flush(); listColumns.Clear();
                    output.Add(new MarkdownHeading(ln.Level, L.Str(s + ln.Prefix, s + ln.ContentEnd).Trim()));
                    break;
                case LineKind.Rule:
                    Flush(); listColumns.Clear();
                    output.Add(new MarkdownRule());
                    break;
                case LineKind.Bullet or LineKind.Number or LineKind.Task:
                {
                    Flush();
                    while (listColumns.Count > 0 && listColumns[^1] > ln.Column) listColumns.RemoveAt(listColumns.Count - 1);
                    if (listColumns.Count == 0 || listColumns[^1] != ln.Column) listColumns.Add(ln.Column);
                    var marker = ln.Kind switch
                    {
                        LineKind.Number => MarkdownMarker.Num(ln.Number),
                        LineKind.Task => MarkdownMarker.Task(ln.Done),
                        _ => MarkdownMarker.Bullet,
                    };
                    output.Add(new MarkdownItem(listColumns.Count - 1, marker, L.Str(s + ln.Prefix, L.Ends[i]).Trim()));
                    break;
                }
                case LineKind.Quote:
                {
                    Flush(); listColumns.Clear();
                    var body = new List<string> { L.Str(s + ln.Prefix, L.Ends[i]) };
                    while (i + 1 < L.Count && L.Classify(i + 1).Kind == LineKind.Quote)
                    {
                        i++; var l2 = L.Classify(i); body.Add(L.Str(L.Starts[i] + l2.Prefix, L.Ends[i]));
                    }
                    output.Add(new MarkdownQuote(string.Join("\n", body)));
                    break;
                }
                default: // Text, Table
                    if (i + 1 < L.Count && L.Str(i).Contains('|') && IsDelimiterRow(L.Str(i + 1)))
                    {
                        Flush(); listColumns.Clear();
                        var header = Cells(L.Str(i));
                        var rows = new List<IReadOnlyList<string>>();
                        i++;
                        while (i + 1 < L.Count && L.Classify(i + 1).Kind != LineKind.Blank && L.Str(i + 1).Contains('|'))
                        {
                            i++;
                            var r = Cells(L.Str(i));
                            while (r.Count < header.Count) r.Add("");
                            rows.Add(r.Take(header.Count).ToList());
                        }
                        output.Add(new MarkdownTable(header, rows));
                    }
                    else if (para.Count == 0 && !lastWasBlank && ln.Column > 0 && output.Count > 0 && output[^1] is MarkdownItem it)
                    {
                        // An indented line right after a list item continues it.
                        output[^1] = it with { Text = it.Text + "\n" + L.Str(i).Trim() };
                    }
                    else
                    {
                        if (para.Count == 0) listColumns.Clear();
                        para.Add(L.Str(i).Trim());
                    }
                    break;
            }
            lastWasBlank = false;
        }
        Flush();
        return output;
    }

    internal static bool IsDelimiterRow(string s)
    {
        var t = s.Trim();
        if (t.Length == 0 || !t.Contains('-')) return false;
        var parts = t.Split('|').Select(p => p.Trim()).ToList();
        if (t.StartsWith('|')) parts.RemoveAt(0);
        if (t.EndsWith('|') && parts.Count > 0) parts.RemoveAt(parts.Count - 1);
        if (parts.Count == 0) return false;
        return parts.All(p =>
        {
            var q = p;
            if (q.StartsWith(':')) q = q[1..];
            if (q.EndsWith(':')) q = q[..^1];
            return q.Length > 0 && q.All(ch => ch == '-');
        });
    }

    /// Cells of a table row: outer pipes dropped, split at unescaped pipes, trimmed, <c>\|</c> unescaped.
    internal static List<string> Cells(string row)
    {
        var t = row.Trim();
        if (t.StartsWith('|')) t = t[1..];
        if (t.EndsWith('|') && !t.EndsWith("\\|")) t = t[..^1];
        var output = new List<string>();
        var cur = new StringBuilder();
        bool esc = false;
        foreach (var ch in t)
        {
            if (esc) { if (ch == '|') cur.Append('|'); else cur.Append('\\').Append(ch); esc = false; continue; }
            if (ch == '\\') { esc = true; continue; }
            if (ch == '|') { output.Add(cur.ToString().Trim()); cur.Clear(); continue; }
            cur.Append(ch);
        }
        if (esc) cur.Append('\\');
        output.Add(cur.ToString().Trim());
        return output;
    }

    // ---- Inline ----

    readonly record struct Piece(int Lo, int Hi, MarkdownStyle Style, string? Link, bool Syntax);

    sealed class InlineScanner(string u)
    {
        public readonly List<Piece> Out = new();

        int Run(int k, char c, int hi) { int n = 0; while (k + n < hi && u[k + n] == c) n++; return n; }

        public void Scan(int lo, int hi, MarkdownStyle style = MarkdownStyle.None, string? link = null)
        {
            int i = lo, textStart = lo;
            void Flush(int end) { if (end > textStart) Out.Add(new Piece(textStart, end, style, link, false)); }
            void Syntax(int a, int b) => Out.Add(new Piece(a, b, style, link, true));

            while (i < hi)
            {
                char c = u[i];
                if (c == '\\' && i + 1 < hi && IsPunct(u[i + 1]))
                {
                    Flush(i); Syntax(i, i + 1); textStart = i + 1; i += 2; continue;
                }
                if (c == '`')
                {
                    int n = Run(i, c, hi);
                    if (ClosingTicks(i + n, n, hi) is int j)
                    {
                        Flush(i); Syntax(i, i + n);
                        Out.Add(new Piece(i + n, j, style | MarkdownStyle.Code, link, false));
                        Syntax(j, j + n); i = j + n; textStart = i;
                    }
                    else i += n;
                    continue;
                }
                if (c is '*' or '_' or '~')
                {
                    int n = Run(i, c, hi);
                    int d = c == '~' ? (n == 2 ? 2 : 0) : Math.Min(n, 3);
                    bool prevOk = c != '_' || i == 0 || !IsAlnum(u[i - 1]);
                    if (d > 0 && n == d && prevOk && i + d < hi && !IsSpace(u[i + d]) && ClosingRun(c, d, i + d, hi) is int k)
                    {
                        var add = c == '~' ? MarkdownStyle.Strike : d == 1 ? MarkdownStyle.Italic : d == 2 ? MarkdownStyle.Bold : MarkdownStyle.Bold | MarkdownStyle.Italic;
                        Flush(i); Syntax(i, i + d);
                        Scan(i + d, k, style | add, link);
                        Syntax(k, k + d); i = k + d; textStart = i;
                    }
                    else i += n;
                    continue;
                }
                if (c == '[' && LinkEnds(i, hi) is (int j2, int k2))
                {
                    Flush(i); Syntax(i, i + 1);
                    var url = u[(j2 + 2)..k2].Trim();
                    Scan(i + 1, j2, style, url);
                    Syntax(j2, k2 + 1); i = k2 + 1; textStart = i;
                    continue;
                }
                i++;
            }
            Flush(hi);
        }

        int? ClosingTicks(int a, int n, int hi)
        {
            int k = a;
            while (k < hi)
            {
                if (u[k] == '`')
                {
                    int m = Run(k, '`', hi);
                    if (m == n && k > a) return k;
                    k += m;
                }
                else k++;
            }
            return null;
        }

        /// The closing delimiter: a run of exactly <paramref name="d"/> characters, not after whitespace, with content before it.
        int? ClosingRun(char c, int d, int a, int hi)
        {
            int k = a;
            while (k < hi)
            {
                char ch = u[k];
                if (ch == '\\') { k += 2; continue; }
                if (ch == '`')
                {
                    int m = Run(k, '`', hi);
                    k = ClosingTicks(k + m, m, hi) is int j ? j + m : k + m;
                    continue;
                }
                if (ch == c)
                {
                    int m = Run(k, c, hi);
                    bool nextOk = c != '_' || k + m >= hi || !IsAlnum(u[k + m]);
                    if (m == d && k > a && !IsSpace(u[k - 1]) && nextOk) return k;
                    k += m; continue;
                }
                k++;
            }
            return null;
        }

        /// <c>[text](url)</c>: the positions of <c>]</c> and <c>)</c>.
        (int, int)? LinkEnds(int i, int hi)
        {
            int depth = 0, j = i;
            while (j < hi)
            {
                if (u[j] == '\\') { j += 2; continue; }
                if (u[j] == '[') depth++;
                if (u[j] == ']') { depth--; if (depth == 0) break; }
                j++;
            }
            if (j >= hi || j <= i + 1 || j + 1 >= hi || u[j + 1] != '(') return null;
            int k = j + 2;
            while (k < hi && u[k] != ')' && u[k] != ' ') k++;
            if (k >= hi || u[k] != ')' || k <= j + 2) return null;
            return (j, k);
        }
    }

    /// The inline runs of a text (bold, italic, strikethrough, code, links), with the markup removed and adjacent
    /// runs of the same style joined.
    public static List<MarkdownRun> Inline(string text)
    {
        var sc = new InlineScanner(text);
        sc.Scan(0, text.Length);
        var runs = new List<MarkdownRun>();
        foreach (var p in sc.Out)
        {
            if (p.Syntax) continue;
            var s = text[p.Lo..p.Hi];
            if (runs.Count > 0 && runs[^1].Style == p.Style && runs[^1].Link == p.Link) runs[^1] = runs[^1] with { Text = runs[^1].Text + s };
            else runs.Add(new MarkdownRun(s, p.Style, p.Link));
        }
        return runs;
    }

    /// Highlighting of the editor text.
    public static List<MarkdownSpan> Highlight(string text)
    {
        var L = new Lines(text);
        var spans = new List<MarkdownSpan>();
        char? fence = null;
        var tableRows = new HashSet<int>();
        // Table rows: a header with a delimiter row below it, and the rows that follow.
        for (int i = 0; i < L.Count;)
        {
            if (i + 1 < L.Count && L.Str(i).Contains('|') && IsDelimiterRow(L.Str(i + 1)))
            {
                tableRows.Add(i); tableRows.Add(i + 1); i += 2;
                while (i < L.Count && L.Classify(i).Kind != LineKind.Blank && L.Str(i).Contains('|')) { tableRows.Add(i); i++; }
            }
            else i++;
        }
        void Inl(int a, int b)
        {
            if (b <= a) return;
            var sc = new InlineScanner(text);
            sc.Scan(a, b);
            foreach (var p in sc.Out)
            {
                if (p.Hi <= p.Lo) continue;
                if (p.Syntax) spans.Add(new MarkdownSpan(p.Lo, p.Hi - p.Lo, MarkdownSpanKind.Syntax));
                else if (p.Style != MarkdownStyle.None || p.Link is not null)
                    spans.Add(new MarkdownSpan(p.Lo, p.Hi - p.Lo, MarkdownSpanKind.Styled, Style: p.Style, Link: p.Link is not null));
            }
        }
        for (int i = 0; i < L.Count; i++)
        {
            int s = L.Starts[i], e = L.Ends[i];
            var ln = L.Classify(i);
            if (fence is char f)
            {
                spans.Add(new MarkdownSpan(s, e - s, MarkdownSpanKind.CodeBlock));
                if (ln.Kind == LineKind.Fence && text[s + ln.Indent] == f) { spans.Add(new MarkdownSpan(s, e - s, MarkdownSpanKind.Syntax)); fence = null; }
                continue;
            }
            if (tableRows.Contains(i))
            {
                spans.Add(new MarkdownSpan(s, e - s, MarkdownSpanKind.Table));
                Inl(s, e);
                for (int k = s; k < e; k++)
                    if (text[k] == '|' && (k == s || text[k - 1] != '\\')) spans.Add(new MarkdownSpan(k, 1, MarkdownSpanKind.Syntax));
                if (IsDelimiterRow(L.Str(i))) spans.Add(new MarkdownSpan(s, e - s, MarkdownSpanKind.Syntax));
                continue;
            }
            switch (ln.Kind)
            {
                case LineKind.Fence:
                    fence = text[s + ln.Indent];
                    spans.Add(new MarkdownSpan(s, e - s, MarkdownSpanKind.CodeBlock));
                    spans.Add(new MarkdownSpan(s, e - s, MarkdownSpanKind.Syntax));
                    break;
                case LineKind.Heading:
                    spans.Add(new MarkdownSpan(s, e - s, MarkdownSpanKind.Heading, Level: ln.Level));
                    spans.Add(new MarkdownSpan(s, ln.Prefix, MarkdownSpanKind.Syntax));
                    Inl(s + ln.Prefix, s + ln.ContentEnd);
                    if (s + ln.ContentEnd < e) spans.Add(new MarkdownSpan(s + ln.ContentEnd, e - s - ln.ContentEnd, MarkdownSpanKind.Syntax));
                    break;
                case LineKind.Rule:
                    spans.Add(new MarkdownSpan(s, e - s, MarkdownSpanKind.Syntax));
                    break;
                case LineKind.Bullet or LineKind.Number or LineKind.Task:
                    spans.Add(new MarkdownSpan(s + ln.Indent, ln.Prefix - ln.Indent, MarkdownSpanKind.ListMarker));
                    Inl(s + ln.Prefix, e);
                    break;
                case LineKind.Quote:
                    spans.Add(new MarkdownSpan(s, e - s, MarkdownSpanKind.Quote));
                    spans.Add(new MarkdownSpan(s + ln.Indent, ln.Prefix - ln.Indent, MarkdownSpanKind.Syntax));
                    Inl(s + ln.Prefix, e);
                    break;
                case LineKind.Text or LineKind.Table:
                    Inl(s + ln.Indent, e);
                    break;
            }
        }
        return spans;
    }

    // ---- Editing commands ----

    /// Ctrl+B / Ctrl+I / strikethrough / code: wraps the selection in <paramref name="marker"/>, or removes the marker
    /// when the selection (or the text around it) already has it. With nothing selected, inserts the pair and puts the
    /// cursor between it.
    public static MarkdownEditResult Wrap(string text, int selStart, int selLength, string marker)
    {
        int L = marker.Length;
        int loc = selStart, len = selLength;
        // Leave surrounding whitespace (a double-clicked word with its space, a line with its break) outside.
        while (len > 0 && char.IsWhiteSpace(text[loc + len - 1])) len--;
        while (len > 0 && char.IsWhiteSpace(text[loc])) { loc++; len--; }
        char c = marker[0];
        int RunLeft(int o) { int n = 0; while (o - n - 1 >= 0 && text[o - n - 1] == c) n++; return n; }
        int RunRight(int o) { int n = 0; while (o + n < text.Length && text[o + n] == c) n++; return n; }
        // A run of the marker character has the marker when it is exactly as long, or 3 long (bold italic).
        bool Has(int n) => n == L || (n == 3 && L < 3);

        if (len == 0)
        {
            int o = selStart;
            if (RunLeft(o) == L && RunRight(o) == L && o - L >= 0)
                return new(text.Remove(o - L, 2 * L), o - L, 0);
            return new(text.Insert(o, marker + marker), o + L, 0);
        }
        var inner = text.Substring(loc, len);
        int lead = 0; while (lead < inner.Length && inner[lead] == c) lead++;
        int trail = 0; while (trail < inner.Length - lead && inner[inner.Length - 1 - trail] == c) trail++;
        if (inner.Length > 2 * L && Has(lead) && Has(trail))
        {
            var core = inner.Substring(L, inner.Length - 2 * L);
            return new(text.Remove(loc, len).Insert(loc, core), loc, core.Length);
        }
        if (Has(RunLeft(loc)) && Has(RunRight(loc + len)))
            return new(text.Remove(loc + len, L).Remove(loc - L, L), loc - L, len);
        return new(text.Remove(loc, len).Insert(loc, marker + inner + marker), loc + L, len);
    }

    /// Lines touched by a selection: a selection that ends at the start of a line leaves that line out.
    static (int First, int Last) TouchedLines(Lines L, int selStart, int selLength)
    {
        int first = L.LineAt(selStart);
        int endO = selStart + selLength;
        if (selLength > 0 && L.Starts.Contains(endO)) endO--;
        return (first, Math.Max(first, L.LineAt(endO)));
    }

    /// Heading, bullet, numbered, task or quote for the selected lines. When every selected line already has the
    /// style, it is removed; otherwise it replaces the lines' current markers. Numbers count 1, 2, 3 ….
    public static MarkdownEditResult ToggleLines(string text, int selStart, int selLength, MarkdownLineStyle style, int headingLevel = 2)
    {
        var L = new Lines(text);
        var (first, last) = TouchedLines(L, selStart, selLength);
        var idxs = Enumerable.Range(first, last - first + 1).ToList();
        var lines = idxs.Select(L.Classify).ToList();
        bool skipBlank = idxs.Count > 1;
        bool Matches(Line ln) => (style, ln.Kind) switch
        {
            (MarkdownLineStyle.Heading, LineKind.Heading) => ln.Level == headingLevel,
            (MarkdownLineStyle.Bullet, LineKind.Bullet) or (MarkdownLineStyle.Numbered, LineKind.Number)
                or (MarkdownLineStyle.Task, LineKind.Task) or (MarkdownLineStyle.Quote, LineKind.Quote) => true,
            _ => false,
        };
        var considered = lines.Where(l => !(skipBlank && l.Kind == LineKind.Blank)).ToList();
        bool remove = considered.Count > 0 && considered.All(Matches);
        int n = 0;
        var newLines = new List<string>();
        (int Old, int New) cursorPrefix = (0, 0);
        int cursorLine = L.LineAt(selStart);
        for (int x = 0; x < idxs.Count; x++)
        {
            int idx = idxs[x]; var ln = lines[x];
            int s = L.Starts[idx];
            var indent = L.Str(s, s + ln.Indent);
            bool hasMarker = ln.Kind is not (LineKind.Text or LineKind.Blank or LineKind.Table or LineKind.Rule or LineKind.Fence);
            int contentStart = hasMarker ? ln.Prefix : ln.Indent;
            var content = L.Str(s + contentStart, L.Ends[idx]);
            string newPrefix;
            if (skipBlank && ln.Kind == LineKind.Blank) { newLines.Add(L.Str(idx)); continue; }
            if (remove) newPrefix = style == MarkdownLineStyle.Heading ? "" : indent;
            else
            {
                n++;
                newPrefix = style switch
                {
                    MarkdownLineStyle.Heading => new string('#', headingLevel) + " ",
                    MarkdownLineStyle.Bullet => indent + "- ",
                    MarkdownLineStyle.Numbered => indent + $"{n}. ",
                    MarkdownLineStyle.Task => indent + "- [ ] ",
                    _ => indent + "> ",
                };
            }
            if (idx == cursorLine) cursorPrefix = (contentStart, newPrefix.Length);
            newLines.Add(newPrefix + content);
        }
        int a = L.Starts[first], b = L.Ends[last];
        var joined = string.Join("\n", newLines);
        var output = text[..a] + joined + text[b..];
        if (selLength == 0)
        {
            // The cursor keeps its place in the content; inside the old marker it moves to the content start.
            int lineStart = L.Starts[cursorLine];
            int loc = lineStart + cursorPrefix.New + Math.Max(0, selStart - lineStart - cursorPrefix.Old);
            return new(output, Math.Min(loc, output.Length), 0);
        }
        return new(output, a, joined.Length);
    }

    /// Return in a list item or quote: continues it with the next marker (next number, an open task box). Return in an
    /// empty item ends the list (the marker is removed). null = an ordinary line break.
    public static MarkdownEditResult? Newline(string text, int selStart, int selLength)
    {
        if (selLength != 0) return null;
        var L = new Lines(text);
        int i = L.LineAt(selStart);
        var ln = L.Classify(i);
        int s = L.Starts[i];
        if (!(ln.IsListItem || ln.Kind == LineKind.Quote) || selStart < s + ln.Prefix) return null;
        var content = L.Str(s + ln.Prefix, L.Ends[i]).Trim();
        if (content.Length == 0)
            return new(text.Remove(s, L.Ends[i] - s), s, 0);
        var indent = L.Str(s, s + ln.Indent);
        var next = ln.Kind switch
        {
            LineKind.Number => indent + $"{ln.Number + 1}{ln.Delimiter} ",
            LineKind.Task => indent + ln.Delimiter + " [ ] ",
            LineKind.Bullet => indent + ln.Delimiter + " ",
            _ => indent + "> ",
        };
        var ins = "\n" + next;
        return new(text.Insert(selStart, ins), selStart + ins.Length, 0);
    }

    /// Tab / Shift+Tab on list items: nests the selected items one level deeper (by the width of the item above's
    /// marker) or moves them out one level. null when a selected line is not a list item.
    public static MarkdownEditResult? Indent(string text, int selStart, int selLength, bool outdent)
    {
        var L = new Lines(text);
        var (first, last) = TouchedLines(L, selStart, selLength);
        var idxs = Enumerable.Range(first, last - first + 1).ToList();
        var lines = idxs.Select(L.Classify).ToList();
        if (!lines.All(l => l.IsListItem || l.Kind == LineKind.Blank) || !lines.Any(l => l.IsListItem)) return null;
        // List items above a line (for the indent widths), nearest first.
        List<Line> Above(int idx)
        {
            var r = new List<Line>();
            for (int k = idx - 1; k >= 0; k--)
            {
                var l = L.Classify(k);
                if (l.IsListItem) r.Add(l); else if (l.Kind != LineKind.Blank) break;
            }
            return r;
        }
        var sb = new StringBuilder(text);
        int delta = 0, firstDelta = 0;
        for (int x = idxs.Count - 1; x >= 0; x--)
        {
            int idx = idxs[x]; var ln = lines[x];
            if (!ln.IsListItem) continue;
            var prev = Above(idx);
            int newIndent;
            if (outdent)
            {
                var p = prev.FirstOrDefault(l => l.Indent < ln.Indent);
                newIndent = prev.Any(l => l.Indent < ln.Indent) ? p.Indent : 0;
                if (newIndent >= ln.Indent) continue;
            }
            else
            {
                var parent = prev.Any(l => l.Indent == ln.Indent) ? prev.First(l => l.Indent == ln.Indent) : ln;
                newIndent = ln.Indent + (parent.Prefix - parent.Indent);
            }
            int s = L.Starts[idx];
            sb.Remove(s, ln.Indent).Insert(s, new string(' ', newIndent));
            delta += newIndent - ln.Indent;
            if (idx == first) firstDelta = newIndent - ln.Indent;
        }
        var output = sb.ToString();
        if (selLength == 0) return new(output, Math.Max(L.Starts[first], selStart + firstDelta), 0);
        int a = L.Starts[first], b = L.Ends[last] + delta;
        return new(output, a, b - a);
    }
}
