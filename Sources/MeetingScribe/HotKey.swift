import AppKit
import Carbon.HIToolbox
import SwiftUI

/// A user-chosen global shortcut: either a key with modifiers (⌃⌥Space) or a single
/// modifier key held on its own (right ⌥, fn).
struct HotKey: Codable, Equatable {
    var keyCode: UInt16
    var modifiers: UInt          // NSEvent.ModifierFlags raw value (⌘⌥⌃⇧ only)
    var modifierOnly: Bool
    /// Modifier-only shortcuts made of several keys (e.g. fn + left ⌃). nil = just `keyCode`.
    var modifierKeys: [UInt16]? = nil

    static let rightOption = HotKey(keyCode: UInt16(kVK_RightOption), modifiers: 0, modifierOnly: true)

    static let allModifierKeys: [UInt16] = [kVK_Function, kVK_Control, kVK_RightControl, kVK_Option, kVK_RightOption,
                                            kVK_Shift, kVK_RightShift, kVK_Command, kVK_RightCommand].map { UInt16($0) }

    /// Modifier-only shortcut from the keys that were held together.
    static func modifiers(_ keys: Set<UInt16>) -> HotKey {
        let ordered = allModifierKeys.filter(keys.contains)
        return HotKey(keyCode: ordered[0], modifiers: 0, modifierOnly: true, modifierKeys: ordered.count > 1 ? ordered : nil)
    }

    var heldKeys: [UInt16] { modifierKeys ?? [keyCode] }

    /// Every device mask that must be down for a modifier-only shortcut.
    var requiredMask: UInt { heldKeys.compactMap(Self.deviceMask(for:)).reduce(0, |) }

    var flags: NSEvent.ModifierFlags { NSEvent.ModifierFlags(rawValue: modifiers) }

    /// Device-dependent bit (left/right specific) of a modifier key in NSEvent.modifierFlags.
    static func deviceMask(for keyCode: UInt16) -> UInt? {
        switch Int(keyCode) {
        case kVK_Control: return 0x0001
        case kVK_Shift: return 0x0002
        case kVK_RightShift: return 0x0004
        case kVK_Command: return 0x0008
        case kVK_RightCommand: return 0x0010
        case kVK_Option: return 0x0020
        case kVK_RightOption: return 0x0040
        case kVK_RightControl: return 0x2000
        case kVK_Function: return NSEvent.ModifierFlags.function.rawValue
        default: return nil
        }
    }

    var display: String {
        if modifierOnly {
            if heldKeys.count > 1 { return heldKeys.map(Self.shortModifierName).joined(separator: " + ") }
            switch Int(keyCode) {
            case kVK_Control: return "Left ⌃ Control"
            case kVK_RightControl: return "Right ⌃ Control"
            case kVK_Shift: return "Left ⇧ Shift"
            case kVK_RightShift: return "Right ⇧ Shift"
            case kVK_Command: return "Left ⌘ Command"
            case kVK_RightCommand: return "Right ⌘ Command"
            case kVK_Option: return "Left ⌥ Option"
            case kVK_RightOption: return "Right ⌥ Option"
            case kVK_Function: return "fn / 🌐"
            default: return "Key \(keyCode)"
            }
        }
        var s = ""
        if flags.contains(.control) { s += "⌃" }
        if flags.contains(.option) { s += "⌥" }
        if flags.contains(.shift) { s += "⇧" }
        if flags.contains(.command) { s += "⌘" }
        return s + Self.keyName(keyCode)
    }

    static func shortModifierName(_ code: UInt16) -> String {
        switch Int(code) {
        case kVK_Control: return "Left ⌃"
        case kVK_RightControl: return "Right ⌃"
        case kVK_Shift: return "Left ⇧"
        case kVK_RightShift: return "Right ⇧"
        case kVK_Command: return "Left ⌘"
        case kVK_RightCommand: return "Right ⌘"
        case kVK_Option: return "Left ⌥"
        case kVK_RightOption: return "Right ⌥"
        case kVK_Function: return "fn"
        default: return "Key \(code)"
        }
    }

