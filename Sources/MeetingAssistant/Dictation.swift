import AppKit
import ApplicationServices
import Carbon.HIToolbox
import CoreAudio
import MeetingAssistantCore
import SwiftUI
import os

private let dictationLog = Logger(subsystem: "de.local.MeetingScribe", category: "Dictation")

/// Push-to-talk dictation: hold the global shortcut, speak, release. The text is pasted where the
/// cursor is (or copied when that is not possible).
///
/// Two modes: plain (word for word, all on this Mac) and Scribe+ (cleaned up by a fast Claude
/// model through the local Claude Code CLI). Holding the Scribe+ shortcut while a plain dictation
/// runs turns it into Scribe+.
@MainActor
final class DictationController: ObservableObject {
    enum Phase: Equatable { case idle, listening, transcribing, message(String) }
    enum Mode: Equatable { case plain, plus }

    @Published private(set) var phase: Phase = .idle
    @Published private(set) var mode: Mode = .plain
    /// What happens while `phase == .transcribing`.
    @Published private(set) var detail = "Transcribing…"
    @Published private(set) var level: Float = 0
    /// Live preview of what has been said so far (refreshed while the key is held).
    @Published private(set) var preview = ""
    @Published private(set) var isTrusted = AXIsProcessTrusted()
    /// Dictations whose text did not arrive; kept for 24 hours.
    @Published private(set) var unfinished: [DictationRecovery.Item] = DictationRecovery.current()
    /// The last delivered text, to copy it again from the menu.
    @Published private(set) var lastText: String?

    /// Wiring from the app.
    var deviceID: () -> AudioObjectID? = { nil }
    var prefs: () -> Preferences = { Preferences() }
    var isBusyRecording: () -> Bool = { false }
    var claudeBinary: () -> URL? = { nil }
    var vocabulary = Vocabulary() { didSet { matcherCache = nil } }

    private var matcherCache: VocabularyMatcher?
    private var matcher: VocabularyMatcher {
        if let m = matcherCache { return m }
        let m = vocabulary.matcher()
        matcherCache = m
        return m
    }

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
    private var startField: FocusedField?
    private var plusSession: ScribePlusSession?
    /// A Claude process started ahead of time (Settings › Dictation › Keep Claude ready).
    private var spare: ScribePlusSession?
    private var spareStarted = Date.distantPast
    private var spareTask: Task<Void, Never>?
    private let muter = OutputMuter()

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

