using System.Globalization;
using MeetingAssistant.Core.Audio;
using MeetingAssistant.Core.Storage;

namespace MeetingAssistant.Core.Text;

/// <summary>
/// Dictations whose text did not arrive (spec 04 §1.8 "Unfinished Dictations"). The audio is saved
/// before it is transcribed and removed once the text is delivered, so an error or a crash loses
/// nothing. A kept one stays until it is transcribed again, deleted, or for 24 hours at most.
/// Files: `&lt;AppData&gt;/Dictations/dictation-&lt;ms since 1970&gt;[-plus].caf`, 16 kHz mono Float32 CAF,
/// the same format as the Mac.
/// </summary>
public static class DictationRecovery
{
    /// How long a kept dictation stays.
    public static readonly TimeSpan KeptFor = TimeSpan.FromHours(24);

    /// One kept dictation.
    public sealed record Item(string Path, DateTime RecordedAt, double Seconds, bool Plus)
    {
        /// File name, unique per dictation.
        public string Id => System.IO.Path.GetFileName(Path);
    }

    /// `<AppData>/Dictations`.
    public static string Folder => AppPaths.Dictations;

    /// The file name for a dictation recorded at `date` (UTC or local).
    public static string FileName(DateTime date, bool plus)
    {
        long ms = (long)(date.ToUniversalTime() - DateTime.UnixEpoch).TotalMilliseconds;
        return $"dictation-{ms.ToString(CultureInfo.InvariantCulture)}{(plus ? "-plus" : "")}.caf";
    }

    /// Saves 16 kHz mono samples; returns the file path, or null when saving failed.
    public static string? Save(ReadOnlySpan<float> samples, bool plus = false, DateTime? at = null, string? dir = null)
    {
        dir ??= Folder;
        try
        {
            Directory.CreateDirectory(dir);
            if (OperatingSystem.IsWindows())
            {
                // No backup exclusion on Windows; at least keep the voice audio out of the search index.
                try { var di = new DirectoryInfo(dir); di.Attributes |= FileAttributes.NotContentIndexed; } catch { }
            }
            var path = System.IO.Path.Combine(dir, FileName(at ?? DateTime.UtcNow, plus));
            AudioContainer.WriteCafFloat32(path, samples);
            return path;
        }
        catch { return null; }
    }

    /// The samples of a kept dictation (16 kHz mono).
    public static float[] Load(Item item) => AudioFile.Read(item.Path);

    /// Deletes a kept dictation (no error when it is gone).
    public static void Delete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    /// Parses `dictation-<ms>[-plus].caf`: the date and whether it was a Scribe+ dictation.
    /// The number before the first `-` after `dictation-` is the date; older names without a
    /// suffix read as plain.
    public static (DateTime recordedAt, bool plus) ParseName(string fileName)
    {
        var stem = System.IO.Path.GetFileNameWithoutExtension(fileName).Replace("dictation-", "", StringComparison.Ordinal);
        var parts = stem.Split('-', StringSplitOptions.RemoveEmptyEntries);
        double ms = parts.Length > 0 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        return (DateTime.UnixEpoch.AddMilliseconds(ms), parts.Skip(1).Contains("plus"));
    }

    /// Kept dictations, newest first. Those older than 24 h are deleted on the way
    /// (the list is read at launch, when the menu opens, and after a failure).
    public static List<Item> Current(DateTime? now = null, string? dir = null)
    {
        dir ??= Folder;
        var n = (now ?? DateTime.UtcNow).ToUniversalTime();
        if (!Directory.Exists(dir)) return new List<Item>();
        var items = new List<Item>();
        foreach (var f in Directory.EnumerateFiles(dir, "*.caf"))
        {
            var (date, plus) = ParseName(System.IO.Path.GetFileName(f));
            if (n - date > KeptFor) { Delete(f); continue; }
            double secs;
            try { secs = AudioContainer.Probe(f).Seconds; } catch { secs = 0; }
            items.Add(new Item(f, date, secs, plus));
        }
        return items.OrderByDescending(i => i.RecordedAt).ToList();
    }

    /// Menu label `<time> · <clock(seconds)>` (clock = `mm:ss`, as the Mac's `Exporter.clock`), with
    /// the short date added when it was not recorded today (spec 04 §1.8). Local time.
    public static string Label(Item item, DateTime? now = null, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var local = item.RecordedAt.ToLocalTime();
        var today = (now ?? DateTime.Now).ToLocalTime().Date;
        var time = local.Date == today ? local.ToString("t", culture) : local.ToString("d", culture) + ", " + local.ToString("t", culture);
        return $"{time} · {Exporter.Clock(item.Seconds)}";
    }
}
