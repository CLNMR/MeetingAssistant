import AppKit
import MeetingAssistantCore
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
    @AppStorage("dismissedSplitHints") private var dismissedHints = ""
    private enum EditKind: Identifiable {
        case trim(start: Double?, end: Double?), split(Pause?)
        var id: String {
            switch self {
            case .trim(let a, let b): return "trim-\(a ?? -1)-\(b ?? -1)"
            case .split(let p): return "split-\(p?.start ?? -1)"
            }
        }
    }

    private var pauses: [Pause] {
        guard let transcript else { return [] }
        return Pauses.find(in: transcript, duration: info.durationSec)
    }

    init(info: SessionInfo) {
        self.info = info
        _player = StateObject(wrappedValue: SessionPlayer(sessionID: info.id, tracks: info.tracks))
    }

    var body: some View {
        VStack(spacing: 0) {
            header
            Divider().overlay(Aya.border)
            if let p = state.progress[info.id] {
                VStack(spacing: 12) {
                    ProgressView(value: p.fraction) { Text(p.message).font(.system(size: 13)) }.tint(Aya.primary)
                    Button("Cancel") { state.cancelProcessing() }.buttonStyle(AyaButtonStyle(kind: .secondary))
                }
                .padding(40)
                Spacer()
            } else if let t = transcript {
                let pauses = pauses
                RecordingTimeline(duration: info.durationSec, turns: t.turns, pauses: pauses, player: player)
                    .padding(.horizontal, 20).padding(.bottom, 10)
                if let hint = pauses.filter(\.suggestsSplit).max(by: { $0.duration < $1.duration }),
                   !dismissedHints.split(separator: ",").contains(Substring(info.id)) {
                    splitHint(hint)
                }
                Divider().overlay(Aya.border)
                speakerBar(t)
                Divider().overlay(Aya.border)
                transcriptList(t, pauses: pauses)
            } else {
                emptyState
            }
        }
        .onAppear { title = info.title; transcript = SessionStore.transcript(info.id) }
        .onChange(of: info.status) { _, _ in transcript = SessionStore.transcript(info.id) }
        .onDisappear { player.pause() }
        .sheet(item: $editing) { kind in
            switch kind {
            case .trim(let start, let end):
                TrimSheet(info: info, playhead: player.currentTime,
                          lastSpeech: transcript?.turns.map(\.end).max().map { min(info.durationSec, $0 + 2) },
                          initialStart: start, initialEnd: end)
            case .split(let pause): SplitSheet(info: info, playhead: player.currentTime, pause: pause)
            }
        }
        .alert("Rename speaker", isPresented: Binding(get: { renaming != nil }, set: { if !$0 { renaming = nil } })) {
            TextField("Name", text: $newName)
            Button("Save") { if let s = renaming { rename(s, to: newName) }; renaming = nil }
            Button("Cancel", role: .cancel) { renaming = nil }
        } message: {
            Text("MeetingAssistant remembers this voice and uses the name automatically in future meetings.")
        }
    }

    // MARK: Header with playback

    private var header: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(alignment: .center, spacing: 14) {
                Button { player.toggle() } label: {
                    Image(systemName: player.isPlaying ? "pause.fill" : "play.fill")
                        .font(.system(size: 14, weight: .bold)).foregroundStyle(.white)
                        .frame(width: 38, height: 38)
                        .background(Aya.brand, in: Circle())
                        .shadow(color: Aya.brandStart.opacity(0.3), radius: 4, y: 1)
                }
                .buttonStyle(.plain)
                .keyboardShortcut(.space, modifiers: [])
                .help(player.isPlaying ? "Pause" : "Play")
                VStack(alignment: .leading, spacing: 3) {
                    TextField("Title", text: $title).textFieldStyle(.plain).font(.system(size: 20, weight: .semibold))
                        .onSubmit { state.rename(info.id, to: title) }
                    HStack(spacing: 8) {
                        Label(info.mode.title, systemImage: modeIcon(info.mode)).labelStyle(AyaLabelStyle())
                        Text(info.startedAt, format: .dateTime.weekday().day().month().year().hour().minute())
                        if let t = transcript { Text("· \(t.speakers.count) speakers") }
                        Text("· \(Exporter.clock(player.currentTime)) / \(Exporter.clock(info.durationSec))").monospacedDigit()
                    }
                    .font(.system(size: 12)).foregroundStyle(Aya.textMuted)
                }
                Spacer()
                if FileManager.default.fileExists(atPath: AssistRecord.url(info.id).path) {
                    Button { state.assist.openReview(info.id) } label: { Label("Assist Recap", systemImage: "sparkles") }
                        .buttonStyle(AyaButtonStyle(kind: .agent, pill: true))
                        .help("Draft the recap and CRM updates for this meeting and upload them to Aya")
                        .disabled(state.assist.phase == .running)
                }
            }
            HStack(spacing: 8) {
                TextField("Search transcript", text: $search).textFieldStyle(AyaFieldStyle(icon: "magnifyingglass"))
                    .frame(maxWidth: 280)
                Spacer()
                actions
            }
        }
        .padding(.horizontal, 20).padding(.vertical, 14)
    }

    private var emptyState: some View {
        VStack(spacing: 14) {
            Spacer()
            Image(systemName: info.status == .failed ? "exclamationmark.triangle" : "text.bubble").font(.system(size: 36)).foregroundStyle(Aya.textSubtle)
            Text(info.status == .failed ? "Transcription failed." : (info.status == .queued ? "Waiting to be transcribed…" : "Not transcribed yet."))
                .font(.system(size: 14)).foregroundStyle(Aya.textMuted)
            Button("Transcribe Now") { state.enqueue(info.id) }.buttonStyle(AyaButtonStyle(kind: .primary))
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
                            Circle().fill(speakerColor(s.id, isMe: s.isMe)).frame(width: 8, height: 8)
                            Text(s.label).foregroundStyle(Aya.text)
                            Text(talkShare(t, s.id)).foregroundStyle(Aya.textMuted)
                        }
                        .font(.system(size: 12, weight: .medium))
                    }
                    .menuStyle(.borderlessButton)
                    .menuIndicator(.hidden)
                    .fixedSize()
                    .padding(.horizontal, 11).padding(.vertical, 5)
                    .background(Aya.card, in: Capsule())
                    .overlay(Capsule().strokeBorder(Aya.border, lineWidth: 1))
                }
                Text("Click a speaker to rename or merge. Right-click a line to change its speaker.")
                    .font(.system(size: 11)).foregroundStyle(Aya.textSubtle)
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

    private enum Row: Identifiable {
        case turn(Turn), pause(Pause)
        var id: String {
            switch self {
            case .turn(let t): return "t\(t.id)"
            case .pause(let p): return "p\(p.start)"
            }
        }
    }

    /// Turns with the pause markers placed where each pause begins (hidden while searching).
    private func rows(_ turns: [Turn], pauses: [Pause], searching: Bool) -> [Row] {
        guard !searching, !pauses.isEmpty else { return turns.map(Row.turn) }
        var out: [Row] = []
        var pending = pauses.sorted { $0.start < $1.start }[...]
        for turn in turns {
            while let p = pending.first, p.start <= turn.start { out.append(.pause(p)); pending.removeFirst() }
            out.append(.turn(turn))
        }
        out += pending.map(Row.pause)
        return out
    }

    private func transcriptList(_ t: Transcript, pauses: [Pause]) -> some View {
        let q = search.trimmingCharacters(in: .whitespaces).lowercased()
        let turns = q.isEmpty ? t.turns : t.turns.filter { $0.text.lowercased().contains(q) || t.label(for: $0.speaker).lowercased().contains(q) }
        let current = t.turns.last { $0.start <= player.currentTime + 0.05 }?.id
        return ScrollViewReader { proxy in
            List(rows(turns, pauses: pauses, searching: !q.isEmpty)) { row in
                switch row {
                case .turn(let turn):
                    TurnRow(turn: turn, speaker: t.speakers.first { $0.id == turn.speaker },
                            isCurrent: turn.id == current && player.isPlaying,
                            onPlay: { player.seek(turn.start); if !player.isPlaying { player.play(from: turn.start) } })
                        .id(turn.id)
                        .contextMenu { turnMenu(t, turn) }
                case .pause(let p):
                    PauseRow(pause: p, onPlay: { player.seek(p.start); if !player.isPlaying { player.play(from: p.start) } },
                             onAction: { act(on: p) })
                }
            }
            .listStyle(.plain)
            .scrollContentBackground(.hidden)
            .onChange(of: current) { _, id in
                if player.isPlaying, let id, q.isEmpty { withAnimation { proxy.scrollTo(id, anchor: .center) } }
            }
        }
    }

    /// The pause's own action: split for a pause in the middle, trim at the start or end.
    private func act(on p: Pause) {
        player.pause()
        switch p.kind {
        case .inner: editing = .split(p)
        case .leading: editing = .trim(start: max(0, p.end - Pauses.margin), end: nil)
        case .trailing: editing = .trim(start: nil, end: p.keepUntil)
        }
    }

    private func splitHint(_ p: Pause) -> some View {
        HStack(spacing: 12) {
            Image(systemName: "scissors").font(.system(size: 14, weight: .semibold)).foregroundStyle(Aya.warning)
                .frame(width: 30, height: 30)
                .background(Aya.warning.opacity(0.14), in: RoundedRectangle(cornerRadius: Aya.radiusControl, style: .continuous))
            VStack(alignment: .leading, spacing: 2) {
                Text("Two meetings in one recording?").font(.system(size: 13, weight: .semibold))
                Text("No conversation for \(pauseLength(p.duration)), from \(Exporter.clock(p.start)) to \(Exporter.clock(p.end)).")
                    .font(.system(size: 12)).foregroundStyle(Aya.textMuted)
            }
            Spacer()
            Button("Play") { player.seek(p.end - Pauses.margin); if !player.isPlaying { player.play(from: p.end - Pauses.margin) } }
                .buttonStyle(AyaButtonStyle(kind: .ghost, size: .small))
                .help("Listen to where speech starts again")
            Button("Dismiss") { dismissedHints = (dismissedHints.split(separator: ",").map(String.init) + [info.id]).joined(separator: ",") }
                .buttonStyle(AyaButtonStyle(kind: .secondary, size: .small))
            Button { act(on: p) } label: { Label("Split Here…", systemImage: "scissors") }
                .buttonStyle(AyaButtonStyle(kind: .primary, size: .small))
                .disabled(state.progress[info.id] != nil)
        }
        .padding(12)
        .ayaSurface(radius: Aya.radiusContainer, fill: Aya.warning.opacity(0.06), shadow: false)
        .padding(.horizontal, 20).padding(.bottom, 12)
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

    @ViewBuilder private var actions: some View {
        HStack(spacing: 8) {
            Menu {
                Button("Copy as Markdown") { export(.markdown, toClipboard: true) }
                Button("Copy as Plain Text") { export(.text, toClipboard: true) }
                Divider()
                ForEach(ExportFormat.allCases) { f in Button("Save as \(f.title)…") { export(f, toClipboard: false) } }
            } label: { Label("Export", systemImage: "square.and.arrow.up") }
            .menuStyle(.button).buttonStyle(AyaButtonStyle(kind: .secondary)).menuIndicator(.hidden).fixedSize()
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
            .menuStyle(.button).buttonStyle(AyaButtonStyle(kind: .secondary)).menuIndicator(.hidden).fixedSize()
            .disabled(state.progress[info.id] != nil)

            Menu {
                Button("Trim…") { player.pause(); editing = .trim(start: nil, end: nil) }
                Button("Split…") { player.pause(); editing = .split(nil) }
            } label: { Label("Edit", systemImage: "scissors") }
            .menuStyle(.button).buttonStyle(AyaButtonStyle(kind: .secondary)).menuIndicator(.hidden).fixedSize()
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
            Button(action: onPlay) { Text(Exporter.clock(turn.start)).foregroundStyle(Aya.primary) }
                .buttonStyle(.plain).font(.system(size: 11).monospacedDigit())
                .help("Play from here")
            Text(speaker?.label ?? turn.speaker)
                .font(.system(size: 13, weight: .semibold))
                .foregroundStyle(speakerColor(turn.speaker, isMe: speaker?.isMe ?? false))
                .frame(width: 110, alignment: .leading)
            Text(turn.text).font(.system(size: 14)).foregroundStyle(Aya.text).lineSpacing(2)
                .textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading)
        }
        .padding(.vertical, 5).padding(.horizontal, 8)
        .listRowSeparator(.hidden)
        .listRowBackground(RoundedRectangle(cornerRadius: Aya.radiusControl).fill(isCurrent ? Aya.primary.opacity(0.10) : Color.clear).padding(.horizontal, 8))
    }
}

