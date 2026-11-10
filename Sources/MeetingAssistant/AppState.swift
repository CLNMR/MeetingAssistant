import AppKit
import AVFoundation
import CoreAudio
import MeetingAssistantCore
import SwiftUI

struct Preferences: Codable, Equatable {
    var engine: ASREngineKind = .parakeetUltra
    var language: TranscriptLanguage = .auto
    var myName: String = "Me"
    var inputDeviceUID: String? = nil
    var liveTranscription = true
    var autoTranscribe = true
    var defaultMode: MeetingMode = .call
    var dictationEnabled = true
    var dictationHotKey: HotKey? = .rightOption
    /// Scribe+: the dictation is cleaned up by a fast Claude model before it is pasted.
    var scribePlusHotKey: HotKey? = .rightOptionCommand
    /// Scribe+ sees the text around the cursor and the app's name, to match tone and context.
    var scribePlusContext = true
    /// Keep one Claude process started in the background, so Scribe+ answers in about a second.
    var scribePlusWarm = true
    /// "sonnet" (careful, 2–4 s) or "haiku" (fastest, about 1 s).
    var scribePlusModel = ScribePlus.model
    /// Spaces and capitals follow the text around the cursor.
    var fitToCursor = true
    /// Mute the sound output while dictating (not during a recording).
    var muteWhileDictating = true

    init() {}

    enum CodingKeys: String, CodingKey {
        case engine, language, myName, inputDeviceUID, liveTranscription, autoTranscribe, defaultMode
        case dictationEnabled, dictationHotKey
        case scribePlusHotKey, scribePlusContext, scribePlusWarm, scribePlusModel, fitToCursor, muteWhileDictating
    }

    // Missing keys fall back to defaults so new settings never reset the old ones.
    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        let d = Preferences()
        engine = (try? c.decodeIfPresent(ASREngineKind.self, forKey: .engine)) ?? d.engine
        language = (try? c.decodeIfPresent(TranscriptLanguage.self, forKey: .language)) ?? d.language
        myName = (try? c.decodeIfPresent(String.self, forKey: .myName)) ?? d.myName
        inputDeviceUID = try? c.decodeIfPresent(String.self, forKey: .inputDeviceUID)
        liveTranscription = (try? c.decodeIfPresent(Bool.self, forKey: .liveTranscription)) ?? d.liveTranscription
        autoTranscribe = (try? c.decodeIfPresent(Bool.self, forKey: .autoTranscribe)) ?? d.autoTranscribe
        defaultMode = (try? c.decodeIfPresent(MeetingMode.self, forKey: .defaultMode)) ?? d.defaultMode
        dictationEnabled = (try? c.decodeIfPresent(Bool.self, forKey: .dictationEnabled)) ?? d.dictationEnabled
        dictationHotKey = c.contains(.dictationHotKey) ? (try? c.decodeIfPresent(HotKey.self, forKey: .dictationHotKey)) : d.dictationHotKey
        scribePlusHotKey = c.contains(.scribePlusHotKey) ? (try? c.decodeIfPresent(HotKey.self, forKey: .scribePlusHotKey)) : d.scribePlusHotKey
        scribePlusContext = (try? c.decodeIfPresent(Bool.self, forKey: .scribePlusContext)) ?? d.scribePlusContext
        scribePlusWarm = (try? c.decodeIfPresent(Bool.self, forKey: .scribePlusWarm)) ?? d.scribePlusWarm
        scribePlusModel = (try? c.decodeIfPresent(String.self, forKey: .scribePlusModel)) ?? d.scribePlusModel
        fitToCursor = (try? c.decodeIfPresent(Bool.self, forKey: .fitToCursor)) ?? d.fitToCursor
        muteWhileDictating = (try? c.decodeIfPresent(Bool.self, forKey: .muteWhileDictating)) ?? d.muteWhileDictating
    }

    func encode(to encoder: Encoder) throws {
        var c = encoder.container(keyedBy: CodingKeys.self)
        try c.encode(engine, forKey: .engine)
        try c.encode(language, forKey: .language)
        try c.encode(myName, forKey: .myName)
        try c.encodeIfPresent(inputDeviceUID, forKey: .inputDeviceUID)
        try c.encode(liveTranscription, forKey: .liveTranscription)
        try c.encode(autoTranscribe, forKey: .autoTranscribe)
        try c.encode(defaultMode, forKey: .defaultMode)
        try c.encode(dictationEnabled, forKey: .dictationEnabled)
        try c.encode(dictationHotKey, forKey: .dictationHotKey)   // explicit null = "no shortcut"
        try c.encode(scribePlusHotKey, forKey: .scribePlusHotKey)
        try c.encode(scribePlusContext, forKey: .scribePlusContext)
        try c.encode(scribePlusWarm, forKey: .scribePlusWarm)
        try c.encode(scribePlusModel, forKey: .scribePlusModel)
        try c.encode(fitToCursor, forKey: .fitToCursor)
        try c.encode(muteWhileDictating, forKey: .muteWhileDictating)
    }

    static let key = "preferences.v1"
    static func load() -> Preferences {
        guard let d = UserDefaults.standard.data(forKey: key),
              let p = try? JSONDecoder().decode(Preferences.self, from: d) else { return Preferences() }
        return p
    }
    func save() { if let d = try? JSONEncoder().encode(self) { UserDefaults.standard.set(d, forKey: Self.key) } }

    var pipelineSettings: PipelineSettings {
        var s = PipelineSettings()
        s.engine = engine; s.language = language; s.myName = myName.isEmpty ? "Me" : myName
        return s
    }
}

