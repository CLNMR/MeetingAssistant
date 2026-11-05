import AppKit
import MeetingScribeCore
import SwiftUI
import UniformTypeIdentifiers

struct SessionDetailView: View {
    @EnvironmentObject var state: AppState
    let info: SessionInfo
    @State private var transcript: Transcript?
    @State private var search = ""
    @State private var title = ""
    @State private var renaming: Speaker?
    @State private var newName = ""
    @StateObject private var player: SessionPlayer
    @State private var editing: EditKind?
    private enum EditKind: String, Identifiable { case trim, split; var id: String { rawValue } }

    init(info: SessionInfo) {
        self.info = info
        _player = StateObject(wrappedValue: SessionPlayer(sessionID: info.id, tracks: info.tracks))
    }

    var body: some View {
        VStack(spacing: 0) {
            header
            Divider()
            if let p = state.progress[info.id] {
                VStack(spacing: 10) {
                    ProgressView(value: p.fraction) { Text(p.message) }
                    Button("Cancel") { state.cancelProcessing() }
                }
                .padding(40)
                Spacer()
            } else if let t = transcript {
                speakerBar(t)
                Divider()
                transcriptList(t)
            } else {
                emptyState
            }
        }
        .navigationTitle(info.title)
        .searchable(text: $search, prompt: "Search transcript")
        .toolbar { toolbar }
        .onAppear { title = info.title; transcript = SessionStore.transcript(info.id) }
        .onChange(of: info.status) { _, _ in transcript = SessionStore.transcript(info.id) }
        .onDisappear { player.pause() }
        .sheet(item: $editing) { kind in
            switch kind {
            case .trim: TrimSheet(info: info, playhead: player.currentTime,
                                  lastSpeech: transcript?.turns.map(\.end).max().map { min(info.durationSec, $0 + 2) })
            case .split: SplitSheet(info: info, playhead: player.currentTime)
            }
        }
        .alert("Rename speaker", isPresented: Binding(get: { renaming != nil }, set: { if !$0 { renaming = nil } })) {
            TextField("Name", text: $newName)
            Button("Save") { if let s = renaming { rename(s, to: newName) }; renaming = nil }
            Button("Cancel", role: .cancel) { renaming = nil }
        } message: {
            Text("MeetingScribe remembers this voice and uses the name automatically in future meetings.")
        }
    }

    // MARK: Header with playback

    private var header: some View {
        HStack(spacing: 14) {
            Button { player.toggle() } label: {
                Image(systemName: player.isPlaying ? "pause.circle.fill" : "play.circle.fill").font(.system(size: 30))
            }
            .buttonStyle(.plain)
            .keyboardShortcut(.space, modifiers: [])
            VStack(alignment: .leading, spacing: 4) {
                TextField("Title", text: $title).textFieldStyle(.plain).font(.title2.weight(.semibold))
                    .onSubmit { state.rename(info.id, to: title) }
                HStack(spacing: 8) {
                    Label(info.mode.title, systemImage: modeIcon(info.mode))
                    Text(info.startedAt, format: .dateTime.weekday().day().month().year().hour().minute())
                    if let t = transcript { Text("· \(t.speakers.count) speakers") }
                }
                .font(.callout).foregroundStyle(.secondary)
            }
            Spacer()
            Text("\(Exporter.clock(player.currentTime)) / \(Exporter.clock(info.durationSec))")
                .font(.callout.monospacedDigit()).foregroundStyle(.secondary)
        }
        .padding(.horizontal, 20).padding(.vertical, 12)
    }

    private var emptyState: some View {
        VStack(spacing: 14) {
            Spacer()
            Image(systemName: info.status == .failed ? "exclamationmark.triangle" : "text.bubble").font(.system(size: 40)).foregroundStyle(.secondary)
            Text(info.status == .failed ? "Transcription failed." : (info.status == .queued ? "Waiting to be transcribed…" : "Not transcribed yet."))
            Button("Transcribe Now") { state.enqueue(info.id) }.buttonStyle(.borderedProminent)
            Spacer()
        }
        .frame(maxWidth: .infinity)
    }

