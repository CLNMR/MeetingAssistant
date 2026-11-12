import Foundation

/// Ghost mode of the floating live transcript: while the cursor is over the window it fades and lets
/// clicks and scrolling through to the app underneath; holding the interact key (⌘ on the Mac, Ctrl on
/// Windows) makes it normal again. Checked about 10 times a second.
public enum GhostMode {
    /// Opacity while the cursor is over the window in ghost mode.
    public static let fadedOpacity = 0.25
    /// How often the cursor and the key are checked.
    public static let pollInterval = 0.1
    /// Duration of the fade.
    public static let fadeSeconds = 0.15
    /// Preference key (UI state, like the font size).
    public static let key = "floatingPanel.ghost"

    public struct Look: Equatable, Sendable {
        public var opacity: Double
        public var clickThrough: Bool
        public init(opacity: Double, clickThrough: Bool) { self.opacity = opacity; self.clickThrough = clickThrough }
    }

    /// Clicks pass through whenever ghost mode is on and the key is not held, also while the cursor is
    /// outside, so a click right after the cursor enters never lands on the window.
    public static func look(enabled: Bool, cursorInside: Bool, keyHeld: Bool) -> Look {
        guard enabled, !keyHeld else { return Look(opacity: 1, clickThrough: false) }
        return Look(opacity: cursorInside ? fadedOpacity : 1, clickThrough: true)
    }
}