enum Selection: Hashable { case recorder, session(String) }

/// The two halves of the app: Scribe (local recording and transcription) and Assist (live meeting assistant).
enum AppMode: String, CaseIterable, Identifiable {
    case scribe, assist
    var id: String { rawValue }
    var title: String { self == .scribe ? "Scribe" : "Assist" }
    var icon: String { self == .scribe ? "waveform" : "sparkles" }
}

struct JobProgress: Equatable { var fraction: Double; var message: String }

@MainActor
final class AppState: ObservableObject {
    @Published var prefs = Preferences.load() {
        didSet {
            guard prefs != oldValue else { return }
            prefs.save()
            if prefs.dictationEnabled != oldValue.dictationEnabled || prefs.dictationHotKey != oldValue.dictationHotKey
                || prefs.scribePlusHotKey != oldValue.scribePlusHotKey || prefs.scribePlusWarm != oldValue.scribePlusWarm
                || prefs.scribePlusModel != oldValue.scribePlusModel {
                applyDictationShortcut()
            }
        }
    }
    @Published var sessions: [SessionInfo] = []
    /// Sessions recorded or imported in Assist mode (they have an assist.json).
    @Published var assistSessionIDs: Set<String> = []
    @Published var appMode = AppMode(rawValue: UserDefaults.standard.string(forKey: "appMode") ?? "") ?? .scribe {
        didSet { UserDefaults.standard.set(appMode.rawValue, forKey: "appMode") }
    }
    @Published var selection: Selection? = .recorder
    @Published var errorMessage: String?

    // Recording
    @Published var isRecording = false
    @Published var elapsed: Double = 0
    @Published var micLevel: Float = 0
    @Published var systemLevel: Float = 0
    @Published var liveLines: [LiveLine] = []
    @Published var recordingMode: MeetingMode = .call
    @Published var recordingTitle = ""
    @Published var expectedSpeakers = 0       // 0 = automatic
    @Published var inputDevices: [AudioInputDevice] = []
    @Published var recordingSessionID: String?

    // Processing
    @Published var progress: [String: JobProgress] = [:]
    @Published var modelSetup: JobProgress?
    @Published var modelsReady = ModelStore.coreModelsCached()

    // Push-to-talk dictation
    let dictation = DictationController()
    private let hotKey = GlobalHotKey()
    private let plusHotKey = GlobalHotKey()
    @Published var hotKeyError: String?
    @Published var vocabulary = Vocabulary.load() {
        didSet { if vocabulary != oldValue { vocabulary.save(); dictation.vocabulary = vocabulary } }
    }

    // Floating always-on live transcript
    let listen = ListenController()
    lazy var floating = FloatingPanelController(state: self)

    // Assist mode
    lazy var assist = AssistController(app: self)
    /// Set by Assist: live transcription is on regardless of the preference, and lines go to the assistant.
    var forceLive = false
    var liveLinesHook: (([LiveLine]) -> Void)?
    var onRecordingStopped: ((String) -> Void)?

    private var recorder: SessionRecorder?
    private var live: LiveTranscriber?
    private var meterTimer: Timer?
    private var queue: [String] = []
    /// Engine chosen for one run ("Transcribe Again" with an engine); the preference stays as it is.
    private var engineOverride: [String: ASREngineKind] = [:]
    private var worker: Task<Void, Never>?

