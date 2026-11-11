namespace MeetingAssistant.App.Infrastructure;

/// Small key/value store for UI state and the few preferences the shell reads (mode, floating window,
/// my name…). Keys are the Mac's preference keys (spec 01 "Preferences relevant to Scribe", 03 §1, §6.4).
/// It is an interface so it can be backed by the Core preferences store (Core/Storage/Preferences)
/// once that exists; until then the app uses <see cref="MemoryUiState"/>.
public interface IUiState
{
    /// The stored value, or <paramref name="fallback"/> when the key is missing or has another type.
    T Get<T>(string key, T fallback);
    /// Stores a value (null removes the key).
    void Set<T>(string key, T value);
    /// Raised with the key after a value changed.
    event Action<string>? Changed;
}

/// Keys used by the shell. Mac names are kept; Windows-only keys are marked.
public static class UiKeys
{
    /// "scribe" or "assist" (spec 03 §1 "Mode persistence").
    public const string AppMode = "appMode";
    /// Floating live transcript open when the app quit (spec 01 "Floating live transcript window").
    public const string FloatingOpen = "floatingPanel.open";
    /// Floating window text size, 9–28, default 13.
    public const string FloatingFontSize = "floatingPanel.fontSize";
    /// Windows only: floating window bounds "left,top,width,height" in DIPs (the Mac uses a frame autosave name).
    public const string FloatingFrame = "floatingPanel.frame";
    /// Windows only: main window bounds "left,top,width,height" and "max" when maximised.
    public const string MainFrame = "mainWindow.frame";
    /// Preference: the user's name (default "Me").
    public const string MyName = "myName";
    /// Preference: push-to-talk dictation on (default true).
    public const string DictationEnabled = "dictationEnabled";
    /// Live screen width shares and transcript height (spec 03 §6.4, §6.6).
    public const string SummaryShare = "assistLive.summaryShare";
    public const string OpenShare = "assistLive.openShare";
    public const string TranscriptHeight = "assistLive.transcriptHeight";
}

/// In-memory <see cref="IUiState"/> (nothing survives a restart). Used until the Core preferences store
/// is wired in, and by tests.
public sealed class MemoryUiState : IUiState
{
    readonly Dictionary<string, object?> _values = new();
    public event Action<string>? Changed;

    public T Get<T>(string key, T fallback)
        => _values.TryGetValue(key, out var v) && v is T t ? t : fallback;

    public void Set<T>(string key, T value)
    {
        if (value is null) _values.Remove(key); else _values[key] = value;
        Changed?.Invoke(key);
    }
}
