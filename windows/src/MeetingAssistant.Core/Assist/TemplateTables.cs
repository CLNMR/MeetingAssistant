using System.Text;
using System.Text.Json.Serialization;

namespace MeetingAssistant.Core.Assist;

/// <summary>
/// A Markdown table in a meeting template that is filled in during the meeting (spec 02 §6.2). Columns
/// that the template already fills name the rows; the empty columns are answered in the meeting. Rows
/// are T1.1, T1.2 … so Claude and the app can address cells.
/// </summary>
public sealed class TemplateTable
{
    /// "T1"
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    /// Heading the table sits under.
    [JsonPropertyName("section")] public string Section { get; set; } = "";
    /// Text right above the table (the question it asks).
    [JsonPropertyName("prompt")] public string Prompt { get; set; } = "";
    [JsonPropertyName("columns")] public List<string> Columns { get; set; } = new();
    /// Columns that are empty in the template.
    [JsonPropertyName("answerColumns")] public List<int> AnswerColumns { get; set; } = new();
    [JsonPropertyName("rows")] public List<Row> Rows { get; set; } = new();

    /// <summary>One row: cells, evidence per answer column, user-set columns, open name cells.</summary>
    public sealed class Row
    {
        /// "T1.3"
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("cells")] public List<string> Cells { get; set; } = new();
        /// Per answer column (by name): a quote or reason for the value.
        [JsonPropertyName("evidence")] public Dictionary<string, string> Evidence { get; set; } = new();
        /// Answer columns last set by the user (Claude does not overwrite them). Insertion-ordered set.
        [JsonPropertyName("userSet")] public HashSet<string> UserSet { get; set; } = new();
        /// Name columns that are open in the template ("Eigener Vorschlag:"), with their template text.
        [JsonPropertyName("openKeys")] public Dictionary<int, string> OpenKeys { get; set; } = new();

        public Row() { }

        /// A template row; every cell ending in ":" becomes an open key.
        public Row(string id, IEnumerable<string> cells)
        {
            Id = id;
            Cells = cells.ToList();
            for (int i = 0; i < Cells.Count; i++) if (IsOpenKey(Cells[i])) OpenKeys[i] = AssistText.TrimWs(Cells[i]);
        }
    }

    public TemplateTable() { }
    public TemplateTable(string id, string section, string prompt, IEnumerable<string> columns, IEnumerable<int> answerColumns, IEnumerable<Row> rows)
    {
        Id = id; Section = section; Prompt = prompt; Columns = columns.ToList(); AnswerColumns = answerColumns.ToList(); Rows = rows.ToList();
    }

    /// The columns that are not answer columns.
    [JsonIgnore] public List<int> KeyColumns => Enumerable.Range(0, Columns.Count).Where(i => !AnswerColumns.Contains(i)).ToList();

    string Cell(Row r, int i) => i < r.Cells.Count ? r.Cells[i] : "";

    /// "CRM &amp; Vertrieb · CRM": the non-empty key cells.
    public string Label(Row row) => string.Join(" · ", KeyColumns.Select(i => Cell(row, i)).Where(c => c.Length > 0));

    /// Answered: an answer cell has a value, or an open row got its proposal (spec 02 §6.3).
    public bool IsFilled(Row row) =>
        AnswerColumns.Any(i => AssistText.TrimWs(Cell(row, i)).Length > 0)
        || row.OpenKeys.Any(kv => AssistText.TrimWs(Cell(row, kv.Key)) != kv.Value);

    [JsonIgnore] public int FilledCount => Rows.Count(IsFilled);

    /// A row whose name ends in ":" ("Eigener Vorschlag:") takes its name from the meeting.
    public static bool IsOpenKey(string s) => AssistText.TrimWs(s).EndsWith(':');

    /// Exact case-insensitive match first, else the first column where one name is a prefix of the other.
    public int? ColumnNamed(string name)
    {
        var n = AssistText.TrimWs(name).ToLowerInvariant();
        var i = Columns.FindIndex(c => c.ToLowerInvariant() == n);
        if (i >= 0) return i;
        i = Columns.FindIndex(c => c.ToLowerInvariant().StartsWith(n, StringComparison.Ordinal) || n.StartsWith(c.ToLowerInvariant(), StringComparison.Ordinal));
        return i >= 0 ? i : null;
    }

    /// <summary>
    /// Sets one cell (spec 02 §6.2). Answer columns take the value; a key column only when it is an open
    /// key (the value is appended to its template text). Claude's values never replace the user's.
    /// Returns whether something changed.
    /// </summary>
    public bool Set(string rowId, string column, string value, string? evidence = null, bool byUser = false)
    {
        var row = Rows.FirstOrDefault(r => r.Id == rowId);
        if (row is null || ColumnNamed(column) is not { } c) return false;
        while (row.Cells.Count < Columns.Count) row.Cells.Add("");
        var v = AssistText.TrimAll(value);
        var col = Columns[c];
        if (AnswerColumns.Contains(c))
        {
            if (!byUser && row.UserSet.Contains(col)) return false;
            if (row.Cells[c] == v) return false;
            row.Cells[c] = v;
            if (byUser) row.UserSet.Add(col);
            else if (!string.IsNullOrEmpty(evidence)) row.Evidence[col] = evidence;
            return true;
        }
        // Only open name cells take a value; Claude fills them once, the user any time.
        if (!row.OpenKeys.TryGetValue(c, out var @base)) return false;
        var current = AssistText.TrimWs(row.Cells[c]);
        if (!byUser && current != @base) return false;
        var nv = v.Length == 0 ? @base : @base + " " + v;
        if (nv == current) return false;
        row.Cells[c] = nv;
        return true;
    }

    /// Tables up to this many rows are written whole; longer ones only with the rows that were answered.
    public const int WriteAllRowsUpTo = 30;

    /// <summary>The filled table as Markdown (spec 02 §6.3).</summary>
    public string Markdown()
    {
        var shown = Rows.Count <= WriteAllRowsUpTo ? Rows : Rows.Where(IsFilled).ToList();
        static string Esc(string s) => s.Replace("|", "\\|").Replace("\n", " ");
        var sb = new StringBuilder();
        if (Prompt.Length > 0) sb.Append(Prompt).Append("\n\n");
        sb.Append("| ").Append(string.Join(" | ", Columns.Select(Esc))).Append(" |\n");
        sb.Append('|').Append(string.Join("|", Columns.Select(_ => "---"))).Append("|\n");
        foreach (var r in shown) sb.Append("| ").Append(string.Join(" | ", r.Cells.Select(Esc))).Append(" |\n");
        if (shown.Count < Rows.Count) sb.Append($"\n_{Rows.Count - shown.Count} rows not discussed._\n");
        return sb.ToString();
    }
}

