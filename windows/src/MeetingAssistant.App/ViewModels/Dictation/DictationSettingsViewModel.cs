using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeetingAssistant.App.Services;
using MeetingAssistant.Core.Dictation;
using MeetingAssistant.Core.Input;
using MeetingAssistant.Core.Storage;
using MeetingAssistant.Core.Text;
using MeetingAssistant.Platform.Windows.SystemServices;

namespace MeetingAssistant.App.Dictation;

/// One shortcut button of Settings › Dictation (spec 04 §3.4 "Recording a shortcut"): shows the
/// shortcut or `Not set`; a click shows `Press shortcut… (esc to cancel)` and records the next
/// gesture (global shortcuts are suspended meanwhile); ⓧ clears it.
public sealed partial class ShortcutSlotViewModel : ObservableObject
{
    readonly Func<HotKeySetting?> _get;
    readonly Action<HotKeySetting?> _set;
    readonly HotKey _fallback;

    public ShortcutSlotViewModel(Func<HotKeySetting?> get, Action<HotKeySetting?> set, HotKey fallback)
    {
        _get = get;
        _set = set;
        _fallback = fallback;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Text), nameof(CanClear))]
    bool _isRecording;

    /// Button text.
    public string Text => IsRecording ? DictationShortcuts.RecordingPrompt : DictationService.Display(DictationShortcuts.ToHotKey(_get(), _fallback));

    /// The ⓧ button is shown.
    public bool CanClear => !IsRecording && _get() is not null;

    [RelayCommand]
    void Record()
    {
        var service = DictationService.Shared;
        if (service is null) return;
        if (IsRecording) { service.CancelRecording(); IsRecording = false; return; }
        IsRecording = service.RecordShortcut(k =>
        {
            IsRecording = false;
            if (k is not null) _set(DictationShortcuts.ToSetting(k));
            Refresh();
        });
    }

    [RelayCommand]
    void Clear()
    {
        _set(null);
        Refresh();
    }

    /// Re-reads the stored shortcut.
    public void Refresh()
    {
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(CanClear));
    }

    /// Stops a running recording (the page closes).
    public void Stop()
    {
        if (!IsRecording) return;
        DictationService.Shared?.CancelRecording();
        IsRecording = false;
    }
}

/// A choice of a picker.
public sealed record Choice(string Value, string Title);

/// Settings › Dictation (spec 04 §6.2). Every change is saved at once (through <see cref="AppServices"/>).
public sealed partial class DictationSettingsViewModel : ObservableObject, IDisposable
{
    readonly AppServices _services;
    readonly DictationService? _dictation;

    public DictationSettingsViewModel(AppServices services, DictationService? dictation)
    {
        _services = services;
        _dictation = dictation;
        Plain = new ShortcutSlotViewModel(() => P.DictationHotKey, v => Update(p => p.DictationHotKey = v), HotKey.DefaultPlain);
        Plus = new ShortcutSlotViewModel(() => P.ScribePlusHotKey, v => Update(p => p.ScribePlusHotKey = v), HotKey.DefaultPlus);
        services.PreferencesChanged += OnPreferencesChanged;
        if (dictation is not null) dictation.PropertyChanged += OnDictationChanged;
    }

    Preferences P => _services.Preferences;

    void Update(Action<Preferences> change) => _services.UpdatePreferences(change);

    void OnPreferencesChanged() => AppServices.Ui(() =>
    {
        OnPropertyChanged(string.Empty);
        Plain.Refresh();
        Plus.Refresh();
    });