    // MARK: Speakers

    private func speakerBar(_ t: Transcript) -> some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 8) {
                ForEach(t.speakers) { s in
                    Menu {
                        Button("Rename…") { newName = s.label.hasPrefix("Speaker ") ? "" : s.label; renaming = s }
                        if !s.isMe { Button("This is me") { markMe(s) } }
                        Menu("Merge into") {
                            ForEach(t.speakers.filter { $0.id != s.id }) { o in Button(o.label) { edit { $0.merge(speaker: s.id, into: o.id) } } }
                        }
                    } label: {
                        HStack(spacing: 6) {
                            Circle().fill(speakerColor(s.id, isMe: s.isMe)).frame(width: 9, height: 9)
                            Text(s.label)
                            Text(talkShare(t, s.id)).foregroundStyle(.secondary)
                        }
                    }
                    .menuStyle(.borderlessButton)
                    .fixedSize()
                    .padding(.horizontal, 10).padding(.vertical, 5)
                    .background(.quaternary.opacity(0.6), in: Capsule())
                }
                Text("Click a speaker to rename or merge. Right-click a line to change its speaker.")
                    .font(.caption).foregroundStyle(.tertiary)
            }
            .padding(.horizontal, 20).padding(.vertical, 10)
        }
    }

    private func talkShare(_ t: Transcript, _ id: String) -> String {
        let total = t.turns.reduce(0) { $0 + ($1.end - $1.start) }
        let mine = t.turns.filter { $0.speaker == id }.reduce(0) { $0 + ($1.end - $1.start) }
        return total > 0 ? "\(Int((mine / total * 100).rounded()))%" : ""
    }

    // MARK: Transcript

    private func transcriptList(_ t: Transcript) -> some View {
        let q = search.trimmingCharacters(in: .whitespaces).lowercased()
        let turns = q.isEmpty ? t.turns : t.turns.filter { $0.text.lowercased().contains(q) || t.label(for: $0.speaker).lowercased().contains(q) }
        let current = t.turns.last { $0.start <= player.currentTime + 0.05 }?.id
        return ScrollViewReader { proxy in
            List(turns) { turn in
                TurnRow(turn: turn, speaker: t.speakers.first { $0.id == turn.speaker },
                        isCurrent: turn.id == current && player.isPlaying,
                        onPlay: { player.seek(turn.start); if !player.isPlaying { player.play(from: turn.start) } })
                    .id(turn.id)
                    .contextMenu { turnMenu(t, turn) }
            }
            .listStyle(.inset)
            .onChange(of: current) { _, id in
                if player.isPlaying, let id, q.isEmpty { withAnimation { proxy.scrollTo(id, anchor: .center) } }
            }
        }
    }

    @ViewBuilder private func turnMenu(_ t: Transcript, _ turn: Turn) -> some View {
        Menu("Change speaker to") {
            ForEach(t.speakers) { s in Button(s.label) { edit { $0.reassign(turn: turn.id, to: s.id) } } }
            Button("New speaker") { newSpeaker(for: turn) }
        }
        Button("Copy") { copy("\(t.label(for: turn.speaker)): \(turn.text)") }
    }

    private func newSpeaker(for turn: Turn) {
        edit { tr in
            let id = "manual-\(UUID().uuidString.prefix(6))"
            tr.speakers.append(Speaker(id: id, label: "Speaker \(tr.speakers.count + 1)", track: turn.track, embedding: nil, isMe: false))
            tr.reassign(turn: turn.id, to: id)
        }
    }

    // MARK: Toolbar / export

    @ToolbarContentBuilder private var toolbar: some ToolbarContent {
        ToolbarItemGroup {
            Menu {
                Button("Copy as Markdown") { export(.markdown, toClipboard: true) }
                Button("Copy as Plain Text") { export(.text, toClipboard: true) }
                Divider()
                ForEach(ExportFormat.allCases) { f in Button("Save as \(f.title)…") { export(f, toClipboard: false) } }
            } label: { Label("Export", systemImage: "square.and.arrow.up") }
            .disabled(transcript == nil)

            Menu {
                let current = info.language.flatMap(TranscriptLanguage.init(rawValue:)) ?? .auto
                ForEach(ASREngineKind.allCases) { e in
                    Section(e == .parakeetUltra ? "Parakeet Ultra (fast)" : "Qwen3-ASR (slower)") {
                        ForEach(TranscriptLanguage.allCases) { l in
                            Button {
                                state.retranscribe(info.id, engine: e, language: l)
                            } label: {
                                if l == current && e == state.prefs.engine { Label(l.title, systemImage: "checkmark") }
                                else { Text(l.title) }
                            }
                        }
                    }
                }
            } label: { Label("Transcribe Again", systemImage: "arrow.clockwise") }
            .disabled(state.progress[info.id] != nil)

            Menu {
                Button("Trim…") { player.pause(); editing = .trim }
                Button("Split…") { player.pause(); editing = .split }
            } label: { Label("Edit Recording", systemImage: "scissors") }
            .help("Trim the start or end, or split into two recordings")
            .disabled(state.progress[info.id] != nil)
        }
    }

    private func export(_ f: ExportFormat, toClipboard: Bool) {
        guard let t = transcript, let text = try? Exporter.render(t, info: info, as: f) else { return }
        if toClipboard { copy(text); return }
        let panel = NSSavePanel()
        panel.nameFieldStringValue = "\(info.title).\(f.rawValue)"
        panel.allowedContentTypes = [UTType(filenameExtension: f.rawValue) ?? .plainText]
        if panel.runModal() == .OK, let url = panel.url {
            do { try text.write(to: url, atomically: true, encoding: .utf8) }
            catch { state.errorMessage = "Could not save: \(error.localizedDescription)" }
        }
    }

    private func copy(_ s: String) {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(s, forType: .string)
    }

    // MARK: Editing

    private func edit(_ change: (inout Transcript) -> Void) {
        guard var t = transcript else { return }
        change(&t)
        transcript = t
        try? SessionStore.saveTranscript(t, id: info.id)
    }

    private func rename(_ s: Speaker, to name: String) {
        let n = name.trimmingCharacters(in: .whitespaces)
        guard !n.isEmpty else { return }
        edit { t in if let i = t.speakers.firstIndex(where: { $0.id == s.id }) { t.speakers[i].label = n } }
        // Learn the voice for future meetings (not for "me": that is the voiceprint's job).
        if let e = s.embedding, !s.isMe {
            var lib = SpeakerLibrary.load()
            lib.learn(name: n, embedding: e)
            try? lib.save()
        }
    }

    private func markMe(_ s: Speaker) {
        edit { t in
            for i in t.speakers.indices {
                let isMe = t.speakers[i].id == s.id
                if t.speakers[i].isMe && !isMe { t.speakers[i].isMe = false; t.speakers[i].label = "Speaker \(i + 1)" }
                if isMe { t.speakers[i].isMe = true; t.speakers[i].label = state.prefs.myName }
            }
        }
    }
}

struct TurnRow: View {
    let turn: Turn
    let speaker: Speaker?
    let isCurrent: Bool
    let onPlay: () -> Void

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 12) {
            Button(Exporter.clock(turn.start), action: onPlay)
                .buttonStyle(.link).font(.caption.monospacedDigit())
                .help("Play from here")
            Text(speaker?.label ?? turn.speaker)
                .font(.callout.weight(.semibold))
                .foregroundStyle(speakerColor(turn.speaker, isMe: speaker?.isMe ?? false))
                .frame(width: 110, alignment: .leading)
            Text(turn.text).textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading)
        }
        .padding(.vertical, 4)
        .listRowBackground(isCurrent ? Color.accentColor.opacity(0.12) : Color.clear)
    }
}
