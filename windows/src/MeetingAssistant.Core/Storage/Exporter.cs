using System.Globalization;
using System.Text;
using MeetingAssistant.Core.Json;
using MeetingAssistant.Core.Model;

namespace MeetingAssistant.Core.Storage;

/// Export formats (spec 01 "Exports"); the value is the file extension.
public enum ExportFormat { Markdown, Text, Srt, Json }

/// <summary>
/// Renders a transcript as Markdown, plain text, SRT subtitles or JSON (spec 01 "Exports").
/// </summary>
public static class Exporter
{
    /// File extension: md, txt, srt, json.
    public static string Extension(this ExportFormat f) => f switch
    {
        ExportFormat.Markdown => "md",
        ExportFormat.Text => "txt",
        ExportFormat.Srt => "srt",
        _ => "json",
    };

    /// Menu title: "Markdown", "Plain text", "Subtitles (SRT)", "JSON".
    public static string Title(this ExportFormat f) => f switch
    {
        ExportFormat.Markdown => "Markdown",
        ExportFormat.Text => "Plain text",
        ExportFormat.Srt => "Subtitles (SRT)",
        _ => "JSON",
    };

    /// Format from its extension (md, txt, srt, json), or null.
    public static ExportFormat? Parse(string ext) => ext.ToLowerInvariant() switch
    {
        "md" => ExportFormat.Markdown,
        "txt" => ExportFormat.Text,
        "srt" => ExportFormat.Srt,
        "json" => ExportFormat.Json,
        _ => null,
    };

    /// `mm:ss` below one hour, `h:mm:ss` from one hour on (truncated to whole seconds).
    public static string Clock(double t)
    {
        long s = (long)t;
        return s >= 3600
            ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", s / 3600, s / 60 % 60, s % 60)
            : string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}", s / 60, s % 60);
    }

    /// `HH:MM:SS,mmm` (milliseconds rounded).
    public static string SrtTime(double t)
    {
        long ms = (long)Math.Round(t * 1000, MidpointRounding.AwayFromZero);
        return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00},{3:000}",
            ms / 3_600_000, ms / 60_000 % 60, ms / 1000 % 60, ms % 1000);
    }

    /// Words per SRT cue.
    public const int WordsPerCue = 12;

    /// "full date, short time" of the Markdown header in the given culture (Mac `DateFormatter`
    /// with `.full` / `.short`: "Tuesday, 10 November 2026 at 11:21").
    public static string FullDate(DateTime date, CultureInfo culture)
    {
        var local = date.Kind == DateTimeKind.Local ? date : DateTime.SpecifyKind(date, DateTimeKind.Utc).ToLocalTime();
        var joiner = culture.TwoLetterISOLanguageName switch { "en" => " at ", "de" => " um ", _ => ", " };
        return local.ToString("D", culture) + joiner + local.ToString("t", culture);
    }

    /// The export text. `culture` only affects the date in the Markdown header (default: current).
    public static string Render(Transcript t, SessionInfo info, ExportFormat format, CultureInfo? culture = null)
    {
        switch (format)
        {
            case ExportFormat.Markdown:
            {
                var sb = new StringBuilder();
                sb.Append("# ").Append(info.Title).Append("\n\n");
                sb.Append('*').Append(FullDate(info.StartedAt, culture ?? CultureInfo.CurrentCulture)).Append(" · ")
                  .Append(Clock(info.DurationSec)).Append(" · ").Append(info.Mode.Title()).Append("*\n\n");
                var names = t.Speakers.Select(s => s.Label).ToList();
                if (names.Count > 0) sb.Append("**Participants:** ").Append(string.Join(", ", names)).Append("\n\n");
                foreach (var turn in t.Turns)
                    sb.Append("**").Append(t.Label(turn.Speaker)).Append("** [").Append(Clock(turn.Start)).Append("]: ").Append(turn.Text).Append("\n\n");
                return sb.ToString();
            }
            case ExportFormat.Text:
                return string.Join("\n", t.Turns.Select(x => $"[{Clock(x.Start)}] {t.Label(x.Speaker)}: {x.Text}")) + "\n";
            case ExportFormat.Srt:
            {
                var sb = new StringBuilder();
                int n = 1;
                foreach (var turn in t.Turns)
                {
                    for (int part = 0; part < turn.Words.Count; part += WordsPerCue)
                    {
                        var ws = turn.Words.Skip(part).Take(WordsPerCue).ToList();
                        var f = ws[0];
                        var l = ws[^1];
                        sb.Append(n).Append('\n')
                          .Append(SrtTime(f.Start)).Append(" --> ").Append(SrtTime(Math.Max(l.End, f.Start + 0.5))).Append('\n')
                          .Append(t.Label(turn.Speaker)).Append(": ").Append(string.Join(" ", ws.Select(w => w.W))).Append("\n\n");
                        n++;
                    }
                }
                return sb.ToString();
            }
            default:
                return JsonIO.Serialize(t);
        }
    }

    /// Writes an export to a file (UTF-8 without BOM).
    public static void Save(Transcript t, SessionInfo info, ExportFormat format, string path, CultureInfo? culture = null) =>
        File.WriteAllText(path, Render(t, info, format, culture), new UTF8Encoding(false));

    /// Default file name: `<title>.<ext>`.
    public static string DefaultFileName(SessionInfo info, ExportFormat format) => $"{info.Title}.{format.Extension()}";
}
