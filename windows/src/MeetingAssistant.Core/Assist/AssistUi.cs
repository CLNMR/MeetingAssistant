using System.Globalization;
using MeetingAssistant.Core.Aya;
using MeetingAssistant.Core.Model;

namespace MeetingAssistant.Core.Assist;

/// <summary>
/// Widths and heights of the live screen (spec 03 §6.4, §6.6): the width shares of Summary / Open points /
/// Checklist with their 15 % clamps, and the transcript height. Pure functions so the WPF view only applies them.
/// </summary>
public static class LiveLayout
{
    /// Every panel keeps at least this share of the row.
    public const double MinShare = 0.15;
    /// Default stored shares (<c>assistLive.summaryShare</c>, <c>assistLive.openShare</c>).
    public const double DefaultSummaryShare = 0.40, DefaultOpenShare = 0.30;
    /// With the checklist, Summary + Open points are scaled down above this sum.
    public const double MaxPairWithChecklist = 0.85;
    /// Resize handle hit area (px).
    public const double HandleThickness = 8;
    /// Minimum width the shares are applied to (row width minus handles).
    public const double MinAvailableWidth = 300;
    /// Minimum height of the panels row; below it the upper area scrolls.
    public const double MinPanelHeight = 160;
    /// Transcript height default and minimum (<c>assistLive.transcriptHeight</c>).
    public const double DefaultTranscriptHeight = 240, MinTranscriptHeight = 90;
    /// The transcript may grow up to the window height minus this.
    public const double TranscriptReserve = 360;

    /// <summary>
    /// The shares in use. Without the checklist Summary and Open points are normalised to fill the row; with it
    /// they are scaled down when their sum exceeds 0.85 so the checklist keeps ≥ 15 %. Each stored share counts as at least 0.05.
    /// </summary>
    public static (double Summary, double Open) Shares(double summary, double open, bool hasChecklist)
    {
        double s = Math.Max(0.05, summary), o = Math.Max(0.05, open);
        if (!hasChecklist) return (s / (s + o), o / (s + o));
        var scale = s + o > MaxPairWithChecklist ? MaxPairWithChecklist / (s + o) : 1;
        return (s * scale, o * scale);
    }

    /// <summary>The width the shares apply to: row width minus the handles (1 or 2), at least 300.</summary>
    public static double Available(double rowWidth, bool hasChecklist) =>
        Math.Max(MinAvailableWidth, rowWidth - (hasChecklist ? 2 : 1) * HandleThickness);

    /// <summary>
    /// Dragging the handle after Summary: Summary and Open points trade width (each ≥ 15 %), the checklist keeps
    /// its share. <paramref name="startSummary"/> is the Summary share when the drag began, <paramref name="delta"/>
    /// the drag distance since then as a share of the available width. Returns the new stored shares.
    /// </summary>
    public static (double Summary, double Open) DragAfterSummary((double Summary, double Open) current, double startSummary, double delta)
    {
        var pair = current.Summary + current.Open;
        var s = Math.Min(Math.Max(startSummary + delta, MinShare), pair - MinShare);
        return (s, pair - s);
    }

    /// <summary>Dragging the handle after Open points: only Open points changes, so that it and the checklist keep ≥ 15 %.</summary>
    public static (double Summary, double Open) DragAfterOpen((double Summary, double Open) current, double startOpen, double delta) =>
        (current.Summary, Math.Min(Math.Max(startOpen + delta, MinShare), 1 - current.Summary - MinShare));

    /// <summary>The largest transcript height for a window height: max(90, height − 360).</summary>
    public static double MaxTranscriptHeight(double windowHeight) => Math.Max(MinTranscriptHeight, windowHeight - TranscriptReserve);

    /// <summary>A transcript height clamped between 90 and <see cref="MaxTranscriptHeight"/>.</summary>
    public static double TranscriptHeight(double requested, double windowHeight) =>
        Math.Min(Math.Max(MinTranscriptHeight, requested), MaxTranscriptHeight(windowHeight));

