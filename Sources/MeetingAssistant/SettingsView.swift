import AVFoundation
import CoreAudio
import MeetingAssistantCore
import SwiftUI

struct SettingsView: View {
    @EnvironmentObject var state: AppState
    var body: some View {
        TabView {
            GeneralSettings().tabItem { Label("General", systemImage: "gearshape") }
            DictationSettings().tabItem { Label("Dictation", systemImage: "keyboard") }
            VocabularySettings().tabItem { Label("Vocabulary", systemImage: "character.book.closed") }
            VoiceSettings().tabItem { Label("Voices", systemImage: "person.wave.2") }
            ModelSettings().tabItem { Label("Models", systemImage: "cpu") }
            AssistSettingsView(assist: state.assist).tabItem { Label("Assist", systemImage: "sparkles") }
        }
        .environmentObject(state)
        .scrollContentBackground(.hidden)
        .background(Aya.page)
        .tint(Aya.primary)
        .frame(width: 620, height: 560)
    }
}

struct GeneralSettings: View {
    @EnvironmentObject var state: AppState
    var body: some View {
        Form {
            TextField("Your name", text: $state.prefs.myName, prompt: Text("Me"))
            LabeledContent("Default meeting type") {
                AyaSelect(selection: $state.prefs.defaultMode, options: MeetingMode.allCases.map { ($0, $0.title) }).frame(maxWidth: 240)
            }
            Toggle("Show live transcript while recording", isOn: $state.prefs.liveTranscription)
            Toggle("Create full transcript automatically after recording", isOn: $state.prefs.autoTranscribe)
            Toggle("Hide the floating transcript and the dictation overlay from screen sharing", isOn: $state.prefs.hideFromScreenSharing)
            Text("Others don’t see them when you share your screen. Some apps on recent macOS versions may still show them.")
                .font(.caption).foregroundStyle(.secondary)
            LabeledContent("Transcription engine") {
                AyaSelect(selection: $state.prefs.engine, options: ASREngineKind.allCases.map { ($0, $0.title) }).frame(maxWidth: 240)
            }
            LabeledContent("Language") {
                AyaSelect(selection: $state.prefs.language, options: TranscriptLanguage.allCases.map { ($0, $0.title) }).frame(maxWidth: 240)
            }
            Text("“Automatic” handles meetings that switch between German and English.")
                .font(.caption).foregroundStyle(.secondary)
            LabeledContent("Recordings folder") {
                Button("Show in Finder") { NSWorkspace.shared.open(AppPaths.sessions) }
            }
        }
        .formStyle(.grouped)
    }
}

struct DictationSettings: View {
    @EnvironmentObject var state: AppState
    var body: some View {
        DictationSettingsForm(dictation: state.dictation)
    }
}

private struct DictationSettingsForm: View {
    @EnvironmentObject var state: AppState
    @ObservedObject var dictation: DictationController

