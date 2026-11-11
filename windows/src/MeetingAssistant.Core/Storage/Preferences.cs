using System.Text.Json.Nodes;
using MeetingAssistant.Core.Model;

namespace MeetingAssistant.Core.Storage;

/// <summary>
/// A push-to-talk shortcut (spec 04 §3.4), stored like the Mac's `HotKey`:
/// `{ "keyCode", "modifiers", "modifierOnly", "modifierKeys"? }` plus `"platform": "windows"`,
/// because Windows virtual-key codes differ from Mac key codes. On Windows `keyCode` and
/// `modifierKeys` are virtual-key codes (VK_RCONTROL = 0xA3 …) and `modifiers` holds the
/// MOD_* bits of a key combination (MOD_ALT 1, MOD_CONTROL 2, MOD_SHIFT 4, MOD_WIN 8).
/// </summary>
public sealed record HotKeySetting(int KeyCode, int Modifiers, bool ModifierOnly, IReadOnlyList<int>? ModifierKeys = null, string? Platform = "windows")
{
    public const int VkLShift = 0xA0, VkRShift = 0xA1, VkLControl = 0xA2, VkRControl = 0xA3, VkLMenu = 0xA4, VkRMenu = 0xA5, VkLWin = 0x5B, VkRWin = 0x5C;

    /// Order of modifier keys in a modifier-only shortcut (like the Mac: control, alt/option, shift, win/command; left before right).
    public static readonly IReadOnlyList<int> ModifierOrder = new[] { VkLControl, VkRControl, VkLMenu, VkRMenu, VkLShift, VkRShift, VkLWin, VkRWin };

    /// Windows default for word-for-word dictation: Right Ctrl alone (Right Alt is AltGr on most European layouts).
    public static HotKeySetting DefaultPlain => new(VkRControl, 0, true);
    /// Windows default for Scribe+: Right Ctrl + Right Shift (a superset of the plain one, so it upgrades a running dictation).
    public static HotKeySetting DefaultPlus => FromModifierKeys(new[] { VkRControl, VkRShift });

    /// Modifier-only shortcut from the keys held together.
    public static HotKeySetting FromModifierKeys(IEnumerable<int> keys)
    {
        var set = keys.ToHashSet();
        var ordered = ModifierOrder.Where(set.Contains).ToList();
        if (ordered.Count == 0) throw new ArgumentException("no modifier keys");
        return new HotKeySetting(ordered[0], 0, true, ordered.Count > 1 ? ordered : null);
    }

    /// All keys held for a modifier-only shortcut.
    public IReadOnlyList<int> HeldKeys => ModifierKeys ?? new[] { KeyCode };

    /// Whether this shortcut was written by the Windows app (Mac key codes cannot be used).
    public bool IsWindows => Platform == "windows";

    public JsonObject ToJson()
    {
        var o = new JsonObject { ["keyCode"] = KeyCode, ["modifiers"] = Modifiers, ["modifierOnly"] = ModifierOnly };
        if (ModifierKeys is not null) o["modifierKeys"] = new JsonArray(ModifierKeys.Select(k => (JsonNode?)k).ToArray());
        if (Platform is not null) o["platform"] = Platform;
        return o;
    }

    public static HotKeySetting? FromJson(JsonNode? n)
    {
        if (n is not JsonObject o) return null;
        try
        {
            var keys = (o["modifierKeys"] as JsonArray)?.Select(x => x!.GetValue<int>()).ToList();
            return new HotKeySetting(o["keyCode"]!.GetValue<int>(), o["modifiers"]?.GetValue<int>() ?? 0,
                o["modifierOnly"]?.GetValue<bool>() ?? false, keys, o["platform"]?.GetValue<string>());
        }
        catch { return null; }
    }

    public bool Equals(HotKeySetting? other) =>
        other is not null && KeyCode == other.KeyCode && Modifiers == other.Modifiers && ModifierOnly == other.ModifierOnly
        && Platform == other.Platform && (ModifierKeys ?? Array.Empty<int>()).SequenceEqual(other.ModifierKeys ?? Array.Empty<int>());

    public override int GetHashCode() => HashCode.Combine(KeyCode, Modifiers, ModifierOnly, Platform, ModifierKeys?.Count ?? 0);
}

/// <summary>
/// App preferences: the `preferences.v1` blob (spec 01 "Preferences relevant to Scribe", 04 §6.1–6.3),
/// same keys and defaults as the Mac. Missing or unreadable keys fall back to defaults, so new
/// settings never reset old ones; an explicit JSON null for a shortcut means "no shortcut", a
/// missing one means the default. Assist settings (`assist.settings.v1`) and the Aya config are
/// separate blobs owned by the Assist code (see <see cref="SettingsStore"/>).
/// </summary>
public sealed class Preferences
{
    public const string ParakeetUltra = "parakeet-ultra", Qwen3 = "qwen3-1.7b";