    /// <summary>Panels row height: the upper area minus the question card and the tables card (with 8 px gaps), at least 160.</summary>
    public static double PanelHeight(double upperHeight, double questionHeight, double tablesHeight, bool hasTables) =>
        Math.Max(MinPanelHeight, upperHeight - questionHeight - 8 - (hasTables ? tablesHeight + 8 : 0));

    /// <summary>Relative bar heights of the status-bar waveform (14 bars).</summary>
    public static readonly IReadOnlyList<double> WaveShape = new double[] { 35, 65, 85, 50, 75, 40, 90, 100, 70, 45, 80, 55, 70, 40 };

    /// <summary>Height of one waveform bar (max 22 px, at least 3 px): 22 × max(0.12, shape/100 × level).</summary>
    public static double WaveBarHeight(double shape, double level) => Math.Max(3, 22 * Math.Max(0.12, shape / 100 * level));
}

/// <summary>A row of the review's Speakers section (spec 03 §8.1).</summary>
public sealed record ReviewSpeakerRow(string Id, string Label, bool IsMe, bool IsGeneric, string Quote, double TalkSeconds, int ColorIndex)
{
    /// Talk time as the app's clock text.
    public string TalkTime => Storage.Exporter.Clock(TalkSeconds);
}

/// <summary>Small display rules of the Assist screens that the views only apply (spec 03).</summary>
public static class AssistUi
{
    // MARK: Text

    /// <summary>A summary paragraph split into a bold topic and the rest when it has ": " within its first 40 characters (spec 03 §6.4).</summary>
    public static (string? Topic, string Body) SummaryParagraph(string p)
    {
        var i = p.IndexOf(": ", StringComparison.Ordinal);
        if (i < 0 || AssistText.Count(p[..i]) > 40) return (null, p);
        var topic = p[..i].Trim('*', '#', ' ');
        return (topic, p[(i + 2)..]);
    }

    /// <summary>The first letter or digit of a name, uppercased, or "•" (spec 03 §3 "Initial avatar").</summary>
    public static string Initial(string? name)
    {
        foreach (var c in name ?? "")
            if (char.IsLetterOrDigit(c)) return char.ToUpper(c, CultureInfo.CurrentCulture).ToString();
        return "•";
    }

