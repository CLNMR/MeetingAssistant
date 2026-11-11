using System.Diagnostics;
using Microsoft.Win32;

namespace MeetingAssistant.Platform.Windows.SystemServices;

/// Microphone privacy state (spec 04 §3.5 Windows notes).
public enum MicrophoneAccess { Granted, Denied, Unknown }

/// Settings › Privacy & security › Microphone (the Windows counterpart of the macOS microphone permission).
public static class MicrophonePrivacy
{
    const string Store = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    /// Message for a denied microphone (recording start; spec 01-recording "Recording lifecycle" step 1,
    /// worded for Windows).
    public const string DeniedMessage =
        "MeetingAssistant has no access to the microphone. Allow it in Settings › Privacy & security › Microphone, then try again.";

    /// Dictation toast (spec 04 §1.2).
    public const string DeniedToast = "No microphone access (Settings › Privacy)";

    /// Reads the consent store: "Microphone access" for the device (HKLM) and for the user (HKCU), and
    /// "Let desktop apps access your microphone" (HKCU …\NonPackaged). Any "Deny" → Denied. Unpackaged
    /// apps cannot request consent; the user must switch it on. WASAPI E_ACCESSDENIED is the final word.
    public static MicrophoneAccess Check()
    {
        var values = new[]
        {
            Read(Registry.LocalMachine, Store),
            Read(Registry.CurrentUser, Store),
            Read(Registry.CurrentUser, Store + @"\NonPackaged"),
        };
        if (values.Any(v => string.Equals(v, "Deny", StringComparison.OrdinalIgnoreCase))) return MicrophoneAccess.Denied;
        if (values.Any(v => string.Equals(v, "Allow", StringComparison.OrdinalIgnoreCase))) return MicrophoneAccess.Granted;
        return MicrophoneAccess.Unknown;
    }

    static string? Read(RegistryKey root, string path)
    {
        try
        {
            using var k = root.OpenSubKey(path);
            return k?.GetValue("Value") as string;
        }
        catch (UnauthorizedAccessException) { return null; }
        catch (System.Security.SecurityException) { return null; }
    }

    /// Opens the microphone privacy page of Settings ("Open System Settings" button).
    public static void OpenSettings() => Shell.Open("ms-settings:privacy-microphone");
}

/// Shell helpers.
public static class Shell
{
    /// Open a URI or file with its default handler.
    public static void Open(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();

    /// "Show in Explorer": opens Explorer with the item selected.
    public static void ShowInExplorer(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false })?.Dispose();
}

/// One running instance per user session: the second start signals the first (to show its window)
/// and exits.
public sealed class SingleInstance : IDisposable
{
    const string Name = "MeetingAssistant.SingleInstance";
    readonly Mutex _mutex;
    readonly EventWaitHandle _signal;
    RegisteredWaitHandle? _wait;

    /// True when this process is the first instance.
    public bool IsFirst { get; }

    /// Raised (on a thread-pool thread) in the first instance when another one starts.
    public event Action? Activated;

    public SingleInstance()
    {
        _mutex = new Mutex(true, @"Local\" + Name, out var created);
        IsFirst = created;
        _signal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\" + Name + ".Activate");
        if (IsFirst)
            _wait = ThreadPool.RegisterWaitForSingleObject(_signal, (_, _) => Activated?.Invoke(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// From a second instance: ask the first one to come to the front.
    public void SignalFirst() => _signal.Set();

    public void Dispose()
    {
        _wait?.Unregister(null);
        if (IsFirst) { try { _mutex.ReleaseMutex(); } catch (ApplicationException) { } }
        _mutex.Dispose();
        _signal.Dispose();
    }
}
