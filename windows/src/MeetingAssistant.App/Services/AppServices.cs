using System.Windows;
using MeetingAssistant.Core.Model;
using MeetingAssistant.Core.Pipeline;
using MeetingAssistant.Core.Platform;
using MeetingAssistant.Core.Storage;
using MeetingAssistant.Core.Text;
using MeetingAssistant.Platform.Windows.SystemServices;
using MeetingAssistant.Speech;

namespace MeetingAssistant.App.Services;

/// <summary>
/// The app's shared services, created once at start-up by <c>App.ConfigureServices</c> (App.Scribe.cs)
/// and used by all three feature areas (Scribe, Assist, Dictation):
/// preferences (`preferences.v1`), the credential store, the vocabulary and its matcher, the
/// transcript cleanup, the transcription queue and the speaker library.
/// </summary>
public sealed class AppServices
{
    readonly object _lock = new();
    Preferences _preferences;
    Vocabulary _vocabulary;
    VocabularyMatcher _matcher;

    /// The running app's services (set before any window opens).
    public static AppServices Shared { get; private set; } = null!;

    /// Creates the services and makes them <see cref="Shared"/>. Also installs the Windows spell
    /// checker (vocabulary "known word", spec 04 §5.3) and the Media Foundation import decoder (spec 01 "Import").
    public static AppServices Create(SettingsStore? store = null)
    {
        SpellCheck.Checker = WindowsSpellChecker.Create();
        SessionImport.Decoder = new MediaFoundationDecoder();
        Shared = new AppServices(store ?? SettingsStore.Shared);
        return Shared;
    }

    AppServices(SettingsStore store)
    {
        Settings = store;
        _preferences = Preferences.Load(store);
        _vocabulary = Vocabulary.Load();
        _matcher = _vocabulary.Matcher(SpellCheck.IsKnown);
        Cleanup = new TranscriptTextCleanup(InventedText.IsCredit, words => Matcher.Apply(words));
        Queue = new TranscriptionQueue(() => PipelineSettings, () => Cleanup);
    }

    // ---- Preferences (spec 01 "Preferences relevant to Scribe", 04 §6) ----

    /// The single preferences file (`preferences.json`) with all Mac defaults keys.
    public SettingsStore Settings { get; }

    /// Current preferences. Treat as read-only; change them with <see cref="UpdatePreferences"/>.
    public Preferences Preferences { get { lock (_lock) return _preferences; } }

    /// Changes, saves and publishes the preferences (raises <see cref="PreferencesChanged"/> on the caller's thread).
    public void UpdatePreferences(Action<Preferences> change)
    {
        Preferences next;
        lock (_lock)
        {
            next = _preferences.Clone();
            change(next);
            next.Save(Settings);
            _preferences = next;
        }
        PreferencesChanged?.Invoke();
    }

    /// Raised after <see cref="UpdatePreferences"/>.
    public event Action? PreferencesChanged;

    /// Settings of the next offline transcription (engine, language, my name).
    public PipelineSettings PipelineSettings
    {
        get
        {
            var p = Preferences;
            return new PipelineSettings
            {
                Engine = AsrEngineText.ParseEngine(p.Engine),
                Language = AsrEngineText.ParseLanguage(p.Language) ?? TranscriptLanguage.Auto,
                MyName = p.EffectiveName,
            };
        }
    }

    // ---- Secrets (spec 02 "API key storage") ----

    /// Windows Credential Manager (`MeetingAssistant/<account>`).
    public ISecretStore Secrets { get; } = new CredentialStore();

    // ---- Vocabulary (spec 04 §5) ----

    /// The current vocabulary (a copy; change it with <see cref="UpdateVocabulary"/>).
    public Vocabulary Vocabulary { get { lock (_lock) return _vocabulary.Clone(); } }

    /// Matcher for the active vocabulary, rebuilt whenever it changes.
    public VocabularyMatcher Matcher { get { lock (_lock) return _matcher; } }

    /// Changes and saves `vocabulary.json`, rebuilds the matcher and raises <see cref="VocabularyChanged"/>.
    public void UpdateVocabulary(Action<Vocabulary> change)
    {
        lock (_lock)
        {
            var next = _vocabulary.Clone();
            change(next);
            next.Save();
            _vocabulary = next;
            _matcher = next.Matcher(SpellCheck.IsKnown);
        }
        VocabularyChanged?.Invoke();
    }

    /// Re-reads `vocabulary.json` (e.g. after msctl changed it) and rebuilds the matcher.
    public void ReloadVocabulary()
    {
        lock (_lock)
        {
            _vocabulary = Vocabulary.Load();
            _matcher = _vocabulary.Matcher(SpellCheck.IsKnown);
        }
        VocabularyChanged?.Invoke();
    }

    /// Raised after the vocabulary or its matcher changed.
    public event Action? VocabularyChanged;

    // ---- Transcription (spec 01 "Transcription queue and session statuses") ----

    /// Credits removal + vocabulary for final transcripts.
    public ITranscriptTextCleanup Cleanup { get; }

    /// The single serial transcription queue. Its events arrive on a background thread.
    public TranscriptionQueue Queue { get; }

    // ---- Speakers (spec 01 "Speaker library", "Voiceprint") ----

    /// Serialises read-modify-write of `speaker-library.json` between the UI and the pipeline.
    public object SpeakerLibraryLock { get; } = new();

    /// Loads the library, applies a change and saves it.
    public void UpdateSpeakerLibrary(Action<SpeakerLibrary> change)
    {
        lock (SpeakerLibraryLock)
        {
            var library = SpeakerLibraryStore.Load();
            change(library);
            library.Save();
        }
        SpeakerLibraryChanged?.Invoke();
    }

    /// Raised after the speaker library or the voiceprint changed through the app.
    public event Action? SpeakerLibraryChanged;

    /// Raises <see cref="SpeakerLibraryChanged"/> (after enrollment or forgetting the voiceprint).
    public void NotifySpeakersChanged() => SpeakerLibraryChanged?.Invoke();

    // ---- Threading ----

    /// Runs `action` on the UI thread (directly when already there).
    public static void Ui(Action action)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) action();
        else d.BeginInvoke(action);
    }
}