/// <summary>A meeting template's agenda, split into checklist items, fill-in tables and free-text notes.</summary>
public sealed class ParsedTemplate
{
    public List<PlaybookItem> Items { get; set; } = new();
    public List<TemplateTable> Tables { get; set; } = new();
    /// Paragraphs that are neither items nor table questions, "Section: text".
    public List<string> Notes { get; set; } = new();
}

/// <summary>Parses template agendas and writes filled tables back (spec 02 §6.1, §6.3).</summary>
public static class TemplateParser
{
    public static ParsedTemplate Parse(string markdown)
    {
        var output = new ParsedTemplate();
        string section = "";
        int sectionNo = 0, itemNo = 0;
        bool flat = true;
        var paragraph = new List<string>();     // plain text lines since the last item/table/heading
        var tableLines = new List<string>();

        void FlushParagraph()
        {
            var text = AssistText.TrimWs(string.Join(" ", paragraph));
            if (text.Length > 0) output.Notes.Add(section.Length == 0 ? text : $"{section}: {text}");
            paragraph.Clear();
        }

        static List<string> Cells(string line)
        {
            var s = AssistText.TrimWs(line);
            if (s.StartsWith('|')) s = s[1..];
            if (s.EndsWith('|')) s = s[..^1];
            // Split on unescaped pipes.
            var parts = new List<string>();
            var cur = new StringBuilder();
            char prev = ' ';
            foreach (var ch in s)
            {
                if (ch == '|' && prev != '\\') { parts.Add(cur.ToString()); cur.Clear(); }
                else cur.Append(ch);
                prev = ch;
            }
            parts.Add(cur.ToString());
            return parts.Select(p => AssistText.TrimWs(p.Replace("\\|", "|"))).ToList();
        }

        void FlushTable()
        {
            try
            {
                if (tableLines.Count < 2) return;
                var header = Cells(tableLines[0]);
                bool IsSeparator(string l) => Cells(l).All(c => c.Length > 0 && c.All(ch => ch is '-' or ':' or ' '));
                var body = tableLines.Skip(1).Where(l => !IsSeparator(l)).Select(Cells).ToList();
                if (header.Count == 0 || body.Count == 0) return;
                var width = header.Count;
                var tid = $"T{output.Tables.Count + 1}";
                var rows = body.Select((c, i) => new TemplateTable.Row($"{tid}.{i + 1}",
                    c.Concat(Enumerable.Repeat("", Math.Max(0, width - c.Count))).Take(width))).ToList();
                var answers = Enumerable.Range(0, width).Where(col => rows.All(r => r.Cells[col].Length == 0)).ToList();
                if (answers.Count == 0)
                {
                    // A filled reference table, nothing to answer: keep it as a note.
                    output.Notes.Add((section.Length == 0 ? "" : $"{section}: ") + string.Join("\n", tableLines));
                    return;
                }
                var prompt = AssistText.TrimWs(string.Join(" ", paragraph));
                paragraph.Clear();
                output.Tables.Add(new TemplateTable(tid, section, prompt, header, answers, rows));
            }
            finally { tableLines.Clear(); }
        }

        string? lead = null;                    // "Pro genutztem Modul:" prefixes the items below it
        foreach (var raw in AssistText.Lines(markdown))
        {
            var line = AssistText.TrimWs(raw);
            if (line.StartsWith('|')) { tableLines.Add(line); lead = null; continue; }
            if (tableLines.Count > 0) FlushTable();
            if (line.Length == 0) continue;
            if (line.StartsWith('#'))
            {
                FlushParagraph(); lead = null;
                flat = false;
                section = PlaybookParser.StripNumber(AssistText.TrimWs(line.TrimStart('#')));
                sectionNo += 1; itemNo = 0;
                continue;
            }
            if (PlaybookParser.BulletText(line) is { } text)
            {
                if (paragraph.Count > 0)
                {
                    var p = AssistText.TrimWs(string.Join(" ", paragraph));
                    if (p.EndsWith(':')) { lead = p; paragraph.Clear(); } else { FlushParagraph(); lead = null; }
                }
                itemNo += 1;
                var id = flat ? $"P{output.Items.Count + 1}" : $"P{Math.Max(sectionNo, 1)}.{itemNo}";
                output.Items.Add(new PlaybookItem(id, section, lead is null ? text : $"{lead} {text}"));
                continue;
            }
            lead = null;
            paragraph.Add(line);
        }
        FlushTable();
        FlushParagraph();
        output.Items = DropOverview(output.Items);
        return output;
    }

