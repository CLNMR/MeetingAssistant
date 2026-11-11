using System.Text.Json.Nodes;
using MeetingAssistant.App.Infrastructure;
using MeetingAssistant.Core.Storage;

namespace MeetingAssistant.App.Services;

/// <see cref="IUiState"/> backed by the app's single preferences file (<see cref="SettingsStore"/>),
/// so `appMode`, `floatingPanel.*`, `mainWindow.frame`, `dismissedSplitHints` survive a restart
/// (spec 01 "Other UI state", 03 §1 "Mode persistence"). `myName` and `dictationEnabled` live inside
/// the `preferences.v1` blob like on the Mac and are read and written through <see cref="AppServices"/>.
public sealed class SettingsUiState : IUiState
{
    readonly SettingsStore _store;
    readonly AppServices _services;

    public SettingsUiState(SettingsStore store, AppServices services)
    {
        _store = store;
        _services = services;
        services.PreferencesChanged += () =>
        {
            Changed?.Invoke(UiKeys.MyName);
            Changed?.Invoke(UiKeys.DictationEnabled);
        };
    }

    public event Action<string>? Changed;

    public T Get<T>(string key, T fallback)
    {
        if (key == UiKeys.MyName && _services.Preferences.MyName is T name) return name;
        if (key == UiKeys.DictationEnabled && _services.Preferences.DictationEnabled is T enabled) return enabled;
        var node = _store.Get(key);
        if (node is not JsonValue v) return fallback;
        // Numbers may be stored as integers or doubles; convert between the two.
        if (typeof(T) == typeof(double) && v.TryGetValue<double>(out var d)) return (T)(object)d;
        if (typeof(T) == typeof(int) && v.TryGetValue<double>(out var i)) return (T)(object)(int)i;
        return v.TryGetValue<T>(out var t) ? t : fallback;
    }

    public void Set<T>(string key, T value)
    {
        if (key == UiKeys.MyName && value is string n) { _services.UpdatePreferences(p => p.MyName = n); return; }
        if (key == UiKeys.DictationEnabled && value is bool b) { _services.UpdatePreferences(p => p.DictationEnabled = b); return; }
        _store.Set(key, value is null ? null : JsonValue.Create(value));
        Changed?.Invoke(key);
    }
}
