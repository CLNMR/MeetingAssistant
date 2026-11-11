using System.Globalization;
using MeetingAssistant.Core.Model;

namespace MeetingAssistant.Core.Text;

/// <summary>
/// The pure decisions and texts of push-to-talk dictation (spec 04 §1.3–1.9), shared by the
/// Windows dictation controller and msctl: tap / accidental-press filters, the clean-up every
/// dictation gets, preview and HUD text, toasts and the log line.
/// </summary>
public static class DictationText
{
    /// Presses shorter than this are taps and ignored silently (seconds).
    public const double TapSeconds = 0.3;
    /// Wait after release so the last audio buffer arrives (ms).
    public const int ReleaseDelayMs = 200;
    /// Live preview interval (s), minimum buffer (samples) and window (samples, one Parakeet window).
    public const double PreviewInterval = 0.7;
    public const int PreviewMinSamples = 8_000, PreviewWindowSamples = 14 * 16_000;
    /// Level meter: every 50 ms over the last 800 samples.
    public const int LevelIntervalMs = 50, LevelSamples = 800;
    /// After this many seconds of "Transcribing…" the model hint replaces it.
    public const double ModelHintSeconds = 5;
    /// Toast durations (s): short, long (text over 50 characters); HUD hide after a paste.
    public const double ToastSeconds = 2.5, LongToastSeconds = 4, HideAfterPasteSeconds = 0.6;

    // Exact UI texts (spec 04 §1). Windows uses Ctrl+V where the Mac says ⌘V.
    public const string Listening = "Listening…", PlusListening = "Scribe+ listening…";
    public const string Transcribing = "Transcribing…", WritingWithClaude = "Writing with Claude…";
    public const string TranscribingKept = "Transcribing the kept dictation…";
    public const string ModelHint = "Preparing the speech model… (the first dictation after an update can take a few minutes)";
    public const string AllowMicrophone = "Allow microphone access, then try again";
    public const string NoMicrophoneAccess = "No microphone access (Settings › Privacy)";
    public const string MicrophoneUnavailable = "Microphone unavailable";
    public const string NoSpeech = "No speech detected";
    public const string TranscriptionFailed = "Transcription failed";
    public const string TranscriptionFailedKept = "Transcription failed – the recording is kept in the tray menu";
    public const string CursorMoved = "Copied – the cursor moved to another field. Paste with Ctrl+V.";
    public const string CopiedNotPasted = "Copied (not pasted) – paste with Ctrl+V";
    public const string NoSpeechInRecording = "No speech in that recording";
    public const string TranscriptionFailedAgain = "Transcription failed again";

    /// A press shorter than 0.3 s is a tap: nothing happens.
    public static bool IsTap(double heldSeconds) => heldSeconds < TapSeconds;

    /// A shortcut pressed by accident: held under 3 s and no voiced run of 0.15 s.
    public static bool IsAccidental(double heldSeconds, double longestVoicedRun) =>
        heldSeconds < InventedText.AccidentalSeconds && longestVoicedRun < InventedText.MinimumVoicedSeconds;

    /// The text processing every dictation gets (spec 04 §1.4 f–g): null when nothing was said
    /// (empty or invented), else vocabulary then spoken forms.
    public static string? Clean(string raw, double longestVoicedRun, VocabularyMatcher? vocabulary)
    {
        if (raw.Length == 0 || InventedText.IsInvented(raw, longestVoicedRun)) return null;
        return SpokenForms.Apply(vocabulary is null ? raw : vocabulary.Apply(raw));
    }

    /// Preview of the last window: when the buffer was longer than the window, the first word is
    /// assumed cut and dropped, and "… " is put in front.
    public static string PreviewText(string text, bool truncated)
    {
        if (!truncated) return text;
        var space = text.IndexOf(' ');
        return space < 0 ? text : "… " + text[(space + 1)..];
    }

    /// The end of long text for the HUD: at most `max` characters, cut at a word boundary, "… " in front.
    public static string Tail(string s, int max = 200)
    {
        if (TextUtil.Length(s) <= max) return s;
        var cut = TextUtil.Suffix(s, max);
        var space = cut.IndexOf(' ');
        return "… " + (space < 0 ? cut : cut[(space + 1)..]);
    }

    /// Toast duration: 4 s for texts over 50 characters, else 2.5 s.
    public static double ToastDuration(string text) => TextUtil.Length(text) > 50 ? LongToastSeconds : ToastSeconds;

    /// HUD level: v = clamp((dB + 55) / 45, 0, 1) with dB = 20·log10(max(rms, 1e-5)); bar i (0–4) lit when i/5 &lt; v.
    public static double LevelFraction(float rms) => Math.Clamp((20 * Math.Log10(Math.Max(rms, 1e-5)) + 55) / 45, 0, 1);

    /// RMS of the last `count` samples.
    public static float Rms(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0) return 0;
        double sum = 0;
        foreach (var x in samples) sum += x * x;
        return (float)Math.Sqrt(sum / samples.Length);
    }

    /// Step timings of one dictation (spec 04 §1.9), for the log and the test hook.
    public sealed class Steps
    {
        public string Mode { get; set; } = "plain";
        public double Audio { get; set; }
        public double Asr { get; set; }
        public double Cleanup { get; set; }
        public double Field { get; set; }
        public double Claude { get; set; }
        public double Total { get; set; }
        public string Outcome { get; set; } = "";
        public string Raw { get; set; } = "";
        public string Text { get; set; } = "";

        /// `dictation <mode>: audio X.X s, asr X.XX, cleanup X.XX, field X.XX, claude X.XX, total X.XX – <outcome>`.
        public string LogLine() => string.Format(CultureInfo.InvariantCulture,
            "dictation {0}: audio {1:F1} s, asr {2:F2}, cleanup {3:F2}, field {4:F2}, claude {5:F2}, total {6:F2} – {7}",
            Mode, Audio, Asr, Cleanup, Field, Claude, Total, Outcome);
    }
}