    /// <summary>An "Agenda" list that only repeats the section headings below it is not a checklist of its own.</summary>
    public static List<PlaybookItem> DropOverview(List<PlaybookItem> items)
    {
        var sections = items.Select(i => i.Section).Distinct().ToList();
        var output = items.ToList();
        foreach (var s in sections)
        {
            var mine = items.Where(i => i.Section == s).ToList();
            var others = items.Where(i => i.Section != s).Select(i => i.Section.ToLowerInvariant()).ToHashSet();
            // "Abschluss" repeats "Abschluss (in den letzten Minuten)": a prefix match is enough.
            bool RepeatsHeading(string text)
            {
                var t = text.ToLowerInvariant();
                return others.Any(o => o.StartsWith(t, StringComparison.Ordinal) || t.StartsWith(o, StringComparison.Ordinal));
            }
            if (mine.Count >= 2 && mine.All(i => RepeatsHeading(i.Text))) output.RemoveAll(i => i.Section == s);
        }
        return output;
    }

    /// <summary>
    /// Replaces the block that starts with the block's first line (up to the next level-2 heading) in
    /// <paramref name="existing"/>, or appends it. Saving twice therefore does not duplicate the tables.
    /// </summary>
    public static string Merge(string existing, string block)
    {
        var heading = AssistText.Lines(block).FirstOrDefault() ?? "";
        var lines = AssistText.Lines(existing).ToList();
        var start = heading.Length == 0 ? -1 : lines.FindIndex(l => AssistText.TrimWs(l) == heading);
        if (start < 0)
        {
            var b = AssistText.TrimAll(existing);
            return b.Length == 0 ? block : b + "\n\n" + block;
        }
        var end = lines.FindIndex(start + 1, l => l.StartsWith("## ", StringComparison.Ordinal));
        if (end < 0) end = lines.Count;
        var replacement = AssistText.Lines(block).ToList();
        if (end < lines.Count) replacement.Add("");
        lines.RemoveRange(start, end - start);
        lines.InsertRange(start, replacement);
        return AssistText.TrimAll(string.Join("\n", lines));
    }

    /// <summary>All filled tables of a meeting as one Markdown block under a heading naming the template; null when none is filled.</summary>
    public static string? Block(string templateTitle, IEnumerable<TemplateTable> tables)
    {
        var used = tables.Where(t => t.FilledCount > 0).ToList();
        if (used.Count == 0) return null;
        var sb = new StringBuilder($"## {templateTitle}\n");
        var section = "";
        foreach (var t in used)
        {
            if (t.Section != section && t.Section.Length > 0) { section = t.Section; sb.Append($"\n### {section}\n"); }
            sb.Append('\n').Append(t.Markdown());
        }
        return AssistText.TrimAll(sb.ToString());
    }
}