    void OnDictationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DictationService.PlainError) or nameof(DictationService.PlusError)) OnPropertyChanged(nameof(ShortcutError));
        if (e.PropertyName == nameof(DictationService.ClaudeReady)) OnPropertyChanged(nameof(ClaudeReady));
    }

    // ---- Section 1 ----

    /// *Push-to-talk dictation* (`dictationEnabled`, default on).
    public bool Enabled { get => P.DictationEnabled; set => Update(p => p.DictationEnabled = value); }

    /// *Word for word* shortcut (`dictationHotKey`).
    public ShortcutSlotViewModel Plain { get; }
    /// *Scribe+* shortcut (`scribePlusHotKey`).
    public ShortcutSlotViewModel Plus { get; }

    /// Registration error in red below the shortcuts, or null.
    public string? ShortcutError
    {
        get
        {
            var errors = new[] { _dictation?.PlainError, _dictation?.PlusError }.Where(e => e is not null).Distinct().ToList();
            return errors.Count == 0 ? null : string.Join("\n", errors);
        }
    }

    public const string Help =
        "Hold a shortcut in any app and speak. When you let go, the text is transcribed on this PC and pasted where your cursor is. " +
        "Scribe+ also has Claude turn it into clean written text: filler words and false starts go, self-corrections are applied, punctuation is fixed.";

    public const string ShortcutHelp =
        "Use a key combination (e.g. Ctrl+Alt+Space) or modifier keys only, such as Right Ctrl or Right Ctrl + Right Shift. " +
        "If the Scribe+ shortcut contains the other one, adding its extra key while you dictate switches to Scribe+. " +
        "With modifier-only shortcuts, pressing any other key while holding them cancels the dictation, so normal shortcuts keep working.";

    // ---- Scribe+ ----

    /// *Claude Code*: `Ready` when Assist's Claude check succeeded.
    public bool ClaudeReady => _dictation?.ClaudeReady == true;

    /// *Model* options (`scribePlusModel`, default `sonnet`).
    public IReadOnlyList<Choice> Models { get; } = new[]
    {
        new Choice("sonnet", "Sonnet – careful, 2–4 s"),
        new Choice("haiku", "Haiku – fastest, about 1 s"),
    };

    public string Model { get => P.ScribePlusModel; set { if (value is not null) Update(p => p.ScribePlusModel = value); } }

    /// *Use the text around the cursor as context* (`scribePlusContext`, default on).
    public bool UseContext { get => P.ScribePlusContext; set => Update(p => p.ScribePlusContext = value); }

    /// *Keep Claude ready* (`scribePlusWarm`, default on).
    public bool KeepWarm { get => P.ScribePlusWarm; set => Update(p => p.ScribePlusWarm = value); }

    public const string PlusCaption =
        "Scribe+ runs Claude through your Claude subscription. The transcript goes to Anthropic, and with this option also up to 1,200 characters before " +
        "and 300 after the cursor (never from password fields) and the app’s name, so the text fits the conversation, e.g. “du” or “Sie”. " +
        "If Claude fails, the word-for-word text is pasted.";

    // ---- Pasting ----

    /// *Fit spaces and capitals to the text around the cursor* (`fitToCursor`, default on).
    public bool Fit { get => P.FitToCursor; set => Update(p => p.FitToCursor = value); }

    /// *Mute other sound while dictating* (`muteWhileDictating`, default on).
    public bool Mute { get => P.MuteWhileDictating; set => Update(p => p.MuteWhileDictating = value); }

    public const string PastingCaption =
        "The text stays on the clipboard after pasting. If you click into another field before it arrives, it is only copied, not pasted.";

    // ---- Permissions (Windows: microphone privacy and elevated windows, spec 04 §3.5 Windows notes) ----

    /// Desktop apps may not use the microphone (Settings › Privacy & security › Microphone).
    public bool MicrophoneBlocked => MicrophonePrivacy.Check() == MicrophoneAccess.Denied;

    [RelayCommand] static void OpenMicrophoneSettings() => MicrophonePrivacy.OpenSettings();

    public const string PermissionCaption =
        "Dictation uses the microphone chosen for recordings. Windows does not let the app paste into windows that run as administrator " +
        "unless MeetingAssistant runs as administrator too; there the text is only copied.";

    public const string Footer =
        "Uses Parakeet Ultra, which detects German, English and 23 other languages automatically. The microphone is the one chosen for recordings. " +
        "If a transcription fails, the recording is kept for 24 hours and can be transcribed again from the tray menu.";

    public void Dispose()
    {
        Plain.Stop();
        Plus.Stop();
        _services.PreferencesChanged -= OnPreferencesChanged;
        if (_dictation is not null) _dictation.PropertyChanged -= OnDictationChanged;
    }
}

/// Settings › Vocabulary (spec 04 §6.3): fuzzy toggle, your terms, replacements, names from Aya.
public sealed partial class VocabularySettingsViewModel : ObservableObject, IDisposable
{
    readonly AppServices _services;
    readonly DictationService? _dictation;

