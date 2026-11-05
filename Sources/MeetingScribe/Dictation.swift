import AppKit
import ApplicationServices
import Carbon.HIToolbox
import CoreAudio
import MeetingScribeCore
import SwiftUI

/// Push-to-talk dictation: hold the global shortcut, speak, release. The text is copied
/// to the clipboard and pasted into whatever app is in front.
@MainActor
final class DictationController: ObservableObject {
    enum Phase: Equatable { case idle, listening, transcribing, message(String) }

    @Published private(set) var phase: Phase = .idle
    @Published private(set) var level: Float = 0
    /// Live preview of what has been said so far (refreshed while the key is held).
    @Published private(set) var preview = ""
    @Published private(set) var isTrusted = AXIsProcessTrusted()

    /// Input device and the error sink come from the app's preferences.
    var deviceID: () -> AudioObjectID? = { nil }

    private var mic: MicCapture?
    private var buffer = SampleBuffer()
    private var meter: Timer?
    private var trustTimer: Timer?
    private var previewTimer: Timer?
    private var previewTask: Task<Void, Never>?
    private var startedAt = Date()
    private var job: Task<Void, Never>?
    private var hideTask: Task<Void, Never>?
    private lazy var hud = DictationHUD(controller: self)

    // MARK: Permissions

    /// Accessibility access is needed to paste (send ⌘V) and to watch a lone modifier key.
    func requestAccessibility() {
        let opts = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary
        isTrusted = AXIsProcessTrustedWithOptions(opts)
        if !isTrusted, let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility") {
            NSWorkspace.shared.open(url)
        }
    }

    /// Polls until access is granted, then calls `onGranted` (monitors must be re-installed).
    func watchTrust(onGranted: @escaping () -> Void) {
        trustTimer?.invalidate()
        guard !AXIsProcessTrusted() else { isTrusted = true; return }
        trustTimer = Timer.scheduledTimer(withTimeInterval: 1.5, repeats: true) { [weak self] t in
            Task { @MainActor in
                guard let self, AXIsProcessTrusted() else { return }
                t.invalidate()
                self.isTrusted = true
                onGranted()
            }
        }
    }

    // MARK: Press / release

    func begin() {
        switch phase {
        case .listening, .transcribing: return
        default: break
        }
        hideTask?.cancel()
        switch Permissions.microphone {
        case .granted: break
        case .notDetermined:
            Task { _ = await Permissions.requestMicrophone() }
            flash("Allow microphone access, then try again")
            return
        case .denied:
            flash("No microphone access (System Settings › Privacy)")
            return
        }
        let buf = SampleBuffer()
        let resampler = Resampler16k()
        let m = MicCapture()
        do {
            try m.start(deviceID: deviceID()) { pcm, _ in buf.append(resampler.convert(pcm)) }
        } catch {
            flash("Microphone unavailable")
            return
        }
        mic = m
        buffer = buf
        startedAt = Date()
        phase = .listening
        level = 0
        preview = ""
        hud.show()
        previewTimer = Timer.scheduledTimer(withTimeInterval: 0.7, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.refreshPreview() }
        }
        meter = Timer.scheduledTimer(withTimeInterval: 0.05, repeats: true) { [weak self] _ in
            Task { @MainActor in
                guard let self else { return }
                let (_, tail) = self.buffer.stats()
                self.level = AudioIO.rms(tail.suffix(800))
            }
        }
    }

    func end() {
        guard phase == .listening else { return }
        let held = Date().timeIntervalSince(startedAt)
        phase = .transcribing
        job = Task {
            // Let the last audio buffer arrive so the final word is not cut off.
            try? await Task.sleep(for: .milliseconds(200))
            let samples = stopMic()
            guard held >= 0.3 else { hide(); return }
            do {
                let text = try await ModelStore.shared.dictate(samples)
                guard !text.isEmpty else { flash("No speech detected"); return }
                deliver(text)
            } catch {
                flash("Transcription failed")
            }
        }
    }

    /// Discard the recording (another key was pressed while the modifier was held).
    func cancel() {
        guard phase == .listening else { return }
        _ = stopMic()
        hide()
    }

    /// Re-transcribe the last ≤14 s (one Parakeet window, ~0.1 s). Older text is cut off in the
    /// overlay anyway; the final result after release always covers the whole recording.
    private func refreshPreview() {
        guard phase == .listening, previewTask == nil else { return }
        let all = buffer.all()
        guard all.count >= 8_000 else { return }
        let window = 14 * 16_000
        let truncated = all.count > window
        let clip = Array(all.suffix(window))
        previewTask = Task {
            var text = (try? await ModelStore.shared.dictate(clip)) ?? ""
            // The window may start mid-word: drop that fragment.
            if truncated, let space = text.firstIndex(of: " ") { text = "… " + text[text.index(after: space)...] }
            if phase == .listening, !text.isEmpty { preview = text }
            previewTask = nil
        }
    }

    private func stopMic() -> [Float] {
        previewTimer?.invalidate(); previewTimer = nil
        meter?.invalidate(); meter = nil
        mic?.stop(); mic = nil
        level = 0
        return buffer.all()
    }

    // MARK: Output

    private func deliver(_ text: String) {
        let pb = NSPasteboard.general
        pb.clearContents()
        pb.setString(text, forType: .string)
        guard AXIsProcessTrusted() else {
            flash("Copied – allow Accessibility in Settings to paste")
            return
        }
        preview = text
        Task {
            await waitForModifiersReleased()
            Self.sendPaste()
            try? await Task.sleep(for: .milliseconds(600))
            if phase == .transcribing { hide() }
        }
    }

    /// Modifiers still held from the shortcut would turn ⌘V into e.g. ⌃⌥⌘V.
    private func waitForModifiersReleased() async {
        let held: CGEventFlags = [.maskCommand, .maskAlternate, .maskControl, .maskShift, .maskSecondaryFn]
        for _ in 0..<75 {
            if CGEventSource.flagsState(.combinedSessionState).intersection(held).isEmpty { return }
            try? await Task.sleep(for: .milliseconds(20))
        }
    }

    private static func sendPaste() {
        let src = CGEventSource(stateID: .combinedSessionState)
        let v = CGKeyCode(kVK_ANSI_V)
        for down in [true, false] {
            let e = CGEvent(keyboardEventSource: src, virtualKey: v, keyDown: down)
            e?.flags = .maskCommand
            e?.post(tap: .cghidEventTap)
        }
    }

    // MARK: HUD

    private func flash(_ text: String) {
        phase = .message(text)
        hud.show()
        hideTask?.cancel()
        hideTask = Task {
            try? await Task.sleep(for: .seconds(2.5))
            if !Task.isCancelled { hide() }
        }
    }

    private func hide() {
        phase = .idle
        preview = ""
        hud.hide()
    }
}