    var body: some View {
        Form {
            Section {
                Toggle("Push-to-talk dictation", isOn: $state.prefs.dictationEnabled)
                LabeledContent("Word for word") {
                    ShortcutRecorder(hotKey: $state.prefs.dictationHotKey,
                                     onRecordingChange: { state.suspendDictationShortcut($0) })
                }
                .disabled(!state.prefs.dictationEnabled)
                LabeledContent("Scribe+") {
                    ShortcutRecorder(hotKey: $state.prefs.scribePlusHotKey,
                                     onRecordingChange: { state.suspendDictationShortcut($0) })
                }
                .disabled(!state.prefs.dictationEnabled)
                if let e = state.hotKeyError { Text(e).font(.caption).foregroundStyle(.red) }
                Text("Hold a shortcut in any app and speak. When you let go, the text is transcribed on this Mac and pasted where your cursor is. Scribe+ also has Claude turn it into clean written text: filler words and false starts go, self-corrections are applied, punctuation is fixed.")
                    .font(.callout).foregroundStyle(.secondary)
                Text("Use a key combination (e.g. ⌃⌥Space) or modifier keys only, such as right ⌥ or right ⌥ + right ⌘. If the Scribe+ shortcut contains the other one, adding its extra key while you dictate switches to Scribe+. With modifier-only shortcuts, pressing any other key while holding them cancels the dictation, so normal shortcuts keep working.")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Section("Scribe+") {
                LabeledContent("Claude Code") {
                    if state.assist.claudeReady {
                        Label("Ready", systemImage: "checkmark.circle.fill").foregroundStyle(Aya.success)
                    } else {
                        Text("Not set up – see Settings › Assist").foregroundStyle(Aya.textMuted)
                    }
                }
                LabeledContent("Model") {
                    AyaSelect(selection: $state.prefs.scribePlusModel,
                              options: [("sonnet", "Sonnet – careful, 2–4 s"), ("haiku", "Haiku – fastest, about 1 s")]).frame(maxWidth: 260)
                }
                Toggle("Use the text around the cursor as context", isOn: $state.prefs.scribePlusContext)
                Toggle("Keep Claude ready (saves about 3 s per dictation, uses about 200 MB memory)", isOn: $state.prefs.scribePlusWarm)
                Text("Scribe+ runs Claude through your Claude subscription. The transcript goes to Anthropic, and with this option also up to 1,200 characters before and 300 after the cursor (never from password fields) and the app’s name, so the text fits the conversation, e.g. “du” or “Sie”. If Claude fails, the word-for-word text is pasted.")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Section("Pasting") {
                Toggle("Fit spaces and capitals to the text around the cursor", isOn: $state.prefs.fitToCursor)
                Toggle("Mute other sound while dictating", isOn: $state.prefs.muteWhileDictating)
                Text("The text stays on the clipboard after pasting. If you click into another field before it arrives, it is only copied, not pasted.")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Section("Permission") {
                LabeledContent("Accessibility") {
                    if dictation.isTrusted {
                        Label("Allowed", systemImage: "checkmark.circle.fill").foregroundStyle(Aya.success)
                    } else {
                        Button("Allow…") {
                            dictation.requestAccessibility()
                            dictation.watchTrust { state.applyDictationShortcut() }
                        }
                    }
                }
                Text("Needed to paste into other apps, to read the text around the cursor and to detect modifier-only shortcuts like fn + ⌃. Without it, the text is only copied to the clipboard.")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Section {
                Text("Uses Parakeet Ultra, which detects German, English and 23 other languages automatically. The microphone is the one chosen for recordings. If a transcription fails, the recording is kept for 24 hours and can be transcribed again from the menu bar menu.")
                    .font(.caption).foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
    }
}

/// Names and terms spelled the user's way, in dictations and transcripts.
struct VocabularySettings: View {
    @EnvironmentObject var state: AppState
    @State private var newTerm = ""
    @State private var newSpoken = ""
    @State private var newWritten = ""
    @State private var refreshing = false

    var body: some View {
        Form {
            Section {
                Text("Speech recognition writes what it hears, and names come out wrong most. Terms here get your spelling in dictations and in transcripts: the same letters (“hubspot”, “Hub Spot” → “HubSpot”) and, if switched on, words that sound very much alike (“Clawd” → “Claude”). Ordinary German or English words are never changed into a name.")
                    .font(.caption).foregroundStyle(.secondary)
                Toggle("Also correct words that sound alike", isOn: $state.vocabulary.fuzzy)
            }
            Section("Your terms") {
                HStack {
                    TextField("Name or term", text: $newTerm, prompt: Text("e.g. HubSpot"))
                        .onSubmit(addTerm)
                    Button("Add", action: addTerm).disabled(newTerm.trimmingCharacters(in: .whitespaces).isEmpty)
                }
                ForEach(state.vocabulary.terms, id: \.self) { t in
                    HStack {
                        Text(t)
                        Spacer()
                        Button { state.vocabulary.terms.removeAll { $0 == t } } label: { Image(systemName: "minus.circle") }
                            .buttonStyle(.plain).foregroundStyle(Aya.textMuted)
                    }
                }
            }
            Section("Replacements") {
                HStack {
                    TextField("Spoken", text: $newSpoken, prompt: Text("n acht n"))
                    Image(systemName: "arrow.right").foregroundStyle(Aya.textMuted)
                    TextField("Written", text: $newWritten, prompt: Text("n8n")).onSubmit(addReplacement)
                    Button("Add", action: addReplacement)
                        .disabled(newSpoken.trimmingCharacters(in: .whitespaces).isEmpty || newWritten.trimmingCharacters(in: .whitespaces).isEmpty)
                }
                ForEach(state.vocabulary.replacements) { r in
                    HStack {
                        Text(r.spoken).foregroundStyle(Aya.textMuted)
                        Image(systemName: "arrow.right").foregroundStyle(Aya.textSubtle)
                        Text(r.written)
                        Spacer()
                        Button { state.vocabulary.replacements.removeAll { $0 == r } } label: { Image(systemName: "minus.circle") }
                            .buttonStyle(.plain).foregroundStyle(Aya.textMuted)
                    }
                }
                Text("Always applied, whole words only; case, spaces and hyphens do not matter.")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Section("From Aya") {
                Toggle("Use names from Aya", isOn: $state.vocabulary.useAya)
                LabeledContent("Names") {
                    HStack {
                        Text(state.vocabulary.ayaTerms.isEmpty ? "None loaded" : "\(state.vocabulary.ayaTerms.count)")
                            .foregroundStyle(Aya.textMuted)
                        Button(refreshing ? "Loading…" : "Refresh") {
                            refreshing = true
                            Task { await state.assist.refreshVocabulary(); refreshing = false }
                        }
                        .disabled(refreshing || !state.assist.isConnected)
                    }
                }
                Text("Companies (without the legal form), contacts and team members with their last names, and short project names. Refreshed with the Aya lists. Stored only on this Mac.")
                    .font(.caption).foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
    }

    private func addTerm() {
        let t = newTerm.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !t.isEmpty, !state.vocabulary.terms.contains(t) else { return }
        state.vocabulary.terms.append(t)
        newTerm = ""
    }

    private func addReplacement() {
        let a = newSpoken.trimmingCharacters(in: .whitespacesAndNewlines), b = newWritten.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !a.isEmpty, !b.isEmpty else { return }
        state.vocabulary.replacements.append(.init(spoken: a, written: b))
        newSpoken = ""; newWritten = ""
    }
}

/// Collects 16 kHz samples from the microphone for voice enrollment.
@MainActor
final class EnrollmentRecorder: ObservableObject {
    @Published var seconds: Double = 0
    @Published var isRecording = false
    @Published var level: Float = 0
    private let mic = MicCapture()
    private var buffer = SampleBuffer()
    private var timer: Timer?

    func start(deviceID: AudioObjectID?) throws {
        let buf = SampleBuffer()
        buffer = buf
        let resampler = Resampler16k()
        try mic.start(deviceID: deviceID) { pcm, _ in buf.append(resampler.convert(pcm)) }
        isRecording = true
        timer = Timer.scheduledTimer(withTimeInterval: 0.1, repeats: true) { [weak self] _ in
            Task { @MainActor in
                guard let self else { return }
                let (n, tail) = buf.stats()
                self.seconds = Double(n) / 16_000
                self.level = AudioIO.rms(tail[...])
            }
        }
    }

    func stop() -> [Float] {
        mic.stop(); timer?.invalidate(); isRecording = false
        return buffer.all()
    }
}

struct VoiceSettings: View {
    @EnvironmentObject var state: AppState
    @StateObject private var rec = EnrollmentRecorder()
    @State private var voiceprint = VoiceprintStore.load()
    @State private var library = SpeakerLibrary.load()
    @State private var busy = false
    @State private var message: String?

    private let readingText = """
    Bitte lies diesen Text in normaler Lautstärke vor: Heute bespreche ich mit dem Team die nächsten Schritte im Projekt. \
    Wir schauen uns die Zahlen an, klären offene Fragen und verteilen die Aufgaben für die kommende Woche. \
    Then continue in English: we review the timeline, agree on priorities and write down the action items.
    """

    var body: some View {
        Form {
            Section("Your voice") {
                Text("MeetingAssistant recognises you by your voice. Record a voice sample once: read the text aloud for at least \(Int(VoiceprintStore.recordingSeconds)) seconds (\(Int(VoiceprintStore.minimumSpeechSeconds)) seconds of speech are needed).")
                    .font(.callout).foregroundStyle(.secondary)
                if let v = voiceprint {
                    LabeledContent("Voice sample") {
                        HStack {
                            Text(String(format: "%.0f s recorded %@", v.seconds, v.createdAt.formatted(date: .abbreviated, time: .omitted)))
                            Button("Delete") { VoiceprintStore.delete(); voiceprint = nil }
                        }
                    }
                }
                if rec.isRecording {
                    Text(readingText).font(.callout).padding(8).background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 6))
                    LevelMeter(label: String(format: "%.0f s", rec.seconds), icon: "mic", level: rec.level)
                    let minimum = VoiceprintStore.recordingSeconds
                    Button(rec.seconds < minimum ? "Keep reading… (\(Int((minimum - rec.seconds).rounded(.up))) s)" : "Finish") { finish() }
                        .disabled(rec.seconds < minimum)
                } else {
                    Button(voiceprint == nil ? "Record Voice Sample" : "Record Again") { start() }
                        .disabled(busy)
                }
                if busy { ProgressView().controlSize(.small) }
                if let message { Text(message).font(.callout).foregroundStyle(.secondary) }
            }
            Section("Remembered voices") {
                if library.entries.isEmpty {
                    Text("When you rename a speaker in a transcript, MeetingAssistant remembers the voice and names that person automatically next time.")
                        .font(.callout).foregroundStyle(.secondary)
                }
                ForEach(library.entries, id: \.name) { e in
                    HStack {
                        Text(e.name)
                        Text("from \(e.count) meeting\(e.count == 1 ? "" : "s")").foregroundStyle(.secondary)
                        Spacer()
                        Button("Forget") { library.forget(name: e.name); try? library.save() }
                    }
                }
            }
        }
        .formStyle(.grouped)
    }

    private func start() {
        message = nil
        Task {
            guard await Permissions.requestMicrophone() else { message = "Microphone access is needed."; return }
            do { try rec.start(deviceID: state.selectedDeviceID) } catch { message = error.localizedDescription }
        }
    }

    private func finish() {
        let samples = rec.stop()
        busy = true
        let name = state.prefs.myName
        Task {
            do {
                let v = try await VoiceprintStore.enroll(name: name, samples: samples)
                voiceprint = v
                message = "Saved. You will be labelled “\(name)” in in-person meetings."
            } catch {
                message = error.localizedDescription
            }
            busy = false
        }
    }
}

struct ModelSettings: View {
    @EnvironmentObject var state: AppState
    var body: some View {
        Form {
            Section("Models (all run locally)") {
                LabeledContent("Transcription", value: "Parakeet Ultra (NVIDIA, Core ML) · Qwen3-ASR 1.7B (MLX, optional)")
                LabeledContent("Speakers", value: "NVIDIA Nemotron 3 Diarization (Core ML)")
                LabeledContent("Voice matching", value: "CAM++ speaker embeddings")
                LabeledContent("Speech detection", value: "Silero VAD")
            }
            Section {
                if let p = state.modelSetup {
                    ProgressView(value: p.fraction) { Text(p.message) }
                } else {
                    Button(state.modelsReady ? "Check / Re-download Models" : "Download Models") { state.prepareModels() }
                }
                Text("Models are downloaded once from Hugging Face and cached. Audio and transcripts never leave this Mac.")
                    .font(.caption).foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
    }
}

/// Thread-safe sample accumulator written from the audio thread.
final class SampleBuffer: @unchecked Sendable {
    private var samples: [Float] = []
    private let lock = NSLock()
    func append(_ s: [Float]) { lock.lock(); samples += s; lock.unlock() }
    func stats() -> (Int, [Float]) { lock.lock(); defer { lock.unlock() }; return (samples.count, Array(samples.suffix(1600))) }
    func all() -> [Float] { lock.lock(); defer { lock.unlock() }; return samples }
}
