import AppKit
import ApplicationServices
import Carbon.HIToolbox
import CoreAudio

// MARK: - Clipboard

/// Pastes text through the clipboard. The text stays on the clipboard afterwards, so it can be
/// pasted again.
@MainActor
enum ClipboardPaster {
    static func paste(_ text: String, sendPaste: () -> Void) {
        copy(text)
        sendPaste()
    }

    static func copy(_ text: String) {
        let pb = NSPasteboard.general
        pb.clearContents()
        pb.setString(text, forType: .string)
    }

    static func sendCommandV() {
        let src = CGEventSource(stateID: .combinedSessionState)
        let v = CGKeyCode(kVK_ANSI_V)
        for down in [true, false] {
            let e = CGEvent(keyboardEventSource: src, virtualKey: v, keyDown: down)
            e?.flags = .maskCommand
            e?.post(tap: .cghidEventTap)
        }
    }
}

// MARK: - Focused text field

/// What the app can see of the field the dictation goes into, via the Accessibility API it
/// already needs for pasting. Password fields are never read; every call gives up quickly.
struct FocusedField {
    var app: String?
    var pid: pid_t
    var element: AXUIElement?
    var role: String?
    var position: CGPoint?
    var before: String?
    var after: String?

    static let beforeLimit = 2_000
    static let afterLimit = 600
    private static var enabledPIDs = Set<pid_t>()

    /// The focused element of the frontmost app; with `readText`, also the text around the cursor.
    @MainActor
    static func current(readText: Bool) -> FocusedField? {
        guard AXIsProcessTrusted(), let app = NSWorkspace.shared.frontmostApplication else { return nil }
        let pid = app.processIdentifier
        let appEl = AXUIElementCreateApplication(pid)
        AXUIElementSetMessagingTimeout(appEl, 0.25)
        // Chromium and Electron apps build their accessibility tree only on request.
        if !enabledPIDs.contains(pid) {
            AXUIElementSetAttributeValue(appEl, "AXManualAccessibility" as CFString, kCFBooleanTrue)
            enabledPIDs.insert(pid)
        }
        var f = FocusedField(app: app.localizedName, pid: pid)
        var ref: CFTypeRef?
        guard AXUIElementCopyAttributeValue(appEl, kAXFocusedUIElementAttribute as CFString, &ref) == .success,
              let el = ref, CFGetTypeID(el) == AXUIElementGetTypeID() else { return f }
        let element = el as! AXUIElement
        AXUIElementSetMessagingTimeout(element, 0.25)
        f.element = element
        f.role = string(element, kAXRoleAttribute)
        f.position = point(element)
        guard readText else { return f }
        let subrole = string(element, kAXSubroleAttribute)
        if subrole == (kAXSecureTextFieldSubrole as String) { return f }
        guard let value = string(element, kAXValueAttribute), let range = selectedRange(element) else { return f }
        let ns = value as NSString
        let loc = min(max(0, range.location), ns.length)
        let end = min(ns.length, loc + max(0, range.length))
        f.before = String(ns.substring(to: loc).suffix(beforeLimit))
        f.after = String(ns.substring(from: end).prefix(afterLimit))
        return f
    }

    /// The same field as `other`: same app, and the same element (or same role at the same place,
    /// when the app rebuilt its accessibility objects).
    func isSameField(as other: FocusedField) -> Bool {
        guard pid == other.pid else { return false }
        // Some apps do not report a focused element; then only the app can be compared.
        guard let a = element, let b = other.element else { return true }
        if CFEqual(a, b) { return true }
        return role == other.role && position == other.position
    }

    private static func string(_ el: AXUIElement, _ attr: String) -> String? {
        var v: CFTypeRef?
        guard AXUIElementCopyAttributeValue(el, attr as CFString, &v) == .success else { return nil }
        return v as? String
    }

    private static func selectedRange(_ el: AXUIElement) -> CFRange? {
        var v: CFTypeRef?
        guard AXUIElementCopyAttributeValue(el, kAXSelectedTextRangeAttribute as CFString, &v) == .success, let v,
              CFGetTypeID(v) == AXValueGetTypeID() else { return nil }
        var r = CFRange()
        return AXValueGetValue(v as! AXValue, .cfRange, &r) ? r : nil
    }

