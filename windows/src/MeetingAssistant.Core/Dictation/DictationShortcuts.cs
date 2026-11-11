using MeetingAssistant.Core.Input;
using MeetingAssistant.Core.Storage;

namespace MeetingAssistant.Core.Dictation;

/// <summary>
/// Converts between the stored shortcut (<see cref="HotKeySetting"/> in `preferences.v1`) and the
/// keyboard-hook shortcut (<see cref="HotKey"/>), and builds the texts that show them (spec 04 §3.4,
/// §6.2, §6.7). A shortcut stored by the Mac (no `"platform": "windows"`) has Mac key codes that do
/// not map to Windows keys; it is read as the Windows default for that slot.
/// </summary>
public static class DictationShortcuts
{
    /// The hook shortcut for a stored one; null = "Not set". Mac-coded shortcuts become <paramref name="fallback"/>.
    public static HotKey? ToHotKey(HotKeySetting? s, HotKey fallback)
    {
        if (s is null) return null;
        if (!s.IsWindows) return fallback;
        if (s.ModifierOnly) return HotKey.FromModifiers(s.HeldKeys);
        return HotKey.Combo(s.KeyCode, s.Modifiers);
    }

    /// The stored form of a hook shortcut (null = "Not set").
    public static HotKeySetting? ToSetting(HotKey? k)
    {
        if (k is null) return null;
        return new HotKeySetting(k.KeyCode, k.Modifiers, k.ModifierOnly, k.ModifierKeys?.ToList());
    }

    /// The word-for-word shortcut of these preferences (null when not set or dictation is off).
    public static HotKey? Plain(Preferences p) => p.DictationEnabled ? ToHotKey(p.DictationHotKey, HotKey.DefaultPlain) : null;

    /// The Scribe+ shortcut of these preferences (null when not set or dictation is off).
    public static HotKey? Plus(Preferences p) => p.DictationEnabled ? ToHotKey(p.ScribePlusHotKey, HotKey.DefaultPlus) : null;

    /// Text of a shortcut button: its display name, or `Not set`.
    public static string Display(HotKey? k, Func<int, string?>? keyChar = null) => k?.Display(keyChar) ?? "Not set";

    /// What the shortcut recorder shows while waiting for a key (spec 04 §3.4, verbatim).
    public const string RecordingPrompt = "Press shortcut… (esc to cancel)";
}
