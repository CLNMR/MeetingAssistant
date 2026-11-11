using System.Text.Json.Serialization;

namespace MeetingAssistant.Core.Input;

/// Windows virtual-key codes used by the dictation shortcuts (spec 04 §3.4). The low-level keyboard
/// hook reports left/right-specific codes for the modifiers.
public static class Vk
{
    public const int Back = 0x08, Tab = 0x09, Return = 0x0D, Escape = 0x1B, Space = 0x20;
    public const int PageUp = 0x21, PageDown = 0x22, End = 0x23, Home = 0x24;
    public const int Left = 0x25, Up = 0x26, Right = 0x27, Down = 0x28, Insert = 0x2D, Delete = 0x2E;
    public const int LWin = 0x5B, RWin = 0x5C;
    public const int F1 = 0x70, F24 = 0x87;
    public const int LShift = 0xA0, RShift = 0xA1, LControl = 0xA2, RControl = 0xA3, LMenu = 0xA4, RMenu = 0xA5;
    /// Generic (not left/right) codes, as delivered by some APIs.
    public const int Shift = 0x10, Control = 0x11, Menu = 0x12;
    /// Unassigned key sent to stop a lone Alt from opening the menu bar or Win from opening Start.
    public const int MaskKey = 0xE8;

    /// Left/right-specific modifier keys in shortcut order: Left Ctrl, Right Ctrl, Left Alt, Right Alt,
    /// Left Shift, Right Shift, Left Win, Right Win (the Mac order fn, ⌃, ⌥, ⇧, ⌘ without fn).
    public static readonly int[] ModifierKeys = { LControl, RControl, LMenu, RMenu, LShift, RShift, LWin, RWin };

    public static bool IsModifier(int vk) => ModifierKeys.Contains(vk) || vk is Shift or Control or Menu;
    public static bool IsFunctionKey(int vk) => vk is >= F1 and <= F24;
    public static bool IsAlt(int vk) => vk is LMenu or RMenu or Menu;
    public static bool IsWin(int vk) => vk is LWin or RWin;

    /// The `MOD_*` flag (RegisterHotKey values) a modifier key contributes to a key combination.
    public static int ModifierFlag(int vk) => vk switch
    {
        LMenu or RMenu or Menu => HotKey.ModAlt,
        LControl or RControl or Control => HotKey.ModControl,
        LShift or RShift or Shift => HotKey.ModShift,
        LWin or RWin => HotKey.ModWin,
        _ => 0,
    };
}

/// A user-chosen global shortcut (spec 04 §3.4): a key with modifiers (Ctrl+Alt+Space, or an F-key
/// alone) or one or more modifier keys held on their own (Right Ctrl; Right Ctrl + Right Shift).
/// JSON shape as on the Mac — `keyCode`, `modifiers`, `modifierOnly`, `modifierKeys` — but with
/// Windows virtual-key codes and `MOD_*` flags (the Windows preferences file is Windows-only).
public sealed record HotKey
{
    public const int ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8;

    [JsonPropertyName("keyCode")] public int KeyCode { get; init; }
    /// `MOD_ALT|MOD_CONTROL|MOD_SHIFT|MOD_WIN` for key combinations; 0 for modifier-only shortcuts.
    [JsonPropertyName("modifiers")] public int Modifiers { get; init; }
    [JsonPropertyName("modifierOnly")] public bool ModifierOnly { get; init; }
    /// All keys of a multi-key modifier-only shortcut; null = just <see cref="KeyCode"/>.
    [JsonPropertyName("modifierKeys")] public List<int>? ModifierKeys { get; init; }

    /// Default "Word for word" shortcut on Windows: Right Ctrl alone (Right Alt is AltGr on most
    /// European layouts, see spec 04 §3.4 Windows notes).
    public static HotKey DefaultPlain => FromModifiers(new[] { Vk.RControl });
    /// Default Scribe+ shortcut on Windows: Right Ctrl + Right Shift (a superset of the plain one,
    /// so adding Right Shift while dictating upgrades to Scribe+).
    public static HotKey DefaultPlus => FromModifiers(new[] { Vk.RControl, Vk.RShift });

    /// Modifier-only shortcut from the keys held together, in the fixed order of <see cref="Vk.ModifierKeys"/>.
    public static HotKey FromModifiers(IEnumerable<int> keys)
    {
        var set = keys.ToHashSet();
        var ordered = Vk.ModifierKeys.Where(set.Contains).ToList();
        if (ordered.Count == 0) throw new ArgumentException("No modifier key.", nameof(keys));
        return new HotKey { KeyCode = ordered[0], Modifiers = 0, ModifierOnly = true, ModifierKeys = ordered.Count > 1 ? ordered : null };
    }

    /// A key combination.
    public static HotKey Combo(int keyCode, int modifiers) => new() { KeyCode = keyCode, Modifiers = modifiers, ModifierOnly = false };

