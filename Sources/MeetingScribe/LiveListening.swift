import AppKit
import CoreAudio
import MeetingScribeCore
import SwiftUI

/// One stretch of live transcript with its own time origin: a listening segment
/// (rotated every ~10 minutes) or a recording.
@MainActor
final class TranscriptSegment {
    let start: Date
    let mode: MeetingMode
    let isRecording: Bool
    var lines: [LiveLine] = []
    var transcriber: LiveTranscriber?
    init(start: Date, mode: MeetingMode, isRecording: Bool) {
        self.start = start; self.mode = mode; self.isRecording = isRecording
    }
}

/// A paragraph in the floating window: consecutive lines of one speaker.
struct PanelParagraph: Identifiable, Equatable {
    var id: String
    var date: Date
    var speaker: String
    var colorKey: String
    var isMe: Bool
    var finalText: String
    var pendingText: String
    var recorded: Bool
}

/// Always-on live transcript for the floating window. Captures without saving; while a
/// recording runs, the recording's own live transcript is shown instead, so the window
/// reads as one continuous transcript.
@MainActor
final class ListenController: ObservableObject {
    @Published private(set) var isListening = false      // user wants it on
    @Published private(set) var paragraphs: [PanelParagraph] = []
    let levels = PanelLevels()
    @Published var error: String?

    weak var state: AppState?
    private let listener = LiveListener()
    private var segments: [TranscriptSegment] = []
    private var current: TranscriptSegment?
    private var suspended = false
    private var timer: Timer?
    private let segmentLength: TimeInterval = 10 * 60
    private let maxLines = 4000

    // MARK: Listening