func pauseLength(_ d: Double) -> String {
    d < 120 ? "\(Int(d.rounded())) s" : "\(Int((d / 60).rounded())) min"
}

/// Marker between transcript lines where nobody spoke for a while.
struct PauseRow: View {
    let pause: Pause
    let onPlay: () -> Void
    let onAction: () -> Void

    private var text: String {
        switch pause.kind {
        case .leading: return "Quiet for the first \(pauseLength(pause.duration))"
        case .trailing: return "Quiet for the last \(pauseLength(pause.duration))"
        case .inner: return "Pause · \(pauseLength(pause.duration))"
        }
    }
    private var action: (String, String) {
        switch pause.kind {
        case .inner: return ("Split Here…", "scissors")
        case .leading: return ("Trim Start…", "arrow.right.to.line")
        case .trailing: return ("Trim End…", "arrow.left.to.line")
        }
    }
    private var tint: Color { pause.suggestsSplit || pause.kind != .inner ? Aya.warning : Aya.textMuted }

    var body: some View {
        HStack(spacing: 10) {
            DashLine().stroke(tint.opacity(0.5), style: StrokeStyle(lineWidth: 1, dash: [4, 3])).frame(height: 1)
            Button(action: onPlay) { Text(Exporter.clock(pause.start)).foregroundStyle(Aya.primary) }
                .buttonStyle(.plain).font(.system(size: 11).monospacedDigit())
                .help("Play from here")
            Label(text, systemImage: "pause.circle").labelStyle(AyaLabelStyle())
                .font(.system(size: 12, weight: .medium)).foregroundStyle(tint)
            Text("\(Exporter.clock(pause.start)) – \(Exporter.clock(pause.end))").font(.system(size: 11).monospacedDigit())
                .foregroundStyle(Aya.textSubtle)
            Button(action: onAction) { Label(action.0, systemImage: action.1) }
                .buttonStyle(AyaButtonStyle(kind: .secondary, size: .small))
            DashLine().stroke(tint.opacity(0.5), style: StrokeStyle(lineWidth: 1, dash: [4, 3])).frame(height: 1)
        }
        .padding(.vertical, 8).padding(.horizontal, 8)
        .listRowSeparator(.hidden)
        .listRowBackground(Color.clear)
    }
}