    /// <summary>Index 0…9 into the speaker palette (orange, purple, green, pink, teal, brown, indigo, mint, red, cyan): FNV-1a 64 of the id modulo 10 (spec 03 §3). The start value is the Mac's 1469598103934665603
    /// (one digit shorter than the standard offset basis), so speakers get the same colours as on the Mac.</summary>
    public static int SpeakerColorIndex(string id)
    {
        ulong h = 1469598103934665603;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(id)) h = unchecked((h ^ b) * 1099511628211);
        return (int)(h % 10);
    }

    /// <summary>A quote truncated at <paramref name="max"/> characters with "…".</summary>
    public static string Truncate(string s, int max = 110) =>
        AssistText.Count(s) > max ? AssistText.Prefix(s, max) + "…" : s;

    /// <summary>"Aya lists updated …" relative time: "just now", "N min ago", "N hr ago", "yesterday", "N days ago".</summary>
    public static string RelativeTime(DateTime then, DateTime now)
    {
        var d = now.ToUniversalTime() - then.ToUniversalTime();
        if (d.TotalSeconds < 60) return "just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours} hr ago";
        var days = (int)d.TotalDays;
        return days == 1 ? "yesterday" : $"{days} days ago";
    }

    /// <summary>The session row's date: "12 Nov, 14:05" (local time).</summary>
    public static string SessionDate(DateTime utc, CultureInfo? culture = null)
    {
        var c = culture ?? CultureInfo.CurrentCulture;
        var local = utc.Kind == DateTimeKind.Local ? utc : utc.ToLocalTime();
        return local.ToString("d MMM", c) + ", " + local.ToString("HH:mm", c);
    }

    /// <summary>"<date> · <duration>" under a meeting's title in the Meetings panel.</summary>
    public static string SessionLine(SessionInfo s) => SessionDate(s.StartedAt) + " · " + Storage.Exporter.Clock(s.DurationSec);

    // MARK: Toggles

    /// <summary>Open-point icon click: answered → open, anything else (open, parked) → answered (spec 03 §6.4).</summary>
    public static OpenPointStatus Toggle(OpenPointStatus s) => s == OpenPointStatus.Answered ? OpenPointStatus.Open : OpenPointStatus.Answered;

    /// <summary>Checklist icon click: covered → missing, anything else → covered.</summary>
    public static PlaybookStatus Toggle(PlaybookStatus s) => s == PlaybookStatus.Covered ? PlaybookStatus.Missing : PlaybookStatus.Covered;

    /// <summary>Boolean recap values: "true", "yes", "ja" or "1" (any case) become "true", anything else "false".</summary>
    public static string NormalizeBool(string v) =>
        new[] { "true", "yes", "ja", "1" }.Contains(v.Trim().ToLowerInvariant()) ? "true" : "false";

    // MARK: People

    /// <summary>Attendees for the chip row: present ones (present ≠ false) first, then the ones only mentioned.</summary>
    public static (List<Attendee> Present, List<Attendee> Mentioned) SplitAttendees(IEnumerable<Attendee> all)
    {
        var list = all.ToList();
        return (list.Where(a => a.Present != false).ToList(), list.Where(a => a.Present == false).ToList());
    }

    /// <summary>Rename suggestions shown in the popover: without the current name, filtered by the typed text
    /// (contains, case- and accent-insensitive), at most 8 (spec 03 §6.7).</summary>
    public static List<string> FilterSuggestions(IEnumerable<string> suggestions, string current, string typed, int limit = 8)
    {
        var q = typed.Trim();
        var all = suggestions.Where(n => n != current);
        if (q.Length > 0)
            all = all.Where(n => CultureInfo.InvariantCulture.CompareInfo.IndexOf(n, q, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0);
        return all.Take(limit).ToList();
    }

    /// <summary>The speaker-button tooltip in the live transcript.</summary>
    public static string SpeakerTooltip(string name) => SpeakerNames.IsGeneric(name)
        ? "Who is this? Click to name the speaker."
        : "Click for details from Aya, or to rename";

    /// <summary>Speakers of the final transcript in the order they first speak, with their longest utterance and talk time (spec 03 §8.1).</summary>
    public static List<ReviewSpeakerRow> ReviewSpeakers(Transcript t)
    {
        var first = new Dictionary<string, double>();
        foreach (var turn in t.Turns)
            if (!first.TryGetValue(turn.Speaker, out var s) || turn.Start < s) first[turn.Speaker] = turn.Start;
        return t.Speakers
            .Select((sp, i) => (sp, i))
            .OrderBy(x => first.TryGetValue(x.sp.Id, out var s) ? s : double.PositiveInfinity).ThenBy(x => x.i)
            .Select(x =>
            {
                var turns = t.Turns.Where(u => u.Speaker == x.sp.Id).ToList();
                var talk = turns.Sum(u => u.End - u.Start);
                var quote = "";
                foreach (var u in turns) if (AssistText.Count(u.Text) > AssistText.Count(quote)) quote = u.Text;
                return new ReviewSpeakerRow(x.sp.Id, x.sp.Label, x.sp.IsMe, SpeakerNames.IsGeneric(x.sp.Label),
                    $"“{Truncate(quote)}”", talk, x.sp.IsMe ? -1 : SpeakerColorIndex(x.sp.Id));
            })
            .ToList();
    }

    /// <summary>The Speakers section text while no transcript is available (spec 03 §8.1).</summary>
    public static string SpeakersWaiting(SessionStatus? status, double? progress) => status switch
    {
        SessionStatus.Transcribing => "The transcript is being made" + (progress is { } p ? $" ({(int)(p * 100)} %)" : "") +
                                      ". Speakers can be named when it is ready.",
        SessionStatus.Queued => "Waiting to be transcribed. Speakers can be named when the transcript is ready.",
        SessionStatus.Failed => "Transcription failed. Try “Transcribe Again” in Scribe.",
        _ => "No transcript yet.",
    };

    // MARK: Tables

    /// <summary>Long tables (more than 30 rows) show only answered rows by default (spec 03 §8.3).</summary>
    public static bool OnlyAnsweredByDefault(TemplateTable t) => t.Rows.Count > TemplateTable.WriteAllRowsUpTo;

    /// <summary>The name of a table: its section, else its id.</summary>
    public static string TableName(TemplateTable t) => t.Section.Length == 0 ? t.Id : t.Section;

    /// <summary>The last <paramref name="count"/> filled rows as "&lt;row label&gt; → &lt;answers joined by ' · '&gt;" (live tables card).</summary>
    public static List<(string RowId, string Text)> LatestAnswers(TemplateTable t, int count = 3) =>
        t.Rows.Where(t.IsFilled).TakeLast(count)
            .Select(r => (r.Id, t.Label(r) + " → " + string.Join(" · ", t.AnswerColumns.Select(i => i < r.Cells.Count ? r.Cells[i] : "").Where(c => c.Length > 0))))
            .ToList();

    // MARK: Settings (spec 03 §11)

    /// <summary>The list choices of a slot: every non-system record type as "&lt;label&gt; · &lt;workspace label&gt;", sorted by label.</summary>
    public static List<(AyaRecordSpec Spec, string Title)> ListChoices(IEnumerable<AyaRecordSpec> specs, IEnumerable<AyaWorkspace> workspaces)
    {
        var ws = workspaces.ToList();
        return specs.Where(s => s.IsSystem != true)
            .OrderBy(s => s.Label, StringComparer.CurrentCultureIgnoreCase)
            .Select(s => (s, s.Label + " · " + (ws.FirstOrDefault(w => w.Id == s.WorkspaceId)?.Label ?? "")))
            .ToList();
    }

    /// <summary>Value stored for "Don't save tables".</summary>
    public const string NoTablesField = "-";

    /// <summary>
    /// Choices of "Template tables → field": "Automatic (&lt;resolved&gt;)" (value null), the meeting list's writable
    /// Markdown fields, and "Don't save tables" (value "-").
    /// </summary>
    public static List<(string? Value, string Title)> TablesFieldChoices(AyaListSchema? schema, AyaConfig config)
    {
        var auto = config with { MeetingTablesField = null };
        var resolved = schema is null ? null : auto.TablesField(schema) is { } k ? schema.Field(k)?.Label ?? k : null;
        var list = new List<(string?, string)> { (null, resolved is null ? "Automatic" : $"Automatic ({resolved})") };
        if (schema is not null)
            list.AddRange(schema.WritableContent.Where(f => f.Kind == AyaFieldKind.Markdown).Select(f => ((string?)f.Key, f.Label)));
        list.Add((NoTablesField, "Don't save tables"));
        return list;
    }

    // MARK: Import (spec 03 §9.2)

    /// <summary>At most this many files are kept from the file dialog (mic, then call audio).</summary>
    public const int MaxImportFiles = 2;

    /// <summary>"HH:MM · Title" for an Aya meeting in the import and target pickers (time omitted when the meeting has no date).</summary>
    public static string MeetingChoice(AyaRecord m, string dateField)
    {
        var time = m.Date(dateField) is { } d ? d.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) : "";
        return string.Join(" · ", new[] { time, m.Title }.Where(x => x.Length > 0));
    }
}
