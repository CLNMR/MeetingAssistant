using System.IO;
using MeetingAssistant.Core.Dictation;
using MeetingAssistant.Core.Platform;
using MeetingAssistant.Platform.Windows.Audio;
using MeetingAssistant.Platform.Windows.Input;
using MeetingAssistant.Platform.Windows.SystemServices;

namespace MeetingAssistant.App.Dictation;

/// The microphone chosen for recordings (`inputDeviceUID`), via WASAPI (spec 04 §1.2, §3.5 Windows notes).
/// Windows has no permission prompt for desktop apps: a blocked privacy setting reads as Denied, and
/// WASAPI's E_ACCESSDENIED surfaces as <see cref="UnauthorizedAccessException"/>.
sealed class WindowsDictationMicrophone(Func<string?> deviceId) : IDictationMicrophone
{
    public MicrophonePermission Permission =>
        MicrophonePrivacy.Check() == MicrophoneAccess.Denied ? MicrophonePermission.Denied : MicrophonePermission.Granted;

    public void RequestPermission() => MicrophonePrivacy.OpenSettings();

    public IAudioSource Create() => new Source(new WasapiMicSource(deviceId()));

    /// Translates the WASAPI access error for the controller.
    sealed class Source(WasapiMicSource inner) : IAudioSource
    {
        public event Action<float[]>? Samples { add => inner.Samples += value; remove => inner.Samples -= value; }
        public event Action<string>? Failed { add => inner.Failed += value; remove => inner.Failed -= value; }

        public void Start()
        {
            try { inner.Start(); }
            catch (MicrophoneAccessDeniedException e) { throw new UnauthorizedAccessException(e.Message, e); }
        }

        public void Stop() => inner.Stop();
        public void Dispose() => inner.Dispose();
    }
}

/// The focused field via UI Automation (spec 04 §3.2). Password fields never give their text.
sealed class WindowsFocusedFields : IFocusedFieldSource
{
    public async Task<DictationField?> ReadAsync(bool readText)
    {
        var f = await FocusedFieldReader.Shared.ReadAsync(readText).ConfigureAwait(false);
        if (f is null) return null;
        return f.IsPassword ? new DictationField(f.App, f.Identity) : new DictationField(f.App, f.Identity, f.Before, f.After);
    }
}

/// Clipboard + Ctrl+V (spec 04 §3.1 Windows notes). The same-field check is done by the controller;
/// this only adds the Windows case of an elevated target window (UIPI blocks SendInput there).
sealed class WindowsDictationOutput : IDictationOutput
{
    public async Task<PasteResult> PasteAsync(string text)
    {
        var outcome = await PasteService.DeliverAsync(text, start: null);
        return outcome switch
        {
            DeliveryOutcome.Pasted => PasteResult.Ok,
            DeliveryOutcome.CopiedElevatedWindow => new PasteResult(false, PasteService.ElevatedToast),
            DeliveryOutcome.CopiedFieldChanged => new PasteResult(false, PasteService.FieldChangedToast),
            _ => new PasteResult(false, PasteService.ClipboardFailedToast, Delivered: false),
        };
    }

    public bool Copy(string text) => ClipboardText.Set(text);
}

/// Mutes the default output endpoint while dictating (spec 04 §1.6).
sealed class WindowsOutputMuter : IOutputMuter
{
    readonly OutputMuter _muter = new();
    public void Mute() => _muter.Mute();
    public void Restore() => _muter.Restore();
}

/// The dictation log (spec 04 §1.9 Windows notes): one line per dictation in
/// `%LOCALAPPDATA%\MeetingAssistant\Logs\dictation.log`, rotated at 1 MB (one old file kept).
static class DictationLog
{
    const long MaxBytes = 1_000_000;
    static readonly object Lock = new();

    public static string File => Path.Combine(Core.AppPaths.Root, "Logs", "dictation.log");

    public static void Write(string line)
    {
        System.Diagnostics.Debug.WriteLine(line);
        lock (Lock)
        {
            try
            {
                var path = File;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    var old = path + ".1";
                    if (System.IO.File.Exists(old)) System.IO.File.Delete(old);
                    System.IO.File.Move(path, old);
                }
                System.IO.File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}");
            }
            catch { /* logging must never break a dictation */ }
        }
    }
}
