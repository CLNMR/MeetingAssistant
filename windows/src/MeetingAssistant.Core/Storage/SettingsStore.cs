using System.Text.Json.Nodes;
using MeetingAssistant.Core.Json;

namespace MeetingAssistant.Core.Storage;

/// <summary>
/// The Windows counterpart of the Mac's user-defaults domain `de.local.MeetingScribe`: one JSON
/// object in <see cref="AppPaths.PreferencesFile"/> whose top-level keys are the Mac defaults keys
/// (`preferences.v1`, `assist.settings.v1`, `aya.config.v2`, `appMode`, `dismissedSplitHints`,
/// `floatingPanel.open`, `floatingPanel.fontSize`, `modelsReady`, …). Values are stored as JSON
/// (blobs as nested objects). Every change is written immediately and atomically.
/// </summary>
public sealed class SettingsStore
{
    readonly object _lock = new();
    readonly string _path;
    JsonObject _values;

    // Keys used on both platforms (spec 01 "Preferences relevant to Scribe", 04 §6, 02 §2.1).
    public const string PreferencesKey = "preferences.v1";
    public const string AssistSettingsKey = "assist.settings.v1";
    public const string AyaConfigKey = "aya.config.v2";
    public const string AppModeKey = "appMode";
    public const string DismissedSplitHintsKey = "dismissedSplitHints";
    public const string FloatingPanelOpenKey = "floatingPanel.open";
    public const string FloatingPanelFontSizeKey = "floatingPanel.fontSize";
    public const string ModelsReadyKey = "modelsReady";

    /// Opens the store at `path` (default <see cref="AppPaths.PreferencesFile"/>). A missing or
    /// unreadable file gives an empty store.
    public SettingsStore(string? path = null)
    {
        _path = path ?? AppPaths.PreferencesFile;
        _values = Read(_path);
    }

    static SettingsStore? _shared;
    /// The app's store (created on first use).
    public static SettingsStore Shared
    {
        get => _shared ??= new SettingsStore();
        set => _shared = value;
    }

    static JsonObject Read(string path)
    {
        try { return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject() : new JsonObject(); }
        catch { return new JsonObject(); }
    }

    /// File backing the store.
    public string FilePath => _path;

    /// A copy of the raw value, or null when absent.
    public JsonNode? Get(string key)
    {
        lock (_lock) return _values[key]?.DeepClone();
    }

    /// Whether the key exists (even with a JSON null).
    public bool Contains(string key)
    {
        lock (_lock) return _values.ContainsKey(key);
    }

    /// Sets (or with null removes) a value and saves.
    public void Set(string key, JsonNode? value)
    {
        lock (_lock)
        {
            if (value is null) _values.Remove(key); else _values[key] = value.DeepClone();
            JsonIO.WriteText(_path, JsonIO.Format(_values));
        }
    }

    public string? GetString(string key) => Get(key) is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    public bool? GetBool(string key) => Get(key) is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
    public double? GetDouble(string key) => Get(key) is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
    public void SetString(string key, string? value) => Set(key, value is null ? null : JsonValue.Create(value));
    public void SetBool(string key, bool value) => Set(key, JsonValue.Create(value));
    public void SetDouble(string key, double value) => Set(key, JsonValue.Create(value));

    /// A typed blob (e.g. `assist.settings.v1`), or null when absent or unreadable.
    public T? GetObject<T>(string key) where T : class
    {
        var n = Get(key);
        if (n is null) return null;
        try
        {
            // The Mac stores some blobs as JSON strings; accept both forms.
            if (n is JsonValue v && v.TryGetValue<string>(out var text)) return JsonIO.Deserialize<T>(text);
            return JsonIO.Deserialize<T>(n.ToJsonString());
        }
        catch { return null; }
    }

    /// Stores a typed blob as a nested object.
    public void SetObject<T>(string key, T value) => Set(key, JsonNode.Parse(JsonIO.Serialize(value)));

    // MARK: UI state helpers (spec 01 "Other UI state")

    /// Session ids whose split hint was dismissed (comma-separated `dismissedSplitHints`).
    public HashSet<string> DismissedSplitHints =>
        (GetString(DismissedSplitHintsKey) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

    /// Remembers a dismissed split hint.
    public void DismissSplitHint(string sessionId)
    {
        var ids = (GetString(DismissedSplitHintsKey) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (ids.Contains(sessionId)) return;
        ids.Add(sessionId);
        SetString(DismissedSplitHintsKey, string.Join(",", ids));
    }
}