private struct DashLine: Shape {
    func path(in r: CGRect) -> Path { Path { p in p.move(to: CGPoint(x: r.minX, y: r.midY)); p.addLine(to: CGPoint(x: r.maxX, y: r.midY)) } }
}

/// Overview of the whole recording: where people spoke, the long pauses, and the playhead.
/// Click or drag to jump.
struct RecordingTimeline: View {
    let duration: Double
    let turns: [Turn]
    let pauses: [Pause]
    @ObservedObject var player: SessionPlayer

    /// Speech merged into blocks, so a long meeting needs only a few hundred shapes.
    private var blocks: [(Double, Double)] {
        var out: [(Double, Double)] = []
        for t in turns.sorted(by: { $0.start < $1.start }) {
            if let last = out.last, t.start - last.1 < max(2, duration / 600) { out[out.count - 1].1 = max(last.1, t.end) }
            else { out.append((t.start, t.end)) }
        }
        return out
    }

    var body: some View {
        GeometryReader { g in
            let w = g.size.width, h = g.size.height
            let scale = w / CGFloat(max(duration, 1))
            ZStack(alignment: .topLeading) {
                RoundedRectangle(cornerRadius: Aya.radiusTag, style: .continuous).fill(Aya.muted.opacity(0.55))
                ForEach(Array(pauses.enumerated()), id: \.offset) { _, p in
                    let pw = max(2, CGFloat(p.duration) * scale)
                    Rectangle().fill(Aya.warning.opacity(p.suggestsSplit ? 0.22 : 0.12))
                        .overlay(alignment: .center) {
                            if pw > 44 {
                                Text(pauseLength(p.duration)).font(.system(size: 10, weight: .semibold)).foregroundStyle(Aya.warning)
                                    .lineLimit(1)
                            }
                        }
                        .frame(width: pw, height: h)
                        .offset(x: CGFloat(p.start) * scale)
                        .help("Pause \(Exporter.clock(p.start)) – \(Exporter.clock(p.end))")
                }
                ForEach(Array(blocks.enumerated()), id: \.offset) { _, b in
                    RoundedRectangle(cornerRadius: 1).fill(Aya.primary.opacity(0.6))
                        .frame(width: max(1, CGFloat(b.1 - b.0) * scale), height: h * 0.45)
                        .offset(x: CGFloat(b.0) * scale, y: h * 0.275)
                }
                Rectangle().fill(Aya.text).frame(width: 2, height: h + 4)
                    .offset(x: min(w - 2, max(0, CGFloat(player.currentTime) * scale - 1)), y: -2)
            }
            .clipShape(RoundedRectangle(cornerRadius: Aya.radiusTag, style: .continuous))
            .contentShape(Rectangle())
            .gesture(DragGesture(minimumDistance: 0).onChanged { v in
                player.seek(min(max(0, Double(v.location.x / scale)), duration))
            })
        }
        .frame(height: 22)
        .help("Speech (blue) and long pauses (amber). Click to jump.")
    }
}