    public VocabularySettingsViewModel(AppServices services, DictationService? dictation)
    {
        _services = services;
        _dictation = dictation;
        Reload();
        services.VocabularyChanged += OnVocabularyChanged;
        if (dictation is not null) dictation.PropertyChanged += OnDictationChanged;
    }

    Vocabulary V => _services.Vocabulary;

    void OnVocabularyChanged() => AppServices.Ui(Reload);

    void OnDictationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DictationService.RefreshingAyaNames) or nameof(DictationService.AyaConnected))
        {
            OnPropertyChanged(nameof(RefreshText));
            RefreshAyaCommand.NotifyCanExecuteChanged();
        }
    }

    void Reload()
    {
        var v = V;
        Terms.Clear();
        foreach (var t in v.Terms) Terms.Add(t);
        Replacements.Clear();
        foreach (var r in v.Replacements) Replacements.Add(r);
        OnPropertyChanged(nameof(Fuzzy));
        OnPropertyChanged(nameof(UseAya));
        OnPropertyChanged(nameof(AyaNamesText));
    }

    public const string Intro =
        "Speech recognition writes what it hears, and names come out wrong most. Terms here get your spelling in dictations and in transcripts: " +
        "the same letters (“hubspot”, “Hub Spot” → “HubSpot”) and, if switched on, words that sound very much alike (“Clawd” → “Claude”). " +
        "Ordinary German or English words are never changed into a name.";

    /// *Also correct words that sound alike* (`fuzzy`, default on).
    public bool Fuzzy { get => V.Fuzzy; set => _services.UpdateVocabulary(v => v.Fuzzy = value); }

    // ---- Your terms ----

    public ObservableCollection<string> Terms { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddTermCommand))]
    string _newTerm = "";

    bool CanAddTerm => NewTerm.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanAddTerm))]
    void AddTerm()
    {
        var t = NewTerm.Trim();
        if (t.Length == 0 || V.Terms.Contains(t)) return;
        _services.UpdateVocabulary(v => v.AddTerm(t));
        NewTerm = "";
    }

    [RelayCommand]
    void RemoveTerm(string term) => _services.UpdateVocabulary(v => v.Terms.RemoveAll(x => x == term));

    // ---- Replacements ----

    public ObservableCollection<Vocabulary.Replacement> Replacements { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddReplacementCommand))]
    string _newSpoken = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddReplacementCommand))]
    string _newWritten = "";

    bool CanAddReplacement => NewSpoken.Trim().Length > 0 && NewWritten.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanAddReplacement))]
    void AddReplacement()
    {
        string a = NewSpoken.Trim(), b = NewWritten.Trim();
        if (a.Length == 0 || b.Length == 0) return;
        _services.UpdateVocabulary(v => v.AddReplacement(a, b));
        NewSpoken = "";
        NewWritten = "";
    }

    [RelayCommand]
    void RemoveReplacement(Vocabulary.Replacement r) => _services.UpdateVocabulary(v => v.Replacements.RemoveAll(x => x == r));

    public const string ReplacementsCaption = "Always applied, whole words only; case, spaces and hyphens do not matter.";

    // ---- From Aya ----

    /// *Use names from Aya* (`useAya`, default on).
    public bool UseAya { get => V.UseAya; set => _services.UpdateVocabulary(v => v.UseAya = value); }

    /// *Names*: the count, or `None loaded`.
    public string AyaNamesText => V.AyaTerms.Count == 0 ? "None loaded" : V.AyaTerms.Count.ToString(System.Globalization.CultureInfo.CurrentCulture);

    public string RefreshText => _dictation?.RefreshingAyaNames == true ? "Loading…" : "Refresh";

    bool CanRefreshAya => _dictation is { AyaConnected: true, RefreshingAyaNames: false };

    [RelayCommand(CanExecute = nameof(CanRefreshAya))]
    Task RefreshAya() => _dictation?.RefreshAyaNamesAsync(force: true) ?? Task.CompletedTask;

    public const string AyaCaption =
        "Companies (without the legal form), contacts and team members with their last names, and short project names. " +
        "Refreshed with the Aya lists. Stored only on this PC.";

    public void Dispose()
    {
        _services.VocabularyChanged -= OnVocabularyChanged;
        if (_dictation is not null) _dictation.PropertyChanged -= OnDictationChanged;
    }
}
