using MeetingAssistant.Core.Audio;
using MeetingAssistant.Core.Model;

namespace MeetingAssistant.Core.Storage;

/// <summary>
/// Decodes an audio file of any supported format to 16 kHz mono float samples. Core's default
/// (<see cref="WavCafDecoder"/>) reads WAV and CAF only; the Windows app plugs in a Media
/// Foundation decoder (mp3, m4a, …) by setting <see cref="SessionImport.Decoder"/> at start-up,
/// e.g. NAudio `MediaFoundationReader` → mono → resample to 16 kHz.
/// </summary>
public interface IAudioDecoder
{
    /// The file as 16 kHz mono samples in [-1, 1]; throws when the format is not supported.
    float[] Decode16k(string path);
}

/// WAV and CAF through <see cref="AudioFile.Read"/>.
public sealed class WavCafDecoder : IAudioDecoder
{
    public float[] Decode16k(string path) => AudioFile.Read(path);
}

/// <summary>
/// "Import Audio…" (spec 01 "Import"): one file = an in-person meeting, two files = a call (first
/// the microphone, then the call audio). Files are decoded to 16 kHz mono and written as WAV
/// tracks (no padding to equal length); id `import_&lt;yyyy-MM-dd_HH-mm-ss&gt;` (now, `_2`… on
/// collision); title = first file name without extension; status `recorded`. The caller then
/// selects and queues the session; on error it shows "Import failed: …".
/// </summary>
public static class SessionImport
{
    /// The decoder used for imports (set by the platform; default WAV/CAF only).
    public static IAudioDecoder Decoder { get; set; } = new WavCafDecoder();

    /// File dialog message.
    public const string DialogMessage = "Choose one recording (in-person meeting), or two files: first your microphone, then the call audio.";

    /// Mode for the number of chosen files: one → In person, two → Call.
    public static MeetingMode ModeFor(int fileCount) => fileCount > 1 ? MeetingMode.Call : MeetingMode.InPerson;

    /// Creates a session from audio files. Without a system file the mode is always In person.
    public static SessionInfo ImportFiles(string mic, string? system, MeetingMode mode, string? title = null,
                                          int? expectedSpeakers = null, DateTime? startedAt = null, IAudioDecoder? decoder = null)
    {
        decoder ??= Decoder;
        var now = DateTime.Now;
        var id = SessionStore.CreateSessionFolder("import_" + SessionStore.FolderName(now));
        try
        {
            double duration = 0;
            var tracks = new List<Track>();
            foreach (var (path, track) in new[] { (mic, Track.Mic), (system, Track.System) })
            {
                if (path is null) continue;
                var samples = decoder.Decode16k(path);
                AudioFile.WriteWav(AppPaths.NewTrack(id, track), samples);
                duration = Math.Max(duration, samples.Length / (double)AudioFile.SampleRate);
                tracks.Add(track);
            }
            var info = new SessionInfo
            {
                Id = id,
                Title = title ?? Path.GetFileNameWithoutExtension(mic),
                StartedAt = (startedAt ?? now).ToUniversalTime(),
                DurationSec = duration,
                Mode = system is null && mode != MeetingMode.InPerson ? MeetingMode.InPerson : mode,
                Tracks = tracks,
                ExpectedSpeakers = expectedSpeakers,
                Language = null,
                Status = SessionStatus.Recorded,
            };
            SessionStore.Save(info);
            return info;
        }
        catch
        {
            try { Directory.Delete(AppPaths.Session(id), recursive: true); } catch { }
            throw;
        }
    }
}