    private static func point(_ el: AXUIElement) -> CGPoint? {
        var v: CFTypeRef?
        guard AXUIElementCopyAttributeValue(el, kAXPositionAttribute as CFString, &v) == .success, let v,
              CFGetTypeID(v) == AXValueGetTypeID() else { return nil }
        var p = CGPoint.zero
        return AXValueGetValue(v as! AXValue, .cgPoint, &p) ? p : nil
    }
}

// MARK: - Muting the sound output

/// Silences the default output device while dictating, so music or a podcast does not run on,
/// and restores it exactly as it was. Muting instead of pausing: apps cannot see what is playing,
/// so "resume" could start something that had been paused on purpose.
final class OutputMuter {
    private var device: AudioDeviceID?
    private var savedVolume: Float32?
    private var mutedBySwitch = false

    func mute() {
        guard device == nil, let dev = Self.defaultOutput() else { return }
        if Self.isMuted(dev) == true { return }                        // already silent
        if Self.setMuted(dev, true) {
            device = dev; mutedBySwitch = true
        } else if let v = Self.volume(dev), v > 0, Self.setVolume(dev, 0) {
            device = dev; savedVolume = v
        }
    }

    func restore() {
        guard let dev = device else { return }
        if mutedBySwitch { _ = Self.setMuted(dev, false) }
        if let v = savedVolume, Self.volume(dev) == 0 { _ = Self.setVolume(dev, v) }
        device = nil; savedVolume = nil; mutedBySwitch = false
    }

    private static func address(_ sel: AudioObjectPropertySelector, output: Bool = true) -> AudioObjectPropertyAddress {
        AudioObjectPropertyAddress(mSelector: sel, mScope: output ? kAudioDevicePropertyScopeOutput : kAudioObjectPropertyScopeGlobal,
                                   mElement: kAudioObjectPropertyElementMain)
    }

    private static func defaultOutput() -> AudioDeviceID? {
        var a = address(kAudioHardwarePropertyDefaultOutputDevice, output: false)
        var dev = AudioDeviceID(0)
        var size = UInt32(MemoryLayout<AudioDeviceID>.size)
        guard AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &a, 0, nil, &size, &dev) == noErr, dev != 0 else { return nil }
        return dev
    }

    private static func isMuted(_ dev: AudioDeviceID) -> Bool? {
        var a = address(kAudioDevicePropertyMute)
        guard AudioObjectHasProperty(dev, &a) else { return nil }
        var v: UInt32 = 0
        var size = UInt32(MemoryLayout<UInt32>.size)
        guard AudioObjectGetPropertyData(dev, &a, 0, nil, &size, &v) == noErr else { return nil }
        return v != 0
    }

    private static func setMuted(_ dev: AudioDeviceID, _ on: Bool) -> Bool {
        var a = address(kAudioDevicePropertyMute)
        var settable: DarwinBoolean = false
        guard AudioObjectHasProperty(dev, &a), AudioObjectIsPropertySettable(dev, &a, &settable) == noErr, settable.boolValue else { return false }
        var v: UInt32 = on ? 1 : 0
        return AudioObjectSetPropertyData(dev, &a, 0, nil, UInt32(MemoryLayout<UInt32>.size), &v) == noErr
    }

    private static func volume(_ dev: AudioDeviceID) -> Float32? {
        var a = address(kAudioDevicePropertyVolumeScalar)
        guard AudioObjectHasProperty(dev, &a) else { return nil }
        var v: Float32 = 0
        var size = UInt32(MemoryLayout<Float32>.size)
        guard AudioObjectGetPropertyData(dev, &a, 0, nil, &size, &v) == noErr else { return nil }
        return v
    }

    private static func setVolume(_ dev: AudioDeviceID, _ v: Float32) -> Bool {
        var a = address(kAudioDevicePropertyVolumeScalar)
        var settable: DarwinBoolean = false
        guard AudioObjectHasProperty(dev, &a), AudioObjectIsPropertySettable(dev, &a, &settable) == noErr, settable.boolValue else { return false }
        var x = v
        return AudioObjectSetPropertyData(dev, &a, 0, nil, UInt32(MemoryLayout<Float32>.size), &x) == noErr
    }
}
