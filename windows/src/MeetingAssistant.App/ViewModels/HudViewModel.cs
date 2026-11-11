using CommunityToolkit.Mvvm.ComponentModel;

namespace MeetingAssistant.App.ViewModels;

/// Phases of the dictation HUD (spec 04 §1.7).
public enum HudPhase { Hidden, Listening, Transcribing, Message }

/// State of the dictation HUD overlay (spec 04 §1.7). The dictation controller (next phase) sets it.
public sealed partial class HudViewModel : ObservableObject
{
    /// Toast durations: 2.5 s, or 4 s when the text is longer than 50 characters.
    public static TimeSpan ToastDuration(string text) => TimeSpan.FromSeconds(text.Length > 50 ? 4 : 2.5);
    /// The HUD hides 0.6 s after a successful paste without a note.
    public static readonly TimeSpan HideAfterPaste = TimeSpan.FromSeconds(0.6);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsListening), nameof(IsTranscribing), nameof(IsMessage), nameof(ShowPreview), nameof(PreviewIsSecondary))]
    HudPhase _phase;

    /// Scribe+ (purple sparkles, "Scribe+ listening…") instead of word for word (red mic, "Listening…").
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ListeningText))]
    bool _isPlus;

    /// Detail while transcribing ("Transcribing…", "Writing with Claude…", …) or the message text.
    [ObservableProperty] string _detail = "";

    /// Preview/final text (shown with the tail kept, max 3 lines).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPreview), nameof(PreviewTail))]
    string _preview = "";

    /// Mic RMS level.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Bar0), nameof(Bar1), nameof(Bar2), nameof(Bar3), nameof(Bar4))]
    float _level;

    public bool IsListening => Phase == HudPhase.Listening;
    public bool IsTranscribing => Phase == HudPhase.Transcribing;
    public bool IsMessage => Phase == HudPhase.Message;
    public string ListeningText => IsPlus ? "Scribe+ listening…" : "Listening…";
    public bool ShowPreview => Preview.Length > 0 && Phase is HudPhase.Listening or HudPhase.Transcribing;
    public bool PreviewIsSecondary => Phase == HudPhase.Listening;
    public string PreviewTail => Tail(Preview);

    public bool Bar0 => BarOn(0, Level);
    public bool Bar1 => BarOn(1, Level);
    public bool Bar2 => BarOn(2, Level);
    public bool Bar3 => BarOn(3, Level);
    public bool Bar4 => BarOn(4, Level);

    /// Level bar mapping: dB = 20·log10(max(rms, 1e-5)); v = clamp((dB + 55) / 45, 0, 1).
    public static double LevelValue(float rms)
    {
        var db = 20 * Math.Log10(Math.Max(rms, 1e-5));
        return Math.Clamp((db + 55) / 45, 0, 1);
    }

    /// Bar i (0–4) is red when i/5 &lt; v.
    public static bool BarOn(int i, float rms) => i / 5.0 < LevelValue(rms);

    /// Keeps the end of long text (what was just said), cut at a word boundary, with "… " in front.
    public static string Tail(string s, int max = 200)
    {
        if (s.Length <= max) return s;
        var cut = s[^max..];
        var sp = cut.IndexOf(' ');
        return "… " + (sp >= 0 ? cut[(sp + 1)..] : cut);
    }
}
