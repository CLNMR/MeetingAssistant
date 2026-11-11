using MeetingAssistant.Core.Input;
using MeetingAssistant.Core.Platform;

namespace MeetingAssistant.Core.Dictation;

/// The two dictation modes (spec 04 §1): word for word ("plain") and Scribe+ ("plus").
public enum DictationMode { Plain, Plus }

/// States of a dictation (spec 04 §1.1). `Message` is a transient toast in the HUD.
public enum DictationPhase { Idle, Listening, Transcribing, Message }

/// Microphone permission as the dictation sees it (spec 04 §1.2 step 2).
public enum MicrophonePermission { Granted, Denied, NotDetermined }

/// The microphone for dictations (spec 04 §1.2 steps 2–3): the device chosen for recordings.
/// Windows: WASAPI on `inputDeviceUID`; msctl: a file; tests: a fake.
public interface IDictationMicrophone
{
    /// Current permission. Windows has no prompt; a blocked privacy setting reads as Denied.
    MicrophonePermission Permission { get; }
    /// Asks for permission (asynchronously; the user tries again afterwards).
    void RequestPermission();
    /// A new, not yet started 16 kHz mono source. `Start()` throws
    /// <see cref="UnauthorizedAccessException"/> when access is denied, anything else when the
    /// device is unavailable.
    IAudioSource Create();
}

/// The focused field of the frontmost app (spec 04 §3.2). <see cref="Before"/>/<see cref="After"/>
/// are null when the text could not be read (then fit is skipped and no context is sent).
public sealed record DictationField(string? App, FieldIdentity Identity, string? Before = null, string? After = null);

/// Reads the focused field (spec 04 §3.2). Never throws; null when there is no focused app.
public interface IFocusedFieldSource
{
    Task<DictationField?> ReadAsync(bool readText);
}

/// Result of handing text to the paste machinery (spec 04 §3.1 step 4).
/// <param name="Pasted">Ctrl+V (⌘V) was sent.</param>
/// <param name="Toast">A message when the text was only copied or nothing worked (e.g. an elevated
/// window on Windows), else null.</param>
/// <param name="Delivered">The text reached the clipboard (so the recovery file may go).</param>
public sealed record PasteResult(bool Pasted, string? Toast = null, bool Delivered = true)
{
    public static PasteResult Ok { get; } = new(true);
}

/// Clipboard and paste (spec 04 §3.1).
public interface IDictationOutput
{
    /// Waits until the shortcut's modifier keys are up (20 ms polls, max 1.5 s), puts the text on the
    /// clipboard (not restored afterwards) and sends the paste keystroke.
    Task<PasteResult> PasteAsync(string text);
    /// Only copies. Returns false when the clipboard could not be opened.
    bool Copy(string text);
}

/// Silences other sound while dictating (spec 04 §1.6): mute, never pause.
public interface IOutputMuter
{
    void Mute();
    void Restore();
}

/// The dictation speech recognizer (spec 04 §1.5): Parakeet on the whole clip, "" for silence.
/// Runs off the caller's thread.
public interface IDictationRecognizer
{
    Task<string> DictateAsync(float[] samples, CancellationToken ct = default);
}

/// Wraps a synchronous dictate function (e.g. `SpeechEngine.Shared.Dictate`): runs it on the thread
/// pool, one call at a time (the live preview and the final transcription share one model).
public sealed class DelegateRecognizer(Func<float[], string> dictate) : IDictationRecognizer
{
    readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<string> DictateAsync(float[] samples, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await Task.Run(() => dictate(samples), ct).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
}

/// Time for the dictation controller, so its timings (200 ms, 0.3 s taps, 5 s hint, 8 s Scribe+
/// timeout, toasts, spare checks) can be tested without waiting.
public interface IDictationClock
{
    DateTime UtcNow { get; }
    Task Delay(TimeSpan delay, CancellationToken ct = default);
}

/// The real clock: `DateTime.UtcNow` and `Task.Delay`.
public sealed class SystemDictationClock : IDictationClock
{
    public static SystemDictationClock Instance { get; } = new();
    public DateTime UtcNow => DateTime.UtcNow;
    public Task Delay(TimeSpan delay, CancellationToken ct = default) => Task.Delay(delay, ct);
}

/// Kept dictation audio (spec 04 §1.8) in a folder (default `<AppData>/Dictations`).
public class DictationRecoveryStore(string? folder = null)
{
    public string Folder => folder ?? Text.DictationRecovery.Folder;
    public virtual string? Save(float[] samples, bool plus, DateTime at) => Text.DictationRecovery.Save(samples, plus, at, Folder);
    public virtual void Delete(string path) => Text.DictationRecovery.Delete(path);
    public virtual List<Text.DictationRecovery.Item> Current(DateTime now) => Text.DictationRecovery.Current(now, Folder);
    public virtual float[] Load(Text.DictationRecovery.Item item) => Text.DictationRecovery.Load(item);
}

/// A thread-safe, growing sample buffer: the capture thread appends, the controller reads.
public sealed class SampleBuffer
{
    readonly object _lock = new();
    readonly List<float> _samples = new();

    public void Append(float[] s) { lock (_lock) _samples.AddRange(s); }
    public int Count { get { lock (_lock) return _samples.Count; } }

    /// The last <paramref name="n"/> samples (or all when fewer).
    public float[] Tail(int n)
    {
        lock (_lock)
        {
            int k = Math.Min(n, _samples.Count);
            return _samples.GetRange(_samples.Count - k, k).ToArray();
        }
    }

    public float[] All() { lock (_lock) return _samples.ToArray(); }
}