    [JsonIgnore] public IReadOnlyList<int> HeldKeys => ModifierKeys ?? new List<int> { KeyCode };

    /// Value equality including the key list.
    public bool Equals(HotKey? other) =>
        other is not null && KeyCode == other.KeyCode && Modifiers == other.Modifiers && ModifierOnly == other.ModifierOnly
        && (ModifierKeys ?? new List<int>()).SequenceEqual(other.ModifierKeys ?? new List<int>());

    public override int GetHashCode() => HashCode.Combine(KeyCode, Modifiers, ModifierOnly, ModifierKeys?.Count ?? 0);

    /// True when every key of this modifier-only shortcut is also part of <paramref name="other"/>
    /// (the Scribe+ shortcut "contains" the plain one: upgrade while dictating).
    public bool IsSubsetOf(HotKey other) =>
        ModifierOnly && other.ModifierOnly && HeldKeys.All(other.HeldKeys.Contains);

    /// Whether the hook should send <see cref="Vk.MaskKey"/> when the shortcut fires, so releasing
    /// Alt does not open the menu bar and releasing Win does not open Start.
    [JsonIgnore] public bool NeedsMaskKey => ModifierOnly
        ? HeldKeys.Any(k => Vk.IsAlt(k) || Vk.IsWin(k))
        : (Modifiers & (ModAlt | ModWin)) != 0;

    /// Text shown in Settings, e.g. "Right Ctrl", "Right Ctrl + Right Shift", "Ctrl+Alt+Space".
    /// <paramref name="keyChar"/> maps other keys to the character of the current layout.
    public string Display(Func<int, string?>? keyChar = null)
    {
        if (ModifierOnly)
            return HeldKeys.Count > 1 ? string.Join(" + ", HeldKeys.Select(ModifierName)) : ModifierName(KeyCode);
        var s = "";
        if ((Modifiers & ModControl) != 0) s += "Ctrl+";
        if ((Modifiers & ModAlt) != 0) s += "Alt+";
        if ((Modifiers & ModShift) != 0) s += "Shift+";
        if ((Modifiers & ModWin) != 0) s += "Win+";
        return s + KeyName(KeyCode, keyChar);
    }

    public static string ModifierName(int vk) => vk switch
    {
        Vk.LControl => "Left Ctrl",
        Vk.RControl => "Right Ctrl",
        Vk.LMenu => "Left Alt",
        Vk.RMenu => "Right Alt",
        Vk.LShift => "Left Shift",
        Vk.RShift => "Right Shift",
        Vk.LWin => "Left Win",
        Vk.RWin => "Right Win",
        _ => $"Key {vk}",
    };

    public static string KeyName(int vk, Func<int, string?>? keyChar = null)
    {
        switch (vk)
        {
            case Vk.Space: return "Space";
            case Vk.Return: return "Enter";
            case Vk.Tab: return "Tab";
            case Vk.Back: return "Backspace";
            case Vk.Delete: return "Delete";
            case Vk.Insert: return "Insert";
            case Vk.Escape: return "Esc";
            case Vk.Left: return "←";
            case Vk.Right: return "→";
            case Vk.Up: return "↑";
            case Vk.Down: return "↓";
            case Vk.Home: return "Home";
            case Vk.End: return "End";
            case Vk.PageUp: return "PgUp";
            case Vk.PageDown: return "PgDn";
        }
        if (Vk.IsFunctionKey(vk)) return $"F{vk - Vk.F1 + 1}";
        var c = keyChar?.Invoke(vk);
        if (!string.IsNullOrEmpty(c)) return c.ToUpperInvariant();
        if (vk is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A) return ((char)vk).ToString();
        return $"Key {vk}";
    }

    /// Error shown under a key combination that another app already registered.
    public string ConflictMessage(Func<int, string?>? keyChar = null) =>
        $"{Display(keyChar)} is already used by another app. Choose a different shortcut.";
}

/// What a keyboard event means for one shortcut.
public enum HotKeyEvent { None, Press, Release, Cancel }

/// Press/release/cancel logic of one global shortcut, fed with raw key events from the keyboard
/// hook (spec 04 §3.4). Pure, so it is unit-tested; the hook only translates and dispatches.
/// - Modifier-only: pressed when all required keys are down (other modifiers may also be down);
///   released when any of them goes up; a non-modifier key-down while pressed cancels.
/// - Key combination: pressed on key-down of the key while exactly the required modifiers are down;
///   released on its key-up; its key events are swallowed (not passed to the focused app).
/// Key repeat is ignored (press fires once until release).
public sealed class HotKeyMatcher
{
    readonly HashSet<int> _down = new();
    public HotKey? HotKey { get; private set; }
    public bool IsDown { get; private set; }

