using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using MeetingAssistant.App.Services;
using MeetingAssistant.App.ViewModels;
using MeetingAssistant.Core.Assist;
using MeetingAssistant.Core.Claude;
using MeetingAssistant.Core.Dictation;
using MeetingAssistant.Core.Input;
using MeetingAssistant.Core.Storage;
using MeetingAssistant.Core.Text;
using MeetingAssistant.Platform.Windows.Input;
using MeetingAssistant.Speech;

namespace MeetingAssistant.App.Dictation;

/// <summary>
/// The dictation feature in the running app (spec 04 §1–§3, §6.2, §6.7): owns the platform-neutral
/// <see cref="DictationController"/> with the Windows parts (WASAPI mic, UI Automation, clipboard +
/// Ctrl+V, output muter), the two global shortcuts (low-level keyboard hook), the HUD, the tray items
/// ("Dictation: hold …", "Copy Last Dictation", "Unfinished Dictations (N)"), the spare Claude
/// process and the Aya names of the vocabulary. Lives on the UI thread.
/// </summary>
public sealed partial class DictationService : CommunityToolkit.Mvvm.ComponentModel.ObservableObject, IDisposable
{
    readonly AppServices _services;
    readonly HudViewModel _hud;
    readonly ShellViewModel _shell;
    readonly GlobalHotKeys? _keys;
    readonly GlobalHotKey? _plainKey, _plusKey;
    string? _locatedClaude;
    (bool, HotKeySetting?, HotKeySetting?, string, bool) _applied;
    MenuItem? _unfinishedSlot;

    /// The running app's dictation (null before start-up).
    public static DictationService? Shared { get; private set; }

    /// The dictation state machine.
    public DictationController Controller { get; }

    public DictationService(AppServices services, HudViewModel hud, ShellViewModel shell)
    {
        _services = services;
        _hud = hud;
        _shell = shell;
        Shared = this;

        Controller = new DictationController(
            new WindowsDictationMicrophone(() => _services.Preferences.InputDeviceUID),
            new DelegateRecognizer(SpeechEngine.Shared.Dictate),
            new WindowsFocusedFields(),
            new WindowsDictationOutput(),
            StartClaude,
            () => _services.Preferences,
            new WindowsOutputMuter(),
            isRecording: () => ScribeController.Shared is { IsRecording: true });
        Controller.Vocabulary = services.Vocabulary;
        Controller.Changed += Sync;
        Controller.Log += DictationLog.Write;
        services.VocabularyChanged += () => AppServices.Ui(() => Controller.Vocabulary = _services.Vocabulary);
        services.PreferencesChanged += () => AppServices.Ui(ApplyPreferences);
        shell.CopyLastDictationRequested += () => Controller.CopyLast();

        try
        {
            _keys = new GlobalHotKeys(SynchronizationContext.Current);
            _plainKey = _keys.Add();
            _plusKey = _keys.Add();
            _plainKey.Pressed += () => Controller.Press(DictationMode.Plain);
            _plusKey.Pressed += () => Controller.Press(DictationMode.Plus);
            _plainKey.Released += Release;
            _plusKey.Released += Release;
            _plainKey.Cancelled += Controller.Cancel;
            _plusKey.Cancelled += Controller.Cancel;
        }
        catch (Exception e)
        {
            PlainError = $"The keyboard shortcuts could not be installed: {e.Message}";
        }

        ApplyPreferences(force: true);
        Sync();
    }

    void Release() => _ = Controller.ReleaseAsync();

    // ================= Claude (spec 04 §2.3, §2.4) =================

    AssistConnection? _aya;

    /// Assist's connection: its Claude binary is reused, its Aya lists give the vocabulary names.
    /// Set by the app once the Assist area is up.
    public AssistConnection? Aya
    {
        get => _aya;
        set
        {
            if (ReferenceEquals(_aya, value)) return;
            if (_aya is not null) { _aya.PropertyChanged -= OnAyaChanged; _aya.VocabularyRefresh = null; }
            _aya = value;
            if (value is not null)
            {
                value.PropertyChanged += OnAyaChanged;
                value.VocabularyRefresh = () => RefreshAyaNamesAsync(force: false);
            }
            OnPropertyChanged(nameof(ClaudeReady));
            OnPropertyChanged(nameof(AyaConnected));
        }
    }

