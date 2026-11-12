namespace MeetingAssistant.Core.Scribe;

/// Ghost mode of the floating live transcript (spec 01 "Floating live transcript window"): while the
/// cursor is over the window it fades and lets clicks and scrolling through to the app underneath;
/// holding Ctrl (⌘ on the Mac) makes it normal again. Checked about 10 times a second.
public static class GhostMode
{
    /// Opacity while the cursor is over the window in ghost mode.
    public const double FadedOpacity = 0.25;
    /// How often the cursor and the key are checked.
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(0.1);
    /// Duration of the fade.
    public static readonly TimeSpan Fade = TimeSpan.FromSeconds(0.15);
    /// UI-state key (like the font size).
    public const string Key = "floatingPanel.ghost";

    public readonly record struct Look(double Opacity, bool ClickThrough);

    /// Clicks pass through whenever ghost mode is on and the key is not held, also while the cursor is
    /// outside, so a click right after the cursor enters never lands on the window.
    public static Look Compute(bool enabled, bool cursorInside, bool keyHeld) =>
        !enabled || keyHeld ? new Look(1, false) : new Look(cursorInside ? FadedOpacity : 1, true);
}