    static func isFunctionKey(_ code: UInt16) -> Bool {
        [kVK_F1, kVK_F2, kVK_F3, kVK_F4, kVK_F5, kVK_F6, kVK_F7, kVK_F8, kVK_F9, kVK_F10, kVK_F11, kVK_F12,
         kVK_F13, kVK_F14, kVK_F15, kVK_F16, kVK_F17, kVK_F18, kVK_F19, kVK_F20].contains(Int(code))
    }

    static func keyName(_ code: UInt16) -> String {
        let named: [Int: String] = [
            kVK_Space: "Space", kVK_Return: "↩", kVK_Tab: "⇥", kVK_Delete: "⌫", kVK_ForwardDelete: "⌦",
            kVK_Escape: "⎋", kVK_LeftArrow: "←", kVK_RightArrow: "→", kVK_UpArrow: "↑", kVK_DownArrow: "↓",
            kVK_Home: "↖", kVK_End: "↘", kVK_PageUp: "⇞", kVK_PageDown: "⇟",
            kVK_F1: "F1", kVK_F2: "F2", kVK_F3: "F3", kVK_F4: "F4", kVK_F5: "F5", kVK_F6: "F6",
            kVK_F7: "F7", kVK_F8: "F8", kVK_F9: "F9", kVK_F10: "F10", kVK_F11: "F11", kVK_F12: "F12",
            kVK_F13: "F13", kVK_F14: "F14", kVK_F15: "F15", kVK_F16: "F16", kVK_F17: "F17", kVK_F18: "F18",
            kVK_F19: "F19", kVK_F20: "F20",
        ]
        if let n = named[Int(code)] { return n }
        // Character produced by the key on the current keyboard layout (QWERTZ etc.).
        guard let src = TISCopyCurrentKeyboardLayoutInputSource()?.takeRetainedValue(),
              let ptr = TISGetInputSourceProperty(src, kTISPropertyUnicodeKeyLayoutData) else { return "Key \(code)" }
        let data = Unmanaged<CFData>.fromOpaque(ptr).takeUnretainedValue() as Data
        var dead: UInt32 = 0
        var chars = [UniChar](repeating: 0, count: 4)
        var len = 0
        let st = data.withUnsafeBytes { raw -> OSStatus in
            UCKeyTranslate(raw.bindMemory(to: UCKeyboardLayout.self).baseAddress, code, UInt16(kUCKeyActionDisplay), 0,
                           UInt32(LMGetKbdType()), OptionBits(kUCKeyTranslateNoDeadKeysBit), &dead, 4, &len, &chars)
        }
        guard st == noErr, len > 0 else { return "Key \(code)" }
        return String(utf16CodeUnits: chars, count: len).uppercased()
    }
}

/// Delivers press / release of the global shortcut while any app is in front.
/// Key combos use a Carbon hot key (no permission needed). A lone modifier key needs
/// event monitors, which macOS only delivers when the app has Accessibility access.
@MainActor
final class GlobalHotKey {
    var onPress: () -> Void = {}
    var onRelease: () -> Void = {}
    /// Another key was typed while a lone modifier was held: the user meant a normal shortcut.
    var onCancel: () -> Void = {}

    private(set) var hotKey: HotKey?
    private var ref: EventHotKeyRef?
    private var handler: EventHandlerRef?
    private var monitors: [Any] = []
    private var isDown = false

    func register(_ key: HotKey?) {
        unregister()
        hotKey = key
        guard let key else { return }
        if key.modifierOnly { installModifierMonitors(key) } else { installCarbon(key) }
    }

    func unregister() {
        if let ref { UnregisterEventHotKey(ref) }
        if let handler { RemoveEventHandler(handler) }
        ref = nil; handler = nil
        for m in monitors { NSEvent.removeMonitor(m) }
        monitors = []
        if isDown { isDown = false; onCancel() }
    }

    /// Error text when the combination is already taken by another app.
    private(set) var registrationError: String?

