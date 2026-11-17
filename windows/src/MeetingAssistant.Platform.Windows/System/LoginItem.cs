using Microsoft.Win32;

namespace MeetingAssistant.Platform.Windows.SystemServices;

/// <summary>
/// Open at login (spec 04 §3.5, §6.1 Windows notes): a value <c>MeetingAssistant</c> under
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> with the quoted exe path. The registry is the
/// source of truth, so a change in Settings › Apps › Startup or Task Manager shows in the app: a value the
/// user switched off there (<c>…\Explorer\StartupApproved\Run</c>, first byte odd) counts as "needs approval",
/// the counterpart of macOS's login item that requires approval.
/// </summary>
public static class LoginItem
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    public const string ValueName = "MeetingAssistant";

    /// The exe this process runs from, when it is the app itself (not `dotnet` running a dll).
    public static string? AppPath
    {
        get
        {
            var p = Environment.ProcessPath;
            if (string.IsNullOrEmpty(p) || !p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
            return string.Equals(Path.GetFileNameWithoutExtension(p), "dotnet", StringComparison.OrdinalIgnoreCase) ? null : p;
        }
    }

    /// Only a real app exe can register itself.
    public static bool IsAvailable => OperatingSystem.IsWindows() && AppPath is not null;

    /// The Run value exists.
    public static bool IsRegistered
    {
        get
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(RunKey);
                return k?.GetValue(ValueName) is string s && s.Length > 0;
            }
            catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException) { return false; }
        }
    }

    /// The value exists but the user switched it off in Settings › Apps › Startup (or Task Manager).
    public static bool IsDisabledByUser
    {
        get
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(ApprovedKey);
                return IsRegistered && k?.GetValue(ValueName) is byte[] { Length: > 0 } b && (b[0] & 1) == 1;
            }
            catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException) { return false; }
        }
    }

    /// Writes or removes the Run value. Throws with the reason when the registry refuses.
    public static void Set(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
                      ?? throw new InvalidOperationException("The Run key could not be opened.");
        if (on)
        {
            var exe = AppPath ?? throw new InvalidOperationException("MeetingAssistant is not running from its installed program file.");
            k.SetValue(ValueName, $"\"{exe}\"", RegistryValueKind.String);
        }
        else k.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// Opens Settings › Apps › Startup.
    public static void OpenSettings() => Shell.Open("ms-settings:startupapps");
}
