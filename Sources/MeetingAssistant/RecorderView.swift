import MeetingAssistantCore
import SwiftUI

struct RecorderView: View {
    @EnvironmentObject var state: AppState
    @State private var stoppingEarlier = false

    var body: some View {
        VStack(spacing: 0) {
            if !state.modelsReady { ModelSetupBanner() }
            if state.isRecording {
                VStack(alignment: .leading, spacing: 18) {
                    recordingHeader
                    if state.prefs.liveTranscription {
                        LiveTranscriptView(lines: state.liveLines, mode: state.recordingMode)
                    } else {
                        Spacer()
                    }
                }
                .padding(24)
                .frame(maxWidth: 860, maxHeight: .infinity, alignment: .topLeading)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                ScrollView {
                    setup
                        .padding(28)
                        .frame(maxWidth: 760, alignment: .leading)
                        .frame(maxWidth: .infinity)
                }
            }
        }
    }

    private var setup: some View {
        VStack(alignment: .leading, spacing: 18) {
            VStack(alignment: .leading, spacing: 4) {
                AyaEyebrow(text: "Scribe")
                Text("New recording").font(.system(size: 24, weight: .semibold))
                Text("Everything is recorded and transcribed on this Mac.").font(.system(size: 13)).foregroundStyle(Aya.textMuted)
            }

            VStack(alignment: .leading, spacing: 8) {
                AyaSegmented(selection: $state.recordingMode,
                             options: MeetingMode.allCases.map { ($0, $0.title, modeIcon($0)) })
                Text(state.recordingMode.explanation).font(.system(size: 12)).foregroundStyle(Aya.textMuted)
            }

            VStack(spacing: 0) {
                AyaRow(label: "Title") {
                    TextField("", text: $state.recordingTitle, prompt: Text("Optional, e.g. Weekly with Anna"))
                        .textFieldStyle(AyaFieldStyle()).frame(maxWidth: 320)
                }
                Divider().overlay(Aya.border)
                AyaRow(label: "Microphone") {
                    AyaSelect(selection: Binding(get: { state.prefs.inputDeviceUID ?? "" },
                                                 set: { state.prefs.inputDeviceUID = $0.isEmpty ? nil : $0 }),
                              options: [("", "System default")] + state.inputDevices.map { ($0.uid, $0.name) })
                        .frame(maxWidth: 320)
                }
                Divider().overlay(Aya.border)
                AyaRow(label: "Participants") {
                    AyaSelect(selection: $state.expectedSpeakers,
                              options: [(0, "Detect automatically")] + (2...10).map { ($0, "\($0) people") })
                        .frame(maxWidth: 320)
                }
                Divider().overlay(Aya.border)
                AyaRow(label: "Language") {
                    AyaSelect(selection: $state.prefs.language,
                              options: TranscriptLanguage.allCases.map { ($0, $0.title) })
                        .frame(maxWidth: 320)
                }
                Divider().overlay(Aya.border)
                AyaRow(label: "Live transcript", hint: "Show the transcript while recording") {
                    Toggle("", isOn: $state.prefs.liveTranscription).toggleStyle(.switch).labelsHidden()
                }
            }
            .padding(.horizontal, 16).padding(.vertical, 6)
            .ayaSurface(radius: Aya.radiusContainer)
            .onAppear { state.refreshDevices() }

            hints

            HStack(spacing: 10) {
                Button { state.floating.show() } label: { Label("Floating Live Transcript", systemImage: "pip") }
                    .buttonStyle(AyaButtonStyle(kind: .secondary))
                    .help("A small always-on-top window with a live transcript that runs without recording (⇧⌘L)")
                Spacer()
                Button {
                    Task { await state.startRecording() }
                } label: {
                    Label("Start Recording", systemImage: "record.circle")
                }
                .buttonStyle(AyaButtonStyle(kind: .destructive, size: .large))
                .keyboardShortcut(.return, modifiers: [.command])
            }
        }
    }

    private var hintText: (String, String) {
        let text: String, icon: String
        switch state.recordingMode {
        case .call:
            text = "Use headphones if you can. With speakers, your mic also hears the other side; MeetingAssistant removes most of that echo, but headphones give the cleanest result."
            icon = "headphones"
        case .inPerson:
            text = VoiceprintStore.load() == nil
                ? "Tip: record a voice sample in Settings so MeetingAssistant can label you as “\(state.prefs.myName)”. For bigger rooms an external conference microphone helps a lot."
                : "Your voice sample is set up, so you will be labelled “\(state.prefs.myName)”. For bigger rooms an external conference microphone helps a lot."
            icon = "person.wave.2"
        case .hybrid:
            text = "Remote and in-room speakers are separated on both tracks. Setting the number of participants is not used in this mode."
            icon = "info.circle"
        }
        return (text, icon)
    }