    init() {
        recordingMode = prefs.defaultMode
        try? AppPaths.ensure()
        refreshDevices()
        reload()
        // `-select <session-id>` launch argument opens a recording directly.
        if let id = UserDefaults.standard.string(forKey: "select") { selection = .session(id) }
        // Recover sessions interrupted by a crash or quit.
        for var s in sessions where [.recording, .transcribing, .queued].contains(s.status) {
            if s.status == .recording { s = SessionStore.recoverInterrupted(s); try? SessionStore.save(s) } else { enqueue(s.id) }
        }
        reload()
        setUpDictation()
        _ = assist      // loads the Aya lists (and the vocabulary's names) when Aya is connected
        listen.state = self
        if let file = UserDefaults.standard.string(forKey: "dictationTest"),
           let out = UserDefaults.standard.string(forKey: "dictationTestOut") {
            // Debug: run an audio file through the dictation path with timings, then quit.
            let plus = UserDefaults.standard.string(forKey: "dictationTestMode") != "plain"
            let runs = max(1, UserDefaults.standard.integer(forKey: "dictationTestRuns"))
            Task {
                await dictation.runTest(file: URL(fileURLWithPath: file), plus: plus, out: URL(fileURLWithPath: out), runs: runs)
                exit(0)
            }
        }
        if let dir = UserDefaults.standard.string(forKey: "assistSnapshot") {
            // Debug: render the Assist screens with sample data to PNGs and quit (used for testing).
            DispatchQueue.main.asyncAfter(deadline: .now() + 1) { [weak self] in
                guard let self else { return }
                AssistSnapshots.run(app: self, dir: URL(fileURLWithPath: dir),
                                    record: UserDefaults.standard.string(forKey: "assistSnapshotRecord").map { URL(fileURLWithPath: $0) })
                exit(0)
            }
            return
        }
        if UserDefaults.standard.bool(forKey: FloatingPanelController.openKey) {
            DispatchQueue.main.async { [weak self] in self?.floating.show() }
        }
    }

    func reload() {
        sessions = SessionStore.list()
        assistSessionIDs = Set(sessions.map(\.id).filter { FileManager.default.fileExists(atPath: AssistRecord.url($0).path) })
    }
    func refreshDevices() { inputDevices = AudioDevices.inputDevices() }

    var selectedDeviceID: AudioObjectID? {
        guard let uid = prefs.inputDeviceUID else { return nil }
        return inputDevices.first { $0.uid == uid }?.id
    }

    // MARK: - Dictation

    private func setUpDictation() {
        dictation.deviceID = { [weak self] in self?.selectedDeviceID }
        dictation.prefs = { [weak self] in self?.prefs ?? Preferences() }
        dictation.isBusyRecording = { [weak self] in self?.isRecording ?? false }
        dictation.claudeBinary = { [weak self] in
            guard let self else { return nil }
            return self.assist.claudeBinary ?? ClaudeCLI.locate(preferred: self.assist.settings.claudePath)
        }
        dictation.vocabulary = vocabulary
        hotKey.onPress = { [weak self] in self?.dictation.begin(.plain) }
        hotKey.onRelease = { [weak self] in self?.dictation.end() }
        hotKey.onCancel = { [weak self] in self?.dictation.cancel() }
        plusHotKey.onPress = { [weak self] in self?.dictation.begin(.plus) }
        plusHotKey.onRelease = { [weak self] in self?.dictation.end() }
        plusHotKey.onCancel = { [weak self] in self?.dictation.cancel() }
        applyDictationShortcut()
        // A lone modifier key is only visible to the app once Accessibility is granted.
        dictation.watchTrust { [weak self] in self?.applyDictationShortcut() }
        if prefs.dictationEnabled, modelsReady {
            Task.detached(priority: .utility) { _ = try? await ModelStore.shared.parakeetManager() }
        }
    }

    func applyDictationShortcut() {
        hotKey.register(prefs.dictationEnabled ? prefs.dictationHotKey : nil)
        plusHotKey.register(prefs.dictationEnabled ? prefs.scribePlusHotKey : nil)
        hotKeyError = hotKey.registrationError ?? plusHotKey.registrationError
        dictation.updateSpare()
    }

    /// Pause the global shortcuts while the user records a new one in Settings.
    func suspendDictationShortcut(_ suspend: Bool) {
        if suspend { hotKey.unregister(); plusHotKey.unregister() } else { applyDictationShortcut() }
    }