/// Small floating capsule at the bottom of the screen. It never takes focus, so the
/// paste goes to the app the user is typing in.
@MainActor
final class DictationHUD {
    private let panel: NSPanel

    init(controller: DictationController) {
        panel = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 620, height: 200),
                        styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: true)
        panel.isFloatingPanel = true
        panel.level = .statusBar
        panel.backgroundColor = .clear
        panel.isOpaque = false
        panel.hasShadow = false
        panel.ignoresMouseEvents = true
        panel.hidesOnDeactivate = false
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        panel.contentView = NSHostingView(rootView: DictationHUDView().environmentObject(controller))
    }

    func show() {
        let mouse = NSEvent.mouseLocation
        let screen = NSScreen.screens.first { $0.frame.contains(mouse) } ?? NSScreen.main
        if let f = screen?.visibleFrame {
            panel.setFrameOrigin(NSPoint(x: f.midX - panel.frame.width / 2, y: f.minY + 70))
        }
        panel.orderFrontRegardless()
    }

    func hide() { panel.orderOut(nil) }
}

struct DictationHUDView: View {
    @EnvironmentObject var c: DictationController

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 10) {
                switch c.phase {
                case .listening:
                    Image(systemName: "mic.fill").foregroundStyle(.red)
                    Text("Listening…")
                    LevelBars(level: c.level)
                case .transcribing:
                    ProgressView().controlSize(.small)
                    Text("Transcribing…")
                case .message(let m):
                    Image(systemName: "info.circle")
                    Text(m).lineLimit(1)
                case .idle:
                    EmptyView()
                }
            }
            .font(.callout.weight(.medium))
            if !c.preview.isEmpty, c.phase == .listening || c.phase == .transcribing {
                Text(Self.tail(c.preview))
                    .font(.callout)
                    .foregroundStyle(c.phase == .listening ? .secondary : .primary)
                    .lineLimit(3)
                    .fixedSize(horizontal: false, vertical: true)
                    .frame(maxWidth: 520, alignment: .leading)
            }
        }
        .padding(.horizontal, 16).padding(.vertical, 10)
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 18, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: 18, style: .continuous).strokeBorder(.white.opacity(0.15)))
        .animation(.easeOut(duration: 0.15), value: c.preview)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .bottom)
    }

    /// Keep the end of long text (what was just said), cut at a word boundary.
    static func tail(_ s: String, max: Int = 200) -> String {
        guard s.count > max else { return s }
        let cut = s.suffix(max)
        let start = cut.firstIndex(of: " ").map { cut.index(after: $0) } ?? cut.startIndex
        return "… " + cut[start...]
    }
}

private struct LevelBars: View {
    let level: Float
    var body: some View {
        let db = 20 * log10(max(level, 1e-5))
        let v = Double(max(0, min(1, (db + 55) / 45)))
        HStack(spacing: 3) {
            ForEach(0..<5) { i in
                Capsule().fill(Double(i) / 5 < v ? Color.red : Color.secondary.opacity(0.3))
                    .frame(width: 4, height: 6 + CGFloat(i) * 3)
            }
        }
        .animation(.linear(duration: 0.05), value: v)
    }
}