    private var hints: some View {
        let (text, icon) = hintText
        return HStack(alignment: .top, spacing: 10) {
            Image(systemName: icon).foregroundStyle(Aya.primary).font(.system(size: 13))
            Text(text).font(.system(size: 12)).foregroundStyle(Aya.textMuted).fixedSize(horizontal: false, vertical: true)
        }
        .padding(12)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Aya.primary.opacity(0.06), in: RoundedRectangle(cornerRadius: Aya.radiusContainer, style: .continuous))
    }

    private var recordingHeader: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack(alignment: .center, spacing: 12) {
                AyaAgentDot(icon: "mic.fill", size: 34)
                VStack(alignment: .leading, spacing: 2) {
                    AyaEyebrow(text: "Recording · \(state.recordingMode.title)", color: Aya.agentTextMid)
                    Text(Exporter.clock(state.elapsed)).font(.system(size: 30, weight: .semibold).monospacedDigit())
                }
                AyaWaveform(level: ayaLevel(max(state.micLevel, state.systemLevel)))
                    .padding(.leading, 8)
                Spacer()
                Button("Stop Earlier…") { stoppingEarlier = true }
                    .buttonStyle(AyaButtonStyle(kind: .ghost))
                    .help("Forgot to stop? Stop now and delete everything after a chosen time.")
                Button {
                    Task { await state.stopRecording() }
                } label: {
                    Label("Stop", systemImage: "stop.fill")
                }
                .buttonStyle(AyaButtonStyle(kind: .destructive, size: .large))
                .keyboardShortcut(.return, modifiers: [.command])
            }
            .sheet(isPresented: $stoppingEarlier) { StopEarlierSheet() }
            VStack(spacing: 8) {
                LevelMeter(label: "Microphone", icon: "mic", level: state.micLevel)
                if state.recordingMode.recordsSystemAudio {
                    LevelMeter(label: "Call audio", icon: "speaker.wave.2", level: state.systemLevel)
                }
            }
            if state.prefs.autoTranscribe {
                Text("When you stop, the full transcript with speaker labels is created automatically.")
                    .font(.system(size: 12)).foregroundStyle(Aya.textMuted)
            }
        }
        .padding(16)
        .ayaAgentSurface()
    }
}

struct LiveTranscriptView: View {
    let lines: [LiveLine]
    let mode: MeetingMode

    /// Live speaker ids ("system-2") become friendly labels in order of first appearance.
    private var labels: [String: String] {
        var map: [String: String] = ["me": "Me"]
        var n = 0
        for l in lines { if let s = l.speaker, map[s] == nil { n += 1; map[s] = mode == .call && s.hasPrefix("system") ? "Remote \(n)" : "Speaker \(n)" } }
        return map
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                AyaEyebrow(text: "Live transcript", icon: "text.bubble")
                Spacer()
                Text("Preview · speaker labels follow a few seconds behind").font(.system(size: 11)).foregroundStyle(Aya.textSubtle)
            }
            ScrollViewReader { proxy in
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 8) {
                        ForEach(lines) { line in
                            HStack(alignment: .firstTextBaseline, spacing: 10) {
                                Text(Exporter.clock(line.start)).font(.system(size: 11).monospacedDigit()).foregroundStyle(Aya.textSubtle)
                                Text(line.speaker.flatMap { labels[$0] } ?? "…")
                                    .font(.system(size: 13, weight: .semibold))
                                    .foregroundStyle(line.speaker.map { speakerColor($0, isMe: $0 == "me") } ?? Aya.textSubtle)
                                    .frame(width: 80, alignment: .leading)
                                Text(line.text).font(.system(size: 14)).foregroundStyle(line.isFinal ? Aya.text : Aya.textMuted).textSelection(.enabled)
                            }
                            .id(line.id)
                        }
                    }
                    .padding(14)
                }
                .frame(minHeight: 200, maxHeight: .infinity)
                .ayaSurface(radius: Aya.radiusContainer, shadow: false)
                .onChange(of: lines.count) { _, _ in
                    if let last = lines.last { withAnimation { proxy.scrollTo(last.id, anchor: .bottom) } }
                }
            }
        }
        .frame(maxHeight: .infinity, alignment: .top)
    }
}

struct ModelSetupBanner: View {
    @EnvironmentObject var state: AppState
    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: "arrow.down.circle").font(.system(size: 20)).foregroundStyle(Aya.primary)
            VStack(alignment: .leading, spacing: 4) {
                if let p = state.modelSetup {
                    Text(p.message).font(.callout)
                    ProgressView(value: p.fraction)
                } else {
                    Text("Download the speech models once (about 1.5 GB). Everything runs on this Mac afterwards; no audio ever leaves it.")
                        .font(.callout)
                }
            }
            Spacer()
            if state.modelSetup == nil { Button("Download Models") { state.prepareModels() }.buttonStyle(AyaButtonStyle(kind: .primary)) }
        }
        .padding(12)
        .background(Aya.primary.opacity(0.08))
    }
}