    public HotKeyMatcher(HotKey? hotKey = null) => HotKey = hotKey;

    /// Replace the shortcut. Returns Cancel when the old one was held (the running dictation is dropped).
    public HotKeyEvent Set(HotKey? hotKey)
    {
        HotKey = hotKey;
        if (!IsDown) return HotKeyEvent.None;
        IsDown = false;
        return HotKeyEvent.Cancel;
    }

    /// Forget all key state (e.g. after the hook was suspended).
    public HotKeyEvent Reset()
    {
        _down.Clear();
        if (!IsDown) return HotKeyEvent.None;
        IsDown = false;
        return HotKeyEvent.Cancel;
    }

    /// Feed one key event. <paramref name="swallow"/> is true when the event must not reach the app.
    public HotKeyEvent Feed(int vk, bool keyDown, out bool swallow)
    {
        swallow = false;
        bool isMod = Vk.IsModifier(vk);
        if (isMod) { if (keyDown) _down.Add(vk); else _down.Remove(vk); }
        var hk = HotKey;
        if (hk is null) return HotKeyEvent.None;

        if (hk.ModifierOnly)
        {
            bool all = hk.HeldKeys.All(_down.Contains);
            if (isMod)
            {
                if (all && !IsDown) { IsDown = true; return HotKeyEvent.Press; }
                if (!all && IsDown) { IsDown = false; return HotKeyEvent.Release; }
                return HotKeyEvent.None;
            }
            if (keyDown && IsDown) { IsDown = false; return HotKeyEvent.Cancel; }
            return HotKeyEvent.None;
        }

        if (vk != hk.KeyCode) return HotKeyEvent.None;
        if (keyDown)
        {
            if (IsDown) { swallow = true; return HotKeyEvent.None; }        // key repeat
            if (CurrentModifiers() != hk.Modifiers) return HotKeyEvent.None;
            IsDown = true;
            swallow = true;
            return HotKeyEvent.Press;
        }
        if (!IsDown) return HotKeyEvent.None;
        IsDown = false;
        swallow = true;
        return HotKeyEvent.Release;
    }

    int CurrentModifiers() => _down.Aggregate(0, (m, k) => m | Vk.ModifierFlag(k));
}

/// Outcome of recording a shortcut in Settings.
public enum ShortcutRecordingResult { Waiting, Saved, Cancelled, Beep }

/// The Settings "Press shortcut… (esc to cancel)" gesture (spec 04 §3.4, "Recording a shortcut"):
/// Esc without modifiers cancels; a key with Ctrl/Alt/Shift/Win held, or an F-key alone, saves a key
/// combination; any other key without modifiers beeps; modifier keys alone are collected and saved as
/// a modifier-only shortcut once all are released.
public sealed class ShortcutRecording
{
    readonly HashSet<int> _held = new();
    readonly HashSet<int> _gesture = new();
    public HotKey? Result { get; private set; }

    public ShortcutRecordingResult Feed(int vk, bool keyDown)
    {
        if (Vk.IsModifier(vk))
        {
            var key = vk switch { Vk.Shift => Vk.LShift, Vk.Control => Vk.LControl, Vk.Menu => Vk.LMenu, _ => vk };
            if (keyDown) { _held.Add(key); _gesture.Add(key); return ShortcutRecordingResult.Waiting; }
            _held.Remove(key);
            if (_held.Count == 0 && _gesture.Count > 0)
            {
                Result = HotKey.FromModifiers(_gesture);
                return ShortcutRecordingResult.Saved;
            }
            return ShortcutRecordingResult.Waiting;
        }
        if (!keyDown) return ShortcutRecordingResult.Waiting;
        int mods = _held.Aggregate(0, (m, k) => m | Vk.ModifierFlag(k));
        if (vk == Vk.Escape && mods == 0) return ShortcutRecordingResult.Cancelled;
        if (mods == 0 && !Vk.IsFunctionKey(vk)) return ShortcutRecordingResult.Beep;
        Result = HotKey.Combo(vk, mods);
        return ShortcutRecordingResult.Saved;
    }
}

/// Identity of the focused field, to tell whether the cursor moved to another field between the
/// shortcut press and the paste (spec 04 §3.1 step 3).
public sealed record FieldIdentity(int ProcessId, int[]? RuntimeId, int? ControlType, (int X, int Y)? Position)
{
    /// Same field: same process, and (either side has no focused element) or the same element
    /// (UI Automation runtime id) or the same control type at the same screen position.
    public bool IsSameField(FieldIdentity other)
    {
        if (ProcessId != other.ProcessId) return false;
        if (RuntimeId is null || other.RuntimeId is null) return true;
        if (RuntimeId.AsSpan().SequenceEqual(other.RuntimeId)) return true;
        return ControlType == other.ControlType && Position == other.Position;
    }
}