    // MARK: - Recording

    func toggleRecording() async {
        if isRecording { await stopRecording() } else { await startRecording() }
    }

    func startRecording() async {
        guard !isRecording else { return }
        guard await Permissions.requestMicrophone() else {
            errorMessage = "MeetingAssistant has no access to the microphone. Allow it in System Settings › Privacy & Security › Microphone, then try again."
            return
        }
        do {
            let title = recordingTitle.trimmingCharacters(in: .whitespaces)
            let rec = try SessionRecorder(mode: recordingMode, title: title.isEmpty ? nil : title,
                                          expectedSpeakers: expectedSpeakers > 0 ? expectedSpeakers : nil,
                                          language: prefs.language == .auto ? nil : prefs.language.rawValue)
            liveLines = []
            await listen.recordingWillStart()   // the recording takes over the microphone
            if prefs.liveTranscription || listen.isListening || forceLive {
                let seg = listen.beginRecordingSegment(start: rec.info.startedAt, mode: recordingMode)
                let lt = LiveTranscriber(mode: recordingMode) { lines in
                    Task { @MainActor [weak self] in
                        self?.liveLines = lines
                        self?.listen.update(seg, lines: lines)
                        self?.liveLinesHook?(lines)
                    }
                }
                await lt.start(tracks: rec.info.tracks)
                rec.onMicSamples = { lt.feed(.mic, $0) }
                rec.onSystemSamples = { lt.feed(.system, $0) }
                live = lt
            }
            try rec.start(inputDeviceID: selectedDeviceID)
            recorder = rec
            recordingSessionID = rec.info.id
            isRecording = true
            elapsed = 0
            meterTimer = Timer.scheduledTimer(withTimeInterval: 0.1, repeats: true) { [weak self] _ in
                Task { @MainActor in
                    guard let self, let r = self.recorder else { return }
                    // Only publish visible changes: every publish redraws all open windows.
                    let e = (r.elapsed * 2).rounded(.down) / 2
                    if e != self.elapsed { self.elapsed = e }
                    let m = quantizedLevel(r.micLevel), s = quantizedLevel(r.systemLevel)
                    if m != self.micLevel { self.micLevel = m }
                    if s != self.systemLevel { self.systemLevel = s }
                }
            }
            selection = .recorder
            reload()
        } catch {
            errorMessage = "Could not start recording: \(error.localizedDescription)"
            live = nil
            await listen.recordingDidStop()
        }
    }

    /// Stop recording. With `keepUntil`, everything after that time (seconds) is deleted,
    /// for when the user forgot to stop.
    func stopRecording(keepUntil: Double? = nil) async {
        guard let rec = recorder else { return }
        let assisted = forceLive
        forceLive = false
        liveLinesHook = nil
        meterTimer?.invalidate(); meterTimer = nil
        isRecording = false
        micLevel = 0; systemLevel = 0
        recorder = nil
        recordingSessionID = nil
        do {
            let info = try rec.stop()
            if let lt = live { await lt.stop() }
            live = nil
            await listen.recordingDidStop()
            recordingTitle = ""
            if let keepUntil, keepUntil < info.durationSec - 0.5 {
                do { try SessionEdit.trim(info.id, start: 0, end: keepUntil) }
                catch { errorMessage = "Could not remove the end: \(error.localizedDescription)" }
            }
            reload()
            selection = .session(info.id)
            // Assist needs the final transcript for the recap.
            if prefs.autoTranscribe || assisted { enqueue(info.id) }
            onRecordingStopped?(info.id)
        } catch {
            errorMessage = "Could not finish recording: \(error.localizedDescription)"
            await listen.recordingDidStop()
            onRecordingStopped?("")
        }
    }

    // MARK: - Processing queue

    func enqueue(_ id: String) {
        guard !queue.contains(id) else { return }
        queue.append(id)
        setStatus(id, .queued)
        if worker == nil { worker = Task { [weak self] in await self?.drain() } }
    }