    private func installCarbon(_ key: HotKey) {
        var specs = [EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed)),
                     EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyReleased))]
        let me = Unmanaged.passUnretained(self).toOpaque()
        InstallEventHandler(GetApplicationEventTarget(), { _, event, user in
            guard let event, let user else { return OSStatus(eventNotHandledErr) }
            let hk = Unmanaged<GlobalHotKey>.fromOpaque(user).takeUnretainedValue()
            let pressed = GetEventKind(event) == UInt32(kEventHotKeyPressed)
            MainActor.assumeIsolated { pressed ? hk.down() : hk.up() }
            return noErr
        }, 2, &specs, me, &handler)

        var mods: UInt32 = 0
        if key.flags.contains(.command) { mods |= UInt32(cmdKey) }
        if key.flags.contains(.option) { mods |= UInt32(optionKey) }
        if key.flags.contains(.control) { mods |= UInt32(controlKey) }
        if key.flags.contains(.shift) { mods |= UInt32(shiftKey) }
        let id = EventHotKeyID(signature: OSType(0x4D534354), id: 1)   // 'MSCT'
        let st = RegisterEventHotKey(UInt32(key.keyCode), mods, id, GetApplicationEventTarget(), 0, &ref)
        registrationError = st == noErr ? nil : "\(key.display) is already used by another app. Choose a different shortcut."
    }

    private func installModifierMonitors(_ key: HotKey) {
        registrationError = nil
        let mask = key.requiredMask
        guard mask != 0 else { return }
        let flagsChanged: (NSEvent) -> Void = { [weak self] e in
            guard let self else { return }
            let pressed = e.modifierFlags.rawValue & mask == mask
            pressed ? self.down() : self.up()
        }
        let keyDown: (NSEvent) -> Void = { [weak self] _ in
            guard let self, self.isDown else { return }
            self.isDown = false
            self.onCancel()
        }
        if let m = NSEvent.addGlobalMonitorForEvents(matching: .flagsChanged, handler: flagsChanged) { monitors.append(m) }
        if let m = NSEvent.addGlobalMonitorForEvents(matching: .keyDown, handler: keyDown) { monitors.append(m) }
        if let m = NSEvent.addLocalMonitorForEvents(matching: .flagsChanged, handler: { flagsChanged($0); return $0 }) { monitors.append(m) }
        if let m = NSEvent.addLocalMonitorForEvents(matching: .keyDown, handler: { keyDown($0); return $0 }) { monitors.append(m) }
    }

    private func down() {
        guard !isDown else { return }   // ignore key repeat
        isDown = true
        onPress()
    }

    private func up() {
        guard isDown else { return }
        isDown = false
        onRelease()
    }
}

/// Settings control: click, then press a key combination or tap a single modifier key.
struct ShortcutRecorder: View {
    @Binding var hotKey: HotKey?
    var onRecordingChange: (Bool) -> Void = { _ in }
    @State private var recording = false
    @State private var monitor: Any?
    @State private var gesture: Set<UInt16> = []

    var body: some View {
        HStack(spacing: 6) {
            Button { recording ? stop() : start() } label: {
                Text(recording ? "Press shortcut… (esc to cancel)" : (hotKey?.display ?? "Not set"))
                    .frame(minWidth: 150)
            }
            if hotKey != nil, !recording {
                Button { hotKey = nil } label: { Image(systemName: "xmark.circle.fill") }
                    .buttonStyle(.plain).foregroundStyle(.secondary).help("Remove shortcut")
            }
        }
        .onDisappear { stop() }
    }

    private func start() {
        recording = true
        gesture = []
        onRecordingChange(true)
        monitor = NSEvent.addLocalMonitorForEvents(matching: [.keyDown, .flagsChanged]) { e in
            if e.type == .keyDown {
                let mods = e.modifierFlags.intersection([.command, .option, .control, .shift])
                if e.keyCode == UInt16(kVK_Escape), mods.isEmpty { stop(); return nil }
                if mods.isEmpty, !HotKey.isFunctionKey(e.keyCode) { NSSound.beep(); return nil }
                hotKey = HotKey(keyCode: e.keyCode, modifiers: mods.rawValue, modifierOnly: false)
                stop()
                return nil
            }
            let raw = e.modifierFlags.rawValue
            let held = HotKey.allModifierKeys.filter { HotKey.deviceMask(for: $0).map { raw & $0 != 0 } ?? false }
            gesture.formUnion(held)
            if held.isEmpty, !gesture.isEmpty {
                hotKey = HotKey.modifiers(gesture)   // all keys released: save what was held together
                stop()
            }
            return nil
        }
    }

    private func stop() {
        if let monitor { NSEvent.removeMonitor(monitor) }
        monitor = nil
        if recording { recording = false; onRecordingChange(false) }
    }
}