    func begin(_ m: Mode) {
        switch phase {
        case .listening:
            // The Scribe+ shortcut includes the plain one: upgrade the running dictation.
            if m == .plus, mode == .plain { mode = .plus; startPlusSession() }
            return
        case .transcribing: return
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
        let mc = MicCapture()
        do {
            try mc.start(deviceID: deviceID()) { pcm, _ in buf.append(resampler.convert(pcm)) }
        } catch {
            flash("Microphone unavailable")
            return
        }
        mic = mc
        buffer = buf
        mode = m
        startedAt = Date()
        phase = .listening
        level = 0
        preview = ""
        hud.show()
        if m == .plus { startPlusSession() }
        if prefs().muteWhileDictating, !isBusyRecording() { muter.mute() }
        // After the microphone runs: reading the field can take a moment in some apps.
        startField = FocusedField.current(readText: false)
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
        detail = "Transcribing…"
        job = Task {
            // Let the last audio buffer arrive so the final word is not cut off.
            try? await Task.sleep(for: .milliseconds(200))
            let samples = stopMic()
            muter.restore()
            guard held >= 0.3 else { closePlus(); hide(); return }
            let voiced = InventedText.longestVoicedRun(samples)
            // A shortcut pressed by accident: nothing that sounds like a word.
            if held < InventedText.accidentalSeconds, voiced < InventedText.minimumVoicedSeconds {
                closePlus(); flash("No speech detected"); return
            }
            await finish(samples, voiced: voiced, mode: mode)
        }
    }

    /// Timings of one dictation, in seconds, for the log and the test hook.
    struct Steps: Codable {
        var mode = "plain"
        var audio = 0.0, asr = 0.0, cleanup = 0.0, field = 0.0, claude = 0.0, total = 0.0
        var outcome = ""
        var raw = "", text = ""
    }
    private(set) var lastSteps = Steps()

    /// Everything after the recording: transcribe, clean up, Scribe+, fit, deliver.
    /// `deliverTo` replaces pasting (test hook).
    func finish(_ samples: [Float], voiced: Double, mode: Mode, deliverTo: ((String, String?) -> Void)? = nil) async {
        var st = Steps()
        st.mode = mode == .plus ? "plus" : "plain"
        st.audio = Double(samples.count) / 16_000
        let t0 = Date()
        func lap(_ since: inout Date) -> Double { let d = Date().timeIntervalSince(since); since = Date(); return d }
        var t = t0
        defer {
            st.total = Date().timeIntervalSince(t0)
            lastSteps = st
            dictationLog.notice("dictation \(st.mode, privacy: .public): audio \(st.audio, format: .fixed(precision: 1), privacy: .public) s, asr \(st.asr, format: .fixed(precision: 2), privacy: .public), cleanup \(st.cleanup, format: .fixed(precision: 2), privacy: .public), field \(st.field, format: .fixed(precision: 2), privacy: .public), claude \(st.claude, format: .fixed(precision: 2), privacy: .public), total \(st.total, format: .fixed(precision: 2), privacy: .public) – \(st.outcome, privacy: .public)")
        }
        let saved = deliverTo == nil ? DictationRecovery.save(samples, plus: mode == .plus) : nil
        let hint = Task { [weak self] in
            try? await Task.sleep(for: .seconds(5))
            guard !Task.isCancelled, let self, self.phase == .transcribing, self.detail == "Transcribing…" else { return }
            self.detail = "Preparing the speech model… (the first dictation after a macOS update can take a few minutes)"
        }
        // The speech model runs off the main thread; the field is read meanwhile.
        let asr = Task.detached(priority: .userInitiated) { try await ModelStore.shared.dictate(samples) }
        let p = prefs()
        var tf = Date()
        let field = (p.fitToCursor || p.scribePlusContext) ? FocusedField.current(readText: true) : FocusedField.current(readText: false)
        st.field = lap(&tf)
        let raw: String
        do {
            raw = try await asr.value
            hint.cancel()
        } catch {
            hint.cancel()
            closePlus()
            refreshUnfinished()
            st.outcome = "asr failed: \(error.localizedDescription)"
            flash(saved == nil ? "Transcription failed" : "Transcription failed – the recording is kept in the menu bar menu")
            return
        }
        st.asr = lap(&t)
        st.raw = raw
        guard var text = cleaned(raw, voiced: voiced) else {
            if let saved { DictationRecovery.delete(saved) }
            st.outcome = "no speech"
            closePlus(); flash("No speech detected"); return
        }
        st.cleanup = lap(&t)
        var note: String?
        if mode == .plus {
            (text, note) = await rewrite(text, field: field, withContext: p.scribePlusContext)
            st.claude = lap(&t)
        }
        if p.fitToCursor, let before = field?.before {
            text = InsertionFit.fit(text, before: before, after: field?.after ?? "")
        }
        st.text = text
        st.outcome = note ?? "ok"
        if let deliverTo { deliverTo(text, note); return }
        deliver(text, field: field, note: note)
        if let saved { DictationRecovery.delete(saved) }
    }

    /// The text processing every dictation gets: the invented-text check, then vocabulary and
    /// spoken forms. Nil when nothing was said.
    private func cleaned(_ raw: String, voiced: Double) -> String? {
        guard !raw.isEmpty, !InventedText.isInvented(raw, longestVoicedRun: voiced) else { return nil }
        return SpokenForms.apply(matcher.apply(raw))
    }

    /// Test hook (`-dictationTest <audio file> -dictationTestMode plus|plain -dictationTestOut <file>`):
    /// runs a recording through `finish` like a real dictation, without pasting, and writes the
    /// timings as JSON lines.
    func runTest(file: URL, plus: Bool, out: URL, runs: Int) async {
        guard let samples = try? AudioIO.load16k(file) else { return }
        var lines: [String] = []
        for _ in 0..<runs {
            try? await Task.sleep(for: .seconds(8))            // the spare is ready, as in daily use
            mode = plus ? .plus : .plain
            if plus { startPlusSession() }
            startField = FocusedField.current(readText: false)
            await finish(samples, voiced: InventedText.longestVoicedRun(samples), mode: mode) { _, _ in }
            if let d = try? JSONEncoder().encode(lastSteps), let l = String(data: d, encoding: .utf8) { lines.append(l) }
        }
        try? lines.joined(separator: "\n").write(to: out, atomically: true, encoding: .utf8)
    }

    /// Discard the recording (another key was pressed while the modifier was held).
    func cancel() {
        guard phase == .listening else { return }
        _ = stopMic()
        muter.restore()
        closePlus()
        hide()
    }

    // MARK: Scribe+

    private func startPlusSession() {
        guard plusSession == nil else { return }
        let model = prefs().scribePlusModel
        if let s = spare, s.isUsable, s.model == model, Date().timeIntervalSince(spareStarted) < 30 * 60 {
            plusSession = s
            spare = nil
            return
        }
        spare?.close(); spare = nil
        guard let bin = claudeBinary() else { return }
        plusSession = try? ScribePlusSession(binary: bin, model: model)
    }

    private func closePlus() {
        plusSession?.close()
        plusSession = nil
        updateSpare(after: 1)
    }

    /// Starts, keeps or stops the spare Claude process to match the settings. A spare is
    /// replaced after half an hour, so an updated Claude Code is picked up.
    func updateSpare(after delay: TimeInterval = 3) {
        spareTask?.cancel()
        let p = prefs()
        guard p.dictationEnabled, p.scribePlusHotKey != nil, p.scribePlusWarm else {
            spare?.close(); spare = nil
            return
        }
        spareTask = Task { [weak self] in
            try? await Task.sleep(for: .seconds(delay))
            guard !Task.isCancelled, let self else { return }
            if let s = self.spare, s.isUsable, s.model == p.scribePlusModel, Date().timeIntervalSince(self.spareStarted) < 30 * 60 { }
            else {
                self.spare?.close(); self.spare = nil
                if let bin = self.claudeBinary() {
                    self.spare = try? ScribePlusSession(binary: bin, model: p.scribePlusModel)
                    self.spareStarted = Date()
                }
            }
            // Check again later (replaces an old or crashed one).
            self.updateSpare(after: 10 * 60)
        }
    }

    /// The Claude rewrite; on any problem the word-for-word text, with a note why.
    private func rewrite(_ text: String, field: FocusedField?, withContext: Bool) async -> (String, String?) {
        defer { closePlus() }
        if plusSession == nil { startPlusSession() }
        guard let session = plusSession else { return (text, "Scribe+ needs Claude Code (Settings › Assist) – pasted word for word") }
        detail = "Writing with Claude…"
        let prompt = ScribePlus.prompt(transcript: text, app: field?.app, before: field?.before, after: field?.after,
                                       useContext: withContext,
                                       vocabulary: ScribePlus.relevantTerms(text, own: vocabulary.terms,
                                                                            aya: vocabulary.useAya ? vocabulary.ayaTerms : []))
        do {
            let out = ScribePlus.withoutEchoedContext(ScribePlus.unwrapped(try await session.rewrite(prompt)),
                                                      before: withContext ? field?.before : nil)
            if let problem = ScribePlus.problem(in: out, source: text) {
                dictationLog.notice("Scribe+ answer rejected: \(String(describing: problem), privacy: .public)")
                return (text, "Scribe+ answer did not fit – pasted word for word")
            }
            return (out, nil)
        } catch let e as ClaudeCLI.CLIError {
            dictationLog.notice("Scribe+ failed: \(e.localizedDescription, privacy: .public)")
            switch e {
            case .usageLimit: return (text, "Claude usage limit reached – pasted word for word")
            case .notLoggedIn: return (text, "Claude Code is not logged in – pasted word for word")
            default: return (text, "Scribe+ failed – pasted word for word")
            }
        } catch {
            return (text, "Scribe+ failed – pasted word for word")
        }
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

    private func deliver(_ text: String, field: FocusedField?, note: String?) {
        lastText = text
        guard AXIsProcessTrusted() else {
            ClipboardPaster.copy(text)
            flash("Copied – allow Accessibility in Settings to paste")
            return
        }
        // Clicked into another field meanwhile: the text must not land there.
        if let start = startField, let now = field ?? FocusedField.current(readText: false), !start.isSameField(as: now) {
            ClipboardPaster.copy(text)
            flash("Copied – the cursor moved to another field. Paste with ⌘V.")
            return
        }
        preview = text
        Task {
            await waitForModifiersReleased()
            ClipboardPaster.paste(text) { ClipboardPaster.sendCommandV() }
            if let note { flash(note); return }
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

    // MARK: Unfinished dictations

    func refreshUnfinished() { unfinished = DictationRecovery.current() }

    /// Transcribes a kept recording again with the same text processing as a normal dictation
    /// (Scribe+ again for a Scribe+ dictation, without cursor context: that field is gone) and
    /// copies the text. It is never pasted: the cursor is no longer where the dictation started.
    func retry(_ item: DictationRecovery.Item) {
        switch phase {
        case .listening, .transcribing: return
        default: break
        }
        hideTask?.cancel()
        Task {
            mode = item.plus ? .plus : .plain
            phase = .transcribing
            detail = "Transcribing the kept dictation…"
            hud.show()
            do {
                let samples = try DictationRecovery.load(item)
                let raw = try await ModelStore.shared.dictate(samples)
                guard var text = cleaned(raw, voiced: InventedText.longestVoicedRun(samples)) else {
                    DictationRecovery.delete(item.url); refreshUnfinished(); flash("No speech in that recording"); return
                }
                var note: String?
                if item.plus {
                    (text, note) = await rewrite(text, field: nil, withContext: false)
                }
                ClipboardPaster.copy(text)
                lastText = text
                DictationRecovery.delete(item.url)
                refreshUnfinished()
                // `rewrite` words its notes for a paste; a retry only copies.
                flash(note.map { $0.replacingOccurrences(of: "pasted", with: "copied") + ". Paste with ⌘V." }
                      ?? "Copied (not pasted) – paste with ⌘V")
            } catch {
                closePlus()
                flash("Transcription failed again")
            }
        }
    }

    func discard(_ item: DictationRecovery.Item) {
        DictationRecovery.delete(item.url)
        refreshUnfinished()
    }

    func copyLast() {
        guard let t = lastText else { return }
        ClipboardPaster.copy(t)
    }

    // MARK: HUD

    private func flash(_ text: String) {
        phase = .message(text)
        hud.show()
        hideTask?.cancel()
        hideTask = Task {
            try? await Task.sleep(for: .seconds(text.count > 50 ? 4 : 2.5))
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
        ScreenSharePrivacy.register(panel)
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
                    if c.mode == .plus {
                        Image(systemName: "sparkles").foregroundStyle(Aya.agent)
                        Text("Scribe+ listening…")
                    } else {
                        Image(systemName: "mic.fill").foregroundStyle(.red)
                        Text("Listening…")
                    }
                    LevelBars(level: c.level)
                case .transcribing:
                    ProgressView().controlSize(.small)
                    Text(c.detail).lineLimit(2)
                case .message(let m):
                    Image(systemName: "info.circle")
                    Text(m).lineLimit(2)
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
