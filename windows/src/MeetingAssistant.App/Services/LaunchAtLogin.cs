using CommunityToolkit.Mvvm.ComponentModel;
using MeetingAssistant.Core.Storage;
using MeetingAssistant.Platform.Windows.SystemServices;

namespace MeetingAssistant.App.Services;

/// <summary>
/// Opens the app when the user signs in (spec 04 §3.5, §6.1). The registry's Run value is the source of
/// truth (<see cref="LoginItem"/>), so turning it off in Settings › Apps › Startup shows here; the
/// first-open question's flag is kept in the preferences file.
/// </summary>
public sealed partial class LaunchAtLogin : ObservableObject
{
    public static LaunchAtLogin Shared { get; } = new();

    LaunchAtLogin() => Refresh();

    [ObservableProperty] bool _isEnabled;
    /// Registered, but switched off by the user in Settings › Apps › Startup.
    [ObservableProperty] bool _needsApproval;
    [ObservableProperty] string? _error;

    /// The toggle can be used (the app runs from its exe).
    public bool IsAvailable => LoginItem.IsAvailable;

    /// The toggle shows on when registered (also when Windows needs the user to allow it).
    public bool IsOn
    {
        get => IsEnabled || NeedsApproval;
        set => Set(value);
    }

    public void Refresh()
    {
        var registered = LoginItem.IsRegistered;
        NeedsApproval = registered && LoginItem.IsDisabledByUser;
        IsEnabled = registered && !NeedsApproval;
        OnPropertyChanged(nameof(IsOn));
    }

    public void Set(bool on)
    {
        Error = null;
        try { LoginItem.Set(on); }
        catch (Exception e)
        {
            Error = $"Could not {(on ? "turn on" : "turn off")} opening at startup: {e.Message}";
        }
        Refresh();
    }

    /// Ask on the first open of the installed app; not in the test modes.
    public static bool ShouldAsk(string[] args) =>
        LoginItem.IsAvailable && SettingsStore.Shared.GetBool(SettingsStore.LaunchAtLoginAskedKey) != true
        && Array.IndexOf(args, "-dictationTest") < 0;

    /// Answer to the first-open question (either answer sets the flag).
    public void Answer(bool on)
    {
        SettingsStore.Shared.SetBool(SettingsStore.LaunchAtLoginAskedKey, true);
        if (on) Set(true);
    }

    /// "Open Startup Apps".
    public void OpenSettings() => LoginItem.OpenSettings();

    public const string Question = "Open MeetingAssistant at startup?";
    public const string QuestionMessage =
        "Dictation, the tray menu and recording shortcuts are then ready right after you start your computer. You can change this later in Settings › General.";
    public const string ApprovalCaption = "Allow MeetingAssistant in Settings › Apps › Startup.";
}