    func startListening() async {
        guard !isListening, let state else { return }
        guard await Permissions.requestMicrophone() else {
            error = "No microphone access. Allow it in System Settings › Privacy & Security › Microphone."
            return
        }
        guard state.modelsReady else { error = "Download the speech models first (main window)."; return }
        error = nil
        isListening = true
        if !state.isRecording { await resumeCapture() }
        timer = Timer.scheduledTimer(withTimeInterval: 0.25, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.tick() }
        }
    }

    func stopListening() async {
        guard isListening else { return }
        isListening = false
        timer?.invalidate(); timer = nil
        await pauseCapture()
        levels.set(mic: 0, system: 0)
    }

    func toggle() async { if isListening { await stopListening() } else { await startListening() } }

    /// A recording takes over the microphone; listening pauses until it ends.
    func recordingWillStart() async { if isListening { suspended = true; await pauseCapture() } }
    func recordingDidStop() async {
        if suspended { suspended = false; if isListening { await resumeCapture() } }
    }

    private var mode: MeetingMode { state?.recordingMode ?? .call }

    private func resumeCapture() async {
        guard let state, !listener.isRunning else { return }
        do {
            // Never hold a Bluetooth headset's mic open: it would degrade the headphones' sound.
            let device = AudioDevices.nonBluetoothInput(instead: state.selectedDeviceID)
            try listener.start(inputDeviceID: device, systemAudio: mode.recordsSystemAudio)
            await beginSegment()
        } catch {
            self.error = "Could not listen: \(error.localizedDescription)"
            isListening = false
        }
    }

    private func pauseCapture() async {
        listener.stop()
        if let seg = current { current = nil; Task { await finish(seg) } }
    }

    private func beginSegment() async {
        let seg = TranscriptSegment(start: Date(), mode: mode, isRecording: false)
        let lt = LiveTranscriber(mode: seg.mode) { [weak self, weak seg] lines in
            Task { @MainActor in seg?.lines = lines; self?.scheduleRebuild() }
        }
        await lt.start(tracks: seg.mode.recordsSystemAudio ? [.mic, .system] : [.mic])
        seg.transcriber = lt
        let old = current
        current = seg
        segments.append(seg)
        listener.beginSegment(mic: { lt.feed(.mic, $0) }, system: { lt.feed(.system, $0) })
        if let old { await finish(old) }
    }

    private func finish(_ seg: TranscriptSegment) async {
        if let lt = seg.transcriber { await lt.stop() }
        seg.transcriber = nil   // frees audio buffers and diarizer state
        trim()
    }

    private func tick() {
        let rec = state?.isRecording == true
        levels.set(mic: rec ? state?.micLevel ?? 0 : listener.micLevel,
                   system: rec ? state?.systemLevel ?? 0 : listener.systemLevel)
        // Start a fresh transcriber now and then, preferably in a pause.
        guard let seg = current else { return }
        let age = Date().timeIntervalSince(seg.start)
        let lastEnd = seg.lines.last?.end ?? 0
        if age > segmentLength && (age - lastEnd > 3 || age > segmentLength + 120) {
            Task { await beginSegment() }
        }
    }

    // MARK: Recording segments

    func beginRecordingSegment(start: Date, mode: MeetingMode) -> TranscriptSegment {
        let seg = TranscriptSegment(start: start, mode: mode, isRecording: true)
        segments.append(seg)
        return seg
    }

    func update(_ seg: TranscriptSegment, lines: [LiveLine]) { seg.lines = lines; scheduleRebuild() }

    /// Coalesce bursts of updates (two tracks, speaker labels) into one redraw.
    private var rebuildScheduled = false
    private func scheduleRebuild() {
        guard !rebuildScheduled else { return }
        rebuildScheduled = true
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.4) { [weak self] in
            self?.rebuildScheduled = false
            self?.rebuild()
        }
    }

    // MARK: Output

    func clear() {
        segments.removeAll { $0 !== current && $0.transcriber == nil && !($0.isRecording && state?.isRecording == true) }
        // Lines of running segments are hidden from here on.
        hiddenBefore = Date()
        rebuild()
    }
    private var hiddenBefore = Date.distantPast

    var plainText: String {
        let f = DateFormatter(); f.dateFormat = "HH:mm:ss"
        return paragraphs.map { "[\(f.string(from: $0.date))] \($0.speaker): \($0.finalText) \($0.pendingText)".trimmingCharacters(in: .whitespaces) }
            .joined(separator: "\n")
    }

    private func trim() {
        var total = segments.reduce(0) { $0 + $1.lines.count }
        while total > maxLines, let first = segments.first, first !== current, first.transcriber == nil {
            total -= first.lines.count
            segments.removeFirst()
        }
    }

    private func rebuild() {
        let me = state?.prefs.myName.isEmpty == false ? state!.prefs.myName : "Me"
        var out: [PanelParagraph] = []
        for (si, seg) in segments.enumerated() {
            var labels: [String: String] = [:]
            var n = 0
            for l in seg.lines {
                let date = seg.start.addingTimeInterval(l.start)
                guard date >= hiddenBefore else { continue }
                let key = l.speaker ?? "?"
                let name: String
                if key == "me" { name = me }
                else if l.speaker == nil { name = "…" }
                else {
                    if labels[key] == nil {
                        n += 1
                        labels[key] = seg.mode == .call && key.hasPrefix("system") ? "Remote \(n)" : "Speaker \(n)"
                    }
                    name = labels[key]!
                }
                let colorKey = "\(si)-\(key)"
                if var last = out.last, last.speaker == name, last.colorKey == colorKey,
                   date.timeIntervalSince(last.date) < 120 {
                    if l.isFinal && last.pendingText.isEmpty { last.finalText += " " + l.text }
                    else { last.pendingText += (last.pendingText.isEmpty ? "" : " ") + l.text }
                    out[out.count - 1] = last
                } else {
                    out.append(PanelParagraph(id: "\(seg.start.timeIntervalSince1970)-\(l.id)", date: date, speaker: name,
                                              colorKey: colorKey, isMe: key == "me",
                                              finalText: l.isFinal ? l.text : "", pendingText: l.isFinal ? "" : l.text,
                                              recorded: seg.isRecording))
                }
            }
        }
        if out != paragraphs { paragraphs = out }
    }
}

// MARK: - Window

/// A small always-on-top window that can be moved and resized freely and does not take
/// focus from the call app when clicked.
final class FloatingPanel: NSPanel {
    override var canBecomeKey: Bool { true }
}

@MainActor
final class FloatingPanelController: NSObject, NSWindowDelegate {
    private var panel: FloatingPanel?
    private weak var state: AppState?
    nonisolated static let openKey = "floatingPanel.open"

    init(state: AppState) { self.state = state }

    var isVisible: Bool { panel?.isVisible == true }

    func toggle() { if isVisible { close() } else { show() } }