    void OnAyaChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AssistConnection.ClaudeReady):
            case nameof(AssistConnection.ClaudeBinary):
            case nameof(AssistConnection.ClaudeAuth):
                AppServices.Ui(() => OnPropertyChanged(nameof(ClaudeReady)));
                break;
            case nameof(AssistConnection.HasApiKey):
            case nameof(AssistConnection.Config):
                AppServices.Ui(() => OnPropertyChanged(nameof(AyaConnected)));
                break;
        }
    }

    /// Finds Assist's connection when it was not there yet (the Assist area may start later).
    public Func<AssistConnection?>? AyaLocator { get; set; }

    AssistConnection? CurrentAya()
    {
        if (_aya is null && AyaLocator?.Invoke() is { } found) Aya = found;
        return _aya;
    }

    /// Settings › Dictation › Claude Code: "Ready" when Assist's Claude check succeeded.
    public bool ClaudeReady => CurrentAya()?.ClaudeReady == true;

    /// Aya is connected (the vocabulary's *Refresh* button is enabled).
    public bool AyaConnected => CurrentAya()?.IsConnected == true;

    /// Starts one Scribe+ process; the binary found by Assist wins, else the usual lookup.
    ClaudeRewriteSession StartClaude(string model)
    {
        var aya = CurrentAya();
        var binary = aya?.ClaudeBinary ?? (_locatedClaude ??= ClaudeCli.Locate(aya?.Settings.ClaudePath));
        if (binary is null) throw ClaudeException.NotInstalled();
        return new ClaudeRewriteSession(binary, ScribePlus.System, model, ScribePlus.Effort);
    }

    // ================= Shortcuts and settings (spec 04 §3.4, §6.2) =================

    /// Error under the word-for-word shortcut (taken by another app), or null.
    [ObservableProperty] string? _plainError;
    /// Error under the Scribe+ shortcut, or null.
    [ObservableProperty] string? _plusError;

    /// Registers the shortcuts again and updates the spare when a relevant preference changed.
    void ApplyPreferences() => ApplyPreferences(force: false);

    void ApplyPreferences(bool force)
    {
        var p = _services.Preferences;
        var key = (p.DictationEnabled, p.DictationHotKey, p.ScribePlusHotKey, p.ScribePlusModel, p.ScribePlusWarm);
        if (!force && key.Equals(_applied)) return;
        bool enabledNow = p.DictationEnabled && (force || !_applied.Item1);
        _applied = key;
        RegisterShortcuts();
        Controller.UpdateSpare();
        if (enabledNow) Preload();
        Sync();
    }

    void RegisterShortcuts()
    {
        if (_plainKey is null || _plusKey is null) return;
        var p = _services.Preferences;
        PlainError = _plainKey.Register(DictationShortcuts.Plain(p));
        PlusError = _plusKey.Register(DictationShortcuts.Plus(p));
    }

    /// Loads Parakeet in the background so the first dictation is fast (only with downloaded models).
    static void Preload()
    {
        if (!ModelCatalog.CoreModelsReady()) return;
        _ = Task.Run(() =>
        {
            try { SpeechEngine.Shared.Preload(); }
            catch (Exception e) { DictationLog.Write($"preload failed: {e.Message}"); }
        });
    }

    /// The display text of a shortcut on the current keyboard layout.
    public static string Display(HotKey? k) => DictationShortcuts.Display(k, GlobalHotKeys.KeyChar);

    /// Records the next shortcut gesture (global shortcuts are suspended meanwhile). <paramref name="done"/>
    /// gets the new shortcut, or null when the user pressed Esc.
    public bool RecordShortcut(Action<HotKey?> done)
    {
        if (_keys is null) return false;
        _keys.RecordShortcut((result, hotKey) => done(result == ShortcutRecordingResult.Saved ? hotKey : null));
        return true;
    }

    /// Stops a shortcut recording without a result (Settings closed).
    public void CancelRecording() => _keys?.CancelRecording();

    // ================= HUD and tray (spec 04 §1.7, §6.7) =================

    void Sync()
    {
        var c = Controller;
        _hud.IsPlus = c.Mode == DictationMode.Plus;
        _hud.Detail = c.Phase == DictationPhase.Message ? c.Message : c.Detail;
        _hud.Preview = c.Preview;
        _hud.Level = c.Level;
        _hud.Phase = c.Phase switch
        {
            DictationPhase.Listening => HudPhase.Listening,
            DictationPhase.Transcribing => HudPhase.Transcribing,
            DictationPhase.Message => HudPhase.Message,
            _ => HudPhase.Hidden,
        };
        var p = _services.Preferences;
        var plain = DictationShortcuts.Plain(p);
        var plus = DictationShortcuts.Plus(p);
        _shell.DictationHotKeyText = plain is null ? null : Display(plain);
        _shell.ScribePlusHotKeyText = plus is null ? null : Display(plus);
        _shell.HasLastDictation = p.DictationEnabled && c.LastText is not null;
        UpdateUnfinishedMenu();
    }

    /// Fills the tray's "Unfinished Dictations (N)" slot; the list is re-read when the menu opens.
    public void AttachTray(MenuItem slot, ContextMenu menu)
    {
        _unfinishedSlot = slot;
        menu.Opened += (_, _) => Controller.RefreshUnfinished();
        UpdateUnfinishedMenu();
    }

    IReadOnlyList<DictationRecovery.Item>? _shownUnfinished;

    void UpdateUnfinishedMenu()
    {
        var slot = _unfinishedSlot;
        if (slot is null) return;
        var items = Controller.Unfinished;
        bool visible = _services.Preferences.DictationEnabled && items.Count > 0;
        slot.Visibility = visible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        if (ReferenceEquals(items, _shownUnfinished)) return;
        _shownUnfinished = items;
        slot.Header = $"Unfinished Dictations ({items.Count})";
        slot.Items.Clear();
        foreach (var item in items)
        {
            var entry = new MenuItem { Header = DictationRecovery.Label(item) };
            var again = new MenuItem { Header = "Transcribe Again and Copy" };
            again.Click += (_, _) => _ = Controller.RetryAsync(item);
            var delete = new MenuItem { Header = "Delete" };
            delete.Click += (_, _) => Controller.Discard(item);
            entry.Items.Add(again);
            entry.Items.Add(delete);
            slot.Items.Add(entry);
        }
    }

    // ================= Vocabulary names from Aya (spec 04 §5.1) =================

    /// The vocabulary's *Refresh* button is busy (`Loading…`).
    [ObservableProperty] bool _refreshingAyaNames;

    /// Builds the Aya terms from the Assist lists and stores them. Without <paramref name="force"/>
    /// only when *Use names from Aya* is on and they are older than 12 h or from another term
    /// version (called after the Aya lists were loaded). An empty result keeps the stored terms.
    public async Task RefreshAyaNamesAsync(bool force)
    {
        var aya = CurrentAya();
        if (aya is null || RefreshingAyaNames) return;
        var v = _services.Vocabulary;
        if (!force && !v.NeedsAyaRefresh(DateTime.UtcNow)) return;
        AppServices.Ui(() => RefreshingAyaNames = true);
        try
        {
            var (companies, people, projects) = await aya.AyaNamesAsync();
            var terms = Vocabulary.TermsFromAya(companies, people, projects);
            if (terms.Count > 0) _services.UpdateVocabulary(x => x.SetAyaTerms(terms, DateTime.UtcNow));
        }
        catch (Exception e)
        {
            DictationLog.Write($"Aya names failed: {e.Message}");
        }
        finally
        {
            AppServices.Ui(() => RefreshingAyaNames = false);
        }
    }

    public void Dispose()
    {
        _keys?.Dispose();
        Controller.Dispose();
        if (_aya is not null) _aya.VocabularyRefresh = null;
        if (Shared == this) Shared = null;
    }
}
