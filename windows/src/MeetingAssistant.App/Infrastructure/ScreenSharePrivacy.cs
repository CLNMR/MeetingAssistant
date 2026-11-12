using System.Windows;
using MeetingAssistant.App.Services;

namespace MeetingAssistant.App.Infrastructure;

/// Keeps the overlay windows (floating live transcript, dictation HUD) out of screen sharing when the
/// preference "hideFromScreenSharing" is on (spec 04 §6.1), and follows changes of the preference.
public static class ScreenSharePrivacy
{
    static bool Hidden => AppServices.Shared?.Preferences.HideFromScreenSharing ?? true;

    /// Call from the window's constructor: applies the preference once the window has a handle and
    /// re-applies it whenever the preferences change, until the window closes.
    public static void Register(Window w)
    {
        Action changed = () => AppServices.Ui(() => Native.SetExcludedFromCapture(w, Hidden));
        w.SourceInitialized += (_, _) =>
        {
            Native.SetExcludedFromCapture(w, Hidden);
            if (AppServices.Shared is { } s) s.PreferencesChanged += changed;
        };
        w.Closed += (_, _) => { if (AppServices.Shared is { } s) s.PreferencesChanged -= changed; };
    }
}