    func show() {
        guard let state else { return }
        if panel == nil {
            let p = FloatingPanel(contentRect: NSRect(x: 0, y: 0, width: 420, height: 320),
                                  styleMask: [.titled, .closable, .resizable, .fullSizeContentView, .nonactivatingPanel, .utilityWindow],
                                  backing: .buffered, defer: false)
            p.title = "Live Transcript"
            p.titlebarAppearsTransparent = true
            p.titleVisibility = .hidden
            p.isFloatingPanel = true
            p.level = .floating
            p.hidesOnDeactivate = false
            p.isMovableByWindowBackground = true
            p.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
            p.minSize = NSSize(width: 260, height: 120)
            p.isReleasedWhenClosed = false
            p.delegate = self
            p.contentView = NSHostingView(rootView: FloatingTranscriptView()
                .environmentObject(state).environmentObject(state.listen))
            p.setFrameAutosaveName("FloatingTranscript")
            if !p.setFrameUsingName("FloatingTranscript"), let screen = NSScreen.main?.visibleFrame {
                p.setFrameOrigin(NSPoint(x: screen.maxX - 440, y: screen.maxY - 340))
            }
            panel = p
        }
        panel?.orderFrontRegardless()
        UserDefaults.standard.set(true, forKey: Self.openKey)
        Task { await state.listen.startListening() }
    }

    func close() { UserDefaults.standard.set(false, forKey: Self.openKey); panel?.close() }

    /// Only called when the user closes the window, not when the app quits, so the
    /// window comes back at the next launch.
    nonisolated func windowShouldClose(_ sender: NSWindow) -> Bool {
        UserDefaults.standard.set(false, forKey: Self.openKey)
        return true
    }

    nonisolated func windowWillClose(_ notification: Notification) {
        Task { @MainActor in
            await self.state?.listen.stopListening()
            self.state?.objectWillChange.send()
        }
    }
}

// MARK: - View

struct FloatingTranscriptView: View {
    @EnvironmentObject var state: AppState
    @EnvironmentObject var listen: ListenController
    @AppStorage("floatingPanel.fontSize") private var fontSize = 13.0
    /// Follow new lines until the user scrolls away; scrolling back down resumes.
    /// Only the user's own scrolling changes this, not new content or resizing.
    @State private var atBottom = true
    @State private var nearBottom = true
    @State private var userScrolling = false
    @State private var jumpToEnd = 0
    /// Only the latest paragraphs are drawn; Copy Transcript still includes everything.
    private let shown = 150
    private static let time: DateFormatter = { let f = DateFormatter(); f.dateFormat = "HH:mm"; return f }()

    var body: some View {
        VStack(spacing: 0) {
            controls
                .padding(.leading, 70)    // room for the window buttons
                .padding(.trailing, 8)
                .frame(height: 28)
            Divider()
            ZStack(alignment: .bottomTrailing) {
                transcript
                if !atBottom {
                    Button { atBottom = true; jumpToEnd += 1 } label: {
                        Image(systemName: "arrow.down.circle.fill").font(.title2)
                    }
                    .buttonStyle(.plain).padding(8).help("Jump to the latest line")
                }
            }
        }
        // Opaque on purpose: a blurred, see-through window makes the window server
        // recompose it constantly whenever anything behind it moves.
        .background(Color(nsColor: .windowBackgroundColor))
        .ignoresSafeArea()
    }

    private var controls: some View {
        HStack(spacing: 8) {
            if state.isRecording {
                Button { Task { await state.stopRecording() } } label: {
                    HStack(spacing: 4) {
                        Image(systemName: "stop.fill")
                        Text(Exporter.clock(state.elapsed)).monospacedDigit()
                    }
                    .foregroundStyle(.red)
                }
                .help("Stop recording")
            } else {
                Button { Task { await state.startRecording() } } label: {
                    Image(systemName: "record.circle").foregroundStyle(.red)
                }
                .help("Start recording (\(state.recordingMode.title))")
            }
            Button { Task { await listen.toggle() } } label: {
                Image(systemName: listen.isListening ? "pause.circle" : "ear")
            }
            .help(listen.isListening ? "Pause the live transcript" : "Resume the live transcript")
            .disabled(state.isRecording)
            LevelDots(levels: listen.levels, system: state.recordingMode.recordsSystemAudio)
            Spacer(minLength: 4)
            Menu {
                Button("Copy Transcript") {
                    NSPasteboard.general.clearContents()
                    NSPasteboard.general.setString(listen.plainText, forType: .string)
                }
                Button("Clear") { listen.clear() }
                Divider()
                Button("Larger Text") { fontSize = min(28, fontSize + 1) }
                Button("Smaller Text") { fontSize = max(9, fontSize - 1) }
                Divider()
                Button("Open MeetingScribe") {
                    NSApp.activate(ignoringOtherApps: true)
                    let main = NSApp.windows.first { $0.identifier?.rawValue.hasPrefix("main") == true }
                        ?? NSApp.windows.first { !($0 is NSPanel) && $0.canBecomeMain }
                    main?.makeKeyAndOrderFront(nil)
                }
            } label: { Image(systemName: "ellipsis.circle") }
            .menuStyle(.borderlessButton).menuIndicator(.hidden).fixedSize()
        }
        .buttonStyle(.borderless)
        .font(.system(size: 13))
    }