    private func drain() async {
        while !queue.isEmpty {
            let id = queue.removeFirst()
            setStatus(id, .transcribing)
            progress[id] = JobProgress(fraction: 0, message: "Starting")
            do {
                var settings = prefs.pipelineSettings
                if let e = engineOverride.removeValue(forKey: id) { settings.engine = e }
                _ = try await TranscriptionPipeline.run(sessionID: id, settings: settings) { p, msg in
                    Task { @MainActor [weak self] in
                        if self?.progress[id] != nil { self?.progress[id] = JobProgress(fraction: p, message: msg) }
                    }
                }
                setStatus(id, .done)
                markModelsReady()
                if !NSApp.isActive { NSApp.requestUserAttention(.informationalRequest) }
                NSSound(named: "Glass")?.play()
            } catch is CancellationError {
                setStatus(id, .recorded)
            } catch {
                setStatus(id, .failed)
                errorMessage = "Transcription failed: \(error.localizedDescription)"
            }
            progress[id] = nil
            if Task.isCancelled { break }
        }
        worker = nil
        await ModelStore.shared.unloadTranscriptionModels()
    }

    func cancelProcessing() {
        worker?.cancel()
        for id in queue { setStatus(id, .recorded) }
        queue.removeAll()
        engineOverride.removeAll()
    }

    private func setStatus(_ id: String, _ status: SessionInfo.Status) {
        guard var s = try? JSONIO.read(SessionInfo.self, from: AppPaths.info(id)) else { return }
        s.status = status
        try? SessionStore.save(s)
        reload()
    }

    private func markModelsReady() {
        modelsReady = true
        UserDefaults.standard.set(true, forKey: "modelsReady")
    }

    func prepareModels() {
        guard modelSetup == nil else { return }
        modelSetup = JobProgress(fraction: 0, message: "Starting download")
        let engine = prefs.engine
        Task {
            do {
                try await ModelStore.shared.prepareAll(engine: engine) { p, msg in
                    Task { @MainActor [weak self] in
                        if self?.modelSetup != nil { self?.modelSetup = JobProgress(fraction: p, message: msg) }
                    }
                }
                markModelsReady()
            } catch {
                errorMessage = "Model download failed: \(error.localizedDescription)"
            }
            modelSetup = nil
        }
    }

    // MARK: - Session management

    func delete(_ id: String) {
        try? SessionStore.delete(id)
        if selection == .session(id) { selection = .recorder }
        reload()
    }

    func trim(_ id: String, start: Double, end: Double) {
        do { try SessionEdit.trim(id, start: start, end: end) }
        catch { errorMessage = "Could not trim: \(error.localizedDescription)" }
        reload()
    }

    func split(_ id: String, at time: Double) {
        do {
            let second = try SessionEdit.split(id, at: time)
            reload()
            selection = .session(second.id)
        } catch {
            errorMessage = "Could not split: \(error.localizedDescription)"
            reload()
        }
    }

    func split(_ id: String, at pause: Pause, removePause: Bool) {
        do {
            let second = try SessionEdit.split(id, at: pause, removePause: removePause)
            reload()
            selection = .session(second.id)
        } catch {
            errorMessage = "Could not split: \(error.localizedDescription)"
            reload()
        }
    }

    /// Transcribe a session again with a chosen engine and spoken language. The engine applies to
    /// this run only; the language is saved with the recording.
    func retranscribe(_ id: String, engine: ASREngineKind, language: TranscriptLanguage) {
        guard var s = try? JSONIO.read(SessionInfo.self, from: AppPaths.info(id)) else { return }
        s.language = language == .auto ? nil : language.rawValue
        try? SessionStore.save(s)
        engineOverride[id] = engine
        enqueue(id)
    }

    func rename(_ id: String, to title: String) {
        guard var s = sessions.first(where: { $0.id == id }) else { return }
        s.title = title
        try? SessionStore.save(s)
        reload()
    }

    func importAudio() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.audio]
        panel.allowsMultipleSelection = true
        panel.message = "Choose one recording (in-person meeting), or two files: first your microphone, then the call audio."
        guard panel.runModal() == .OK, !panel.urls.isEmpty else { return }
        let urls = panel.urls
        do {
            let info = try SessionStore.importFiles(mic: urls[0], system: urls.count > 1 ? urls[1] : nil,
                                                    mode: urls.count > 1 ? .call : .inPerson)
            reload()
            selection = .session(info.id)
            enqueue(info.id)
        } catch {
            errorMessage = "Import failed: \(error.localizedDescription)"
        }
    }
}

/// Rounds a level to the steps a meter can show (2 dB), so idle noise does not cause redraws.
func quantizedLevel(_ v: Float) -> Float {
    guard v > 1e-6 else { return 0 }
    let db = (20 * log10(v) / 2).rounded() * 2
    return db < -60 ? 0 : pow(10, db / 20)
}