    /// ASR engine for meetings: `parakeet-ultra` (default) or `qwen3-1.7b`. Not used by dictation.
    public string Engine { get; set; } = ParakeetUltra;
    /// `auto` (default), `german`, `english`.
    public string Language { get; set; } = "auto";
    /// Label for the user's speaker; empty → "Me".
    public string MyName { get; set; } = "Me";
    /// Microphone (stable device id); null = system default.
    public string? InputDeviceUID { get; set; }
    /// Show live transcript while recording.
    public bool LiveTranscription { get; set; } = true;
    /// Create full transcript automatically after recording.
    public bool AutoTranscribe { get; set; } = true;
    /// Mode preselected at launch.
    public MeetingMode DefaultMode { get; set; } = MeetingMode.Call;
    /// Push-to-talk dictation.
    public bool DictationEnabled { get; set; } = true;
    /// Word-for-word shortcut (null = not set).
    public HotKeySetting? DictationHotKey { get; set; } = HotKeySetting.DefaultPlain;
    /// Scribe+ shortcut (null = not set).
    public HotKeySetting? ScribePlusHotKey { get; set; } = HotKeySetting.DefaultPlus;
    /// Scribe+ sees the text around the cursor and the app's name.
    public bool ScribePlusContext { get; set; } = true;
    /// Keep one Claude process started in the background.
    public bool ScribePlusWarm { get; set; } = true;
    /// `sonnet` (default) or `haiku`.
    public string ScribePlusModel { get; set; } = Text.ScribePlus.DefaultModel;
    /// Fit spaces and capitals to the text around the cursor.
    public bool FitToCursor { get; set; } = true;
    /// Mute other sound while dictating (not during a recording).
    public bool MuteWhileDictating { get; set; } = true;

    /// The user's name for labels: trimmed `MyName`, or "Me" when empty.
    public string EffectiveName => MyName.Trim().Length == 0 ? "Me" : MyName.Trim();

    /// Language to store in a new session: null for `auto`.
    public string? SessionLanguage => Language == "auto" ? null : Language;

    public Preferences Clone() => FromJson(ToJson());

    /// The `preferences.v1` object.
    public JsonObject ToJson()
    {
        var o = new JsonObject
        {
            ["engine"] = Engine, ["language"] = Language, ["myName"] = MyName,
            ["liveTranscription"] = LiveTranscription, ["autoTranscribe"] = AutoTranscribe, ["defaultMode"] = DefaultMode.Raw(),
            ["dictationEnabled"] = DictationEnabled,
            ["dictationHotKey"] = DictationHotKey?.ToJson(),        // explicit null = "no shortcut"
            ["scribePlusHotKey"] = ScribePlusHotKey?.ToJson(),
            ["scribePlusContext"] = ScribePlusContext, ["scribePlusWarm"] = ScribePlusWarm, ["scribePlusModel"] = ScribePlusModel,
            ["fitToCursor"] = FitToCursor, ["muteWhileDictating"] = MuteWhileDictating,
        };
        if (InputDeviceUID is not null) o["inputDeviceUID"] = InputDeviceUID;
        return o;
    }

    /// Reads a `preferences.v1` object leniently: every missing or invalid key keeps its default.
    public static Preferences FromJson(JsonNode? node)
    {
        var p = new Preferences();
        if (node is JsonValue v && v.TryGetValue<string>(out var text))
        {
            try { node = JsonNode.Parse(text); } catch { return p; }
        }
        if (node is not JsonObject o) return p;
        string? Str(string k) => o[k] is JsonValue x && x.TryGetValue<string>(out var s) ? s : null;
        bool? Bool(string k) => o[k] is JsonValue x && x.TryGetValue<bool>(out var b) ? b : null;

        if (Str("engine") is { } e && (e == ParakeetUltra || e == Qwen3)) p.Engine = e;
        if (Str("language") is { } l && l is "auto" or "german" or "english") p.Language = l;
        if (Str("myName") is { } n) p.MyName = n;
        p.InputDeviceUID = Str("inputDeviceUID");
        if (Bool("liveTranscription") is { } lt) p.LiveTranscription = lt;
        if (Bool("autoTranscribe") is { } at) p.AutoTranscribe = at;
        if (Str("defaultMode") is { } m && ParseMode(m) is { } mode) p.DefaultMode = mode;
        if (Bool("dictationEnabled") is { } de) p.DictationEnabled = de;
        if (o.ContainsKey("dictationHotKey")) p.DictationHotKey = HotKeySetting.FromJson(o["dictationHotKey"]);
        if (o.ContainsKey("scribePlusHotKey")) p.ScribePlusHotKey = HotKeySetting.FromJson(o["scribePlusHotKey"]);
        if (Bool("scribePlusContext") is { } sc) p.ScribePlusContext = sc;
        if (Bool("scribePlusWarm") is { } sw) p.ScribePlusWarm = sw;
        if (Str("scribePlusModel") is { } sm) p.ScribePlusModel = sm;
        if (Bool("fitToCursor") is { } fc) p.FitToCursor = fc;
        if (Bool("muteWhileDictating") is { } mu) p.MuteWhileDictating = mu;
        return p;
    }

    static MeetingMode? ParseMode(string s) => s switch
    {
        "call" => MeetingMode.Call,
        "inPerson" => MeetingMode.InPerson,
        "hybrid" => MeetingMode.Hybrid,
        _ => null,
    };

    /// Loads `preferences.v1` from the store (defaults when absent).
    public static Preferences Load(SettingsStore? store = null) =>
        FromJson((store ?? SettingsStore.Shared).Get(SettingsStore.PreferencesKey));

    /// Saves `preferences.v1` to the store.
    public void Save(SettingsStore? store = null) => (store ?? SettingsStore.Shared).Set(SettingsStore.PreferencesKey, ToJson());
}