    // A plain VStack on purpose: a LazyVStack anchored to the bottom got stuck in an
    // endless layout loop (100% CPU, growing memory) while the window was resized.
    private var transcript: some View {
        let items = Array(listen.paragraphs.suffix(shown))
        return ScrollViewReader { proxy in
            ScrollView {
                VStack(alignment: .leading, spacing: 6) {
                    if let e = listen.error {
                        Text(e).foregroundStyle(.orange).font(.callout)
                    } else if items.isEmpty {
                        Text(listen.isListening || state.isRecording ? "Listening… nothing is saved unless you press record."
                                                                     : "Paused.")
                            .foregroundStyle(.secondary).font(.callout)
                    }
                    ForEach(items) { p in paragraph(p) }
                    Color.clear.frame(height: 1).id("end")
                }
                .padding(10)
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            .onScrollGeometryChange(for: Bool.self) { g in
                g.contentOffset.y + g.containerSize.height >= g.contentSize.height - 40
            } action: { _, bottom in
                nearBottom = bottom
                // Content grew or the window was resized: stay pinned to the end.
                if atBottom && !bottom && !userScrolling { scrollToEnd(proxy) }
            }
            .onScrollPhaseChange { _, phase in
                switch phase {
                case .interacting, .tracking, .decelerating:
                    userScrolling = true
                case .idle:
                    if userScrolling { userScrolling = false; atBottom = nearBottom }
                default:
                    break
                }
            }
            .onChange(of: listen.paragraphs) { _, _ in if atBottom { scrollToEnd(proxy) } }
            .onChange(of: jumpToEnd) { _, _ in scrollToEnd(proxy) }
            .onAppear { proxy.scrollTo("end", anchor: .bottom) }
        }
    }

    /// Deferred by one run-loop turn so the new lines are laid out before scrolling.
    private func scrollToEnd(_ proxy: ScrollViewProxy) {
        DispatchQueue.main.async { proxy.scrollTo("end", anchor: .bottom) }
    }

    private func paragraph(_ p: PanelParagraph) -> some View {
        VStack(alignment: .leading, spacing: 1) {
            HStack(spacing: 6) {
                Text(p.speaker).fontWeight(.semibold)
                    .foregroundStyle(p.speaker == "…" ? Color.secondary : speakerColor(p.colorKey, isMe: p.isMe))
                Text(Self.time.string(from: p.date)).foregroundStyle(.tertiary)
                if p.recorded { Image(systemName: "record.circle").foregroundStyle(.red.opacity(0.7)) }
            }
            .font(.system(size: fontSize * 0.8))
            (Text(p.finalText) + Text(p.finalText.isEmpty || p.pendingText.isEmpty ? "" : " ")
                + Text(p.pendingText).foregroundColor(.secondary))
                .font(.system(size: fontSize))
                .fixedSize(horizontal: false, vertical: true)
                .frame(maxWidth: .infinity, alignment: .leading)
        }
        .id(p.id)
    }
}


/// Levels live in their own object so that only the two little icons redraw.
@MainActor
final class PanelLevels: ObservableObject {
    @Published private(set) var mic = false
    @Published private(set) var system = false
    func set(mic m: Float, system s: Float) {
        // On/off is all the icons show; -45 dB is roughly "someone is talking".
        let mOn = m > 0.0056, sOn = s > 0.0056
        if mOn != mic { mic = mOn }
        if sOn != system { system = sOn }
    }
}

private struct LevelDots: View {
    @ObservedObject var levels: PanelLevels
    let system: Bool
    var body: some View {
        HStack(spacing: 6) {
            icon("mic.fill", levels.mic)
            if system { icon("speaker.wave.2.fill", levels.system) }
        }
    }
    private func icon(_ name: String, _ on: Bool) -> some View {
        Image(systemName: name).font(.system(size: 10))
            .foregroundStyle(on ? Color.green : Color.secondary.opacity(0.5))
            .help(on ? "Sound is coming in" : "Silence")
    }
}
