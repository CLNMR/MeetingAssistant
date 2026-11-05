import MeetingScribeCore
import SwiftUI

struct RecorderView: View {
    @EnvironmentObject var state: AppState
    @State private var stoppingEarlier = false

    var body: some View {
        VStack(spacing: 0) {
            if !state.modelsReady { ModelSetupBanner() }
            if state.isRecording {
                // While recording, the live transcript fills all height below the header.
                VStack(alignment: .leading, spacing: 20) {
                    recordingHeader
                    if state.prefs.liveTranscription {
                        LiveTranscriptView(lines: state.liveLines, mode: state.recordingMode)
                    } else {
                        Spacer()
                    }
                }
                .padding(24)
                .frame(maxWidth: 820, maxHeight: .infinity, alignment: .topLeading)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                ScrollView {
                    setup
                        .padding(24)
                        .frame(maxWidth: 820, alignment: .leading)
                        .frame(maxWidth: .infinity)
                }
            }
        }
        .navigationTitle(state.isRecording ? "Recording" : "New Recording")
    }

    private var setup: some View {
        VStack(alignment: .leading, spacing: 18) {
            Picker("Meeting type", selection: $state.recordingMode) {
                ForEach(MeetingMode.allCases) { m in Label(m.title, systemImage: modeIcon(m)).tag(m) }
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            Text(state.recordingMode.explanation).foregroundStyle(.secondary)

            Form {
                TextField("Title", text: $state.recordingTitle, prompt: Text("Optional, e.g. Weekly with Anna"))
                Picker("Microphone", selection: Binding(get: { state.prefs.inputDeviceUID ?? "" },
                                                        set: { state.prefs.inputDeviceUID = $0.isEmpty ? nil : $0 })) {
                    Text("System default").tag("")
                    ForEach(state.inputDevices) { d in Text(d.name).tag(d.uid) }
                }
                Picker("Participants", selection: $state.expectedSpeakers) {
                    Text("Detect automatically").tag(0)
                    ForEach(2...10, id: \.self) { n in Text("\(n) people").tag(n) }
                }
                Picker("Language", selection: $state.prefs.language) {
                    ForEach(TranscriptLanguage.allCases) { l in Text(l.title).tag(l) }
                }
                Toggle("Show live transcript while recording", isOn: $state.prefs.liveTranscription)
            }
            .formStyle(.grouped)
            .onAppear { state.refreshDevices() }

            hints

            HStack {
                Spacer()
                Button {
                    Task { await state.startRecording() }
                } label: {
                    Label("Start Recording", systemImage: "record.circle")
                        .font(.title3.weight(.semibold))
                        .padding(.horizontal, 18).padding(.vertical, 6)
                }
                .buttonStyle(.borderedProminent)
                .tint(.red)
                .keyboardShortcut(.return, modifiers: [.command])
                Spacer()
            }
            .overlay(alignment: .leading) {
                Button { state.floating.show() } label: { Label("Floating Live Transcript", systemImage: "pip") }
                    .help("A small always-on-top window with a live transcript that runs without recording (⇧⌘L)")
            }
        }
    }

    @ViewBuilder private var hints: some View {
        switch state.recordingMode {
        case .call:
            Label("Use headphones if you can. With speakers, your mic also hears the other side; MeetingScribe removes most of that echo, but headphones give the cleanest result.",
                  systemImage: "headphones").font(.callout).foregroundStyle(.secondary)
        case .inPerson:
            Label(VoiceprintStore.load() == nil
                  ? "Tip: record a voice sample in Settings so MeetingScribe can label you as “\(state.prefs.myName)”. For bigger rooms an external conference microphone helps a lot."
                  : "Your voice sample is set up, so you will be labelled “\(state.prefs.myName)”. For bigger rooms an external conference microphone helps a lot.",
                  systemImage: "person.wave.2").font(.callout).foregroundStyle(.secondary)
        case .hybrid:
            Label("Remote and in-room speakers are separated on both tracks. Setting the number of participants is not used in this mode.",
                  systemImage: "info.circle").font(.callout).foregroundStyle(.secondary)
        }
    }

    private var recordingHeader: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack(alignment: .firstTextBaseline) {
                Circle().fill(.red).frame(width: 12, height: 12)
                    .opacity(Int(state.elapsed * 2) % 2 == 0 ? 1 : 0.35)
                Text(Exporter.clock(state.elapsed)).font(.system(size: 34, weight: .semibold, design: .monospaced))
                Text(state.recordingMode.title).foregroundStyle(.secondary)
                Spacer()
                Button {
                    Task { await state.stopRecording() }
                } label: {
                    Label("Stop", systemImage: "stop.fill").font(.title3.weight(.semibold)).padding(.horizontal, 14).padding(.vertical, 4)
                }
                .buttonStyle(.borderedProminent)
                .keyboardShortcut(.return, modifiers: [.command])
                Button("Stop Earlier…") { stoppingEarlier = true }
                    .help("Forgot to stop? Stop now and delete everything after a chosen time.")
            }
            .sheet(isPresented: $stoppingEarlier) { StopEarlierSheet() }
            LevelMeter(label: "Microphone", icon: "mic", level: state.micLevel)
            if state.recordingMode.recordsSystemAudio {
                LevelMeter(label: "Call audio", icon: "speaker.wave.2", level: state.systemLevel)
            }
            if state.prefs.autoTranscribe {
                Text("When you stop, the full transcript with speaker labels is created automatically.")
                    .font(.callout).foregroundStyle(.secondary)
            }
        }
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
            Text("Live transcript").font(.headline)
            Text("Preview only. Speaker labels appear a few seconds behind the text.").font(.caption).foregroundStyle(.secondary)
            ScrollViewReader { proxy in
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 8) {
                        ForEach(lines) { line in
                            HStack(alignment: .firstTextBaseline, spacing: 10) {
                                Text(Exporter.clock(line.start)).font(.caption.monospacedDigit()).foregroundStyle(.tertiary)
                                Text(line.speaker.flatMap { labels[$0] } ?? "…")
                                    .font(.callout.weight(.semibold))
                                    .foregroundStyle(line.speaker.map { speakerColor($0, isMe: $0 == "me") } ?? .secondary)
                                    .frame(width: 80, alignment: .leading)
                                Text(line.text).foregroundStyle(line.isFinal ? .primary : .secondary).textSelection(.enabled)
                            }
                            .id(line.id)
                        }
                    }
                    .padding(12)
                }
                .frame(minHeight: 200, maxHeight: .infinity)
                .background(.background.secondary, in: RoundedRectangle(cornerRadius: 10))
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
            Image(systemName: "arrow.down.circle").font(.title2)
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
            if state.modelSetup == nil { Button("Download Models") { state.prepareModels() } }
        }
        .padding(12)
        .background(.yellow.opacity(0.15))
    }
}
