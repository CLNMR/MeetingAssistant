using MeetingAssistant.Core.Model;

namespace MeetingAssistant.Core;

/// The app data folder (spec 01-recording, "App data directory").
/// Windows: %LOCALAPPDATA%\MeetingAssistant\. Override with MEETINGASSISTANT_HOME (or legacy MEETINGSCRIBE_HOME).
public static class AppPaths
{
    static string? _root;

    public static string Root
    {
        get
        {
            if (_root is not null) return _root;
            var env = Environment.GetEnvironmentVariable("MEETINGASSISTANT_HOME")
                      ?? Environment.GetEnvironmentVariable("MEETINGSCRIBE_HOME");
            if (!string.IsNullOrWhiteSpace(env)) return _root = Path.GetFullPath(env);
            var baseDir = OperatingSystem.IsWindows()
                ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                : OperatingSystem.IsMacOS()
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support")
                    : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return _root = Path.Combine(baseDir, "MeetingAssistant");
        }
    }

    /// For tests: point the root somewhere else (null resets to the default lookup).
    public static void OverrideRoot(string? path) => _root = path is null ? null : Path.GetFullPath(path);

    public static string Sessions => Path.Combine(Root, "Sessions");
    public static string VoiceprintFile => Path.Combine(Root, "me-voiceprint.json");
    public static string SpeakerLibraryFile => Path.Combine(Root, "speaker-library.json");
    public static string VocabularyFile => Path.Combine(Root, "vocabulary.json");
    public static string Dictations => Path.Combine(Root, "Dictations");
    public static string Aya => Path.Combine(Root, "Aya");
    public static string ClaudeWork => Path.Combine(Root, "ClaudeWork");
    /// Windows only: downloaded ONNX models (the Mac keeps them in library caches).
    public static string Models => Path.Combine(Root, "Models");
    /// Windows only: preferences (the Mac uses user defaults). JSON blob like `preferences.v1`.
    public static string PreferencesFile => Path.Combine(Root, "preferences.json");

    public static string Session(string id) => Path.Combine(Sessions, id);
    public static string Info(string id) => Path.Combine(Session(id), "session.json");
    public static string TranscriptFile(string id) => Path.Combine(Session(id), "transcript.json");
    public static string AssistFile(string id) => Path.Combine(Session(id), "assist.json");

    /// A track's audio for reading: `<track>.caf` (Mac) wins over `<track>.wav`; when neither exists, the WAV path.
    public static string TrackFile(string id, Track t)
    {
        var caf = Path.Combine(Session(id), t.Raw() + ".caf");
        if (File.Exists(caf)) return caf;
        return NewTrack(id, t);
    }

    /// Where Windows writes a new track: always WAV (16 kHz mono 16-bit PCM).
    public static string NewTrack(string id, Track t) => Path.Combine(Session(id), t.Raw() + ".wav");

    public static void Ensure()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Sessions);
    }
}
