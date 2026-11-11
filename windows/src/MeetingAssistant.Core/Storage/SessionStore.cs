using System.Globalization;
using MeetingAssistant.Core.Json;
using MeetingAssistant.Core.Model;

namespace MeetingAssistant.Core.Storage;

/// <summary>
/// Sessions on disk (spec 01 "Session storage"): `Sessions/&lt;id&gt;/session.json`, `transcript.json`,
/// track files. Listing, loading, saving, unique folder ids, crash recovery, delete and rename.
/// </summary>
public static class SessionStore
{
    /// Every readable `Sessions/*/session.json`, newest `startedAt` first. Unreadable folders are ignored.
    public static List<SessionInfo> List()
    {
        if (!Directory.Exists(AppPaths.Sessions)) return new List<SessionInfo>();
        var list = new List<SessionInfo>();
        foreach (var dir in Directory.EnumerateDirectories(AppPaths.Sessions))
        {
            var file = Path.Combine(dir, "session.json");
            if (!File.Exists(file)) continue;
            try { list.Add(JsonIO.Read<SessionInfo>(file)); } catch { }
        }
        return list.OrderByDescending(s => s.StartedAt).ToList();
    }

    /// The session's `session.json` (throws when missing or unreadable).
    public static SessionInfo Load(string id) => JsonIO.Read<SessionInfo>(AppPaths.Info(id));

    /// The session, or null when missing or unreadable.
    public static SessionInfo? TryLoad(string id)
    {
        try { return File.Exists(AppPaths.Info(id)) ? Load(id) : null; } catch { return null; }
    }

    /// Writes `session.json` atomically.
    public static void Save(SessionInfo info) => JsonIO.Write(info, AppPaths.Info(info.Id));

    /// The transcript, or null when there is none (or it cannot be read).
    public static Transcript? LoadTranscript(string id)
    {
        try { return File.Exists(AppPaths.TranscriptFile(id)) ? JsonIO.Read<Transcript>(AppPaths.TranscriptFile(id)) : null; }
        catch { return null; }
    }

    /// Writes `transcript.json` atomically.
    public static void SaveTranscript(Transcript t, string id) => JsonIO.Write(t, AppPaths.TranscriptFile(id));

    /// Whether the session has an `assist.json` (an Assist recording).
    public static bool HasAssistRecord(string id) => File.Exists(AppPaths.AssistFile(id));

    /// Deletes the whole session folder.
    public static void Delete(string id)
    {
        var dir = AppPaths.Session(id);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    /// Sets the title (detail header) and saves.
    public static SessionInfo Rename(string id, string title)
    {
        var s = Load(id);
        s.Title = title;
        Save(s);
        return s;
    }

    /// Sets the status on disk (no-op when the session cannot be read). Returns the updated info.
    public static SessionInfo? SetStatus(string id, SessionStatus status)
    {
        var s = TryLoad(id);
        if (s is null) return null;
        s.Status = status;
        Save(s);
        return s;
    }

    /// `yyyy-MM-dd_HH-mm-ss` of `date` in local time: the base of a session folder name.
    public static string FolderName(DateTime date)
    {
        var local = date.Kind == DateTimeKind.Utc ? date.ToLocalTime() : date;
        return local.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
    }

    /// Creates a new, empty session folder named `baseName`, or `baseName_2`, `_3`… when taken,
    /// and returns the id. Creating the folder is the reservation.
    public static string CreateSessionFolder(string baseName)
    {
        AppPaths.Ensure();
        var id = baseName;
        int n = 1;
        while (true)
        {
            var dir = AppPaths.Session(id);
            if (!Directory.Exists(dir) && !File.Exists(dir))
            {
                // Directory.CreateDirectory also succeeds on an existing folder, so create a private
                // folder and rename it into place: the rename fails when another process (app or
                // msctl) took the name in the meantime.
                var tmp = Path.Combine(AppPaths.Sessions, ".new-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tmp);
                try
                {
                    Directory.Move(tmp, dir);
                    return id;
                }
                catch (IOException) when (Directory.Exists(dir))
                {
                    Directory.Delete(tmp);
                }
            }
            n++;
            id = $"{baseName}_{n}";
        }
    }

    /// Length in seconds of the longest track on disk (0 when there is none). A track with an
    /// unreadable header is estimated from its size (16 kHz 16-bit mono after a 4096-byte header).
    public static double AudioDuration(string id, IEnumerable<Track> tracks)
    {
        double longest = 0;
        foreach (var t in tracks)
        {
            var path = AppPaths.TrackFile(id, t);
            if (!File.Exists(path)) continue;
            longest = Math.Max(longest, AudioContainer.DurationOrEstimate(path));
        }
        return longest;
    }

    /// A session still `recording` when the app quit or crashed becomes `recorded` and, when its
    /// length was never saved, gets it from the audio files (spec 01 "Crash recovery at launch").
    public static SessionInfo RecoverInterrupted(SessionInfo info)
    {
        var s = info.Clone();
        if (s.Status == SessionStatus.Recording) s.Status = SessionStatus.Recorded;
        if (s.DurationSec <= 0) s.DurationSec = AudioDuration(s.Id, s.Tracks);
        return s;
    }
}
