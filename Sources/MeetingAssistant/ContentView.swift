import MeetingAssistantCore
import SwiftUI

struct ContentView: View {
    @EnvironmentObject var state: AppState

    var body: some View {
        HStack(spacing: 8) {
            AyaRail()
            Group {
                switch state.appMode {
                case .scribe: scribe
                case .assist: AssistHomeView()
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
        .padding([.horizontal, .bottom], 8)
        .padding(.top, 2)
        .ayaPage()
        .alert("MeetingAssistant", isPresented: Binding(get: { state.errorMessage != nil },
                                                     set: { if !$0 { state.errorMessage = nil } })) {
            Button("OK") { state.errorMessage = nil }
            if state.errorMessage?.contains("System Settings") == true {
                Button("Open System Settings") {
                    NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Microphone")!)
                }
            }
        } message: {
            Text(state.errorMessage ?? "")
        }
    }

    private var scribe: some View {
        HStack(spacing: 8) {
            RecordingsPanel()
                .frame(width: 290)
            Group {
                switch state.selection {
                case .session(let id):
                    if let info = state.sessions.first(where: { $0.id == id }) {
                        SessionDetailView(info: info).id("\(id)-\(info.durationSec)")
                    } else {
                        RecorderView()
                    }
                default:
                    RecorderView()
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .ayaSurface()
            .clipShape(RoundedRectangle(cornerRadius: Aya.radiusSurface, style: .continuous))
        }
    }
}

// MARK: - Rail

/// Aya's slim sidebar: the logo tile on top, one icon per mode, settings and the
/// person's initials at the bottom.
struct AyaRail: View {
    @EnvironmentObject var state: AppState

    var body: some View {
        VStack(spacing: 6) {
            RailLogo(assist: state.assist).padding(.bottom, 8)
            modeButton(.scribe, help: "Scribe: record and transcribe on this Mac (⌘1)")
            modeButton(.assist, help: "Assist: live meeting assistant with Aya and Claude (⌘2)")
            Spacer()
            Button { state.floating.toggle() } label: { Label("Floating Live Transcript", systemImage: "pip") }
                .buttonStyle(AyaIconButtonStyle(bordered: false))
                .help("Floating live transcript (⇧⌘L)")
            SettingsLink { Label("Settings", systemImage: "gearshape") }
                .buttonStyle(AyaIconButtonStyle(bordered: false))
                .help("Settings (⌘,)")
            Text(initials)
                .font(.system(size: 11, weight: .semibold))
                .foregroundStyle(Aya.primary)
                .frame(width: 28, height: 28)
                .background(Aya.primary.opacity(0.12), in: Circle())
                .padding(.top, 4)
                .help(state.prefs.myName)
        }
        .padding(.vertical, 10)
        .frame(width: 48)
        .frame(maxHeight: .infinity)
        .ayaSurface(radius: Aya.radiusOverlay, fill: Aya.rail)
    }

    private var initials: String {
        let parts = state.prefs.myName.split(separator: " ").prefix(2)
        let s = parts.compactMap(\.first).map(String.init).joined().uppercased()
        return s.isEmpty ? "ME" : s
    }

    private func modeButton(_ m: AppMode, help: String) -> some View {
        Button { state.appMode = m } label: { Label(m.title, systemImage: m.icon) }
            .buttonStyle(AyaIconButtonStyle(bordered: false, active: state.appMode == m))
            .overlay(alignment: .topTrailing) {
                if m == .scribe && state.isRecording && state.appMode != .scribe || m == .assist && state.assist.phase == .running && state.appMode != .assist {
                    Circle().fill(Color.red).frame(width: 7, height: 7).offset(x: 1, y: -1)
                }
            }
            .help(help)
    }
}

/// The logo opens the connected Aya organization in the browser.
private struct RailLogo: View {
    @ObservedObject var assist: AssistController
    var body: some View {
        if let url = assist.orgURL {
            Link(destination: url) { AyaLogoTile(size: 32) }
                .buttonStyle(.plain)
                .pointerStyle(.link)
                .help("Open your organization in Aya")
        } else {
            AyaLogoTile(size: 32).help("Aya is not connected yet. Connect it in Settings › Assist.")
        }
    }
}

// MARK: - Recordings list

struct RecordingsPanel: View {
    @EnvironmentObject var state: AppState
    @State private var filter = ""
    @FocusState private var listFocused: Bool

    private var items: [SessionInfo] {
        let all = state.sessions.filter { $0.id != state.recordingSessionID }
        let f = filter.trimmingCharacters(in: .whitespaces)
        return f.isEmpty ? all : all.filter { $0.title.localizedCaseInsensitiveContains(f) }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Text("Recordings").font(.system(size: 17, weight: .semibold))
                Spacer()
                Button { state.importAudio() } label: { Label("Import Audio", systemImage: "square.and.arrow.down") }
                    .buttonStyle(AyaIconButtonStyle())
                    .help("Transcribe an existing audio file (⌘I)")
            }
            newRecordingRow
            TextField("Search", text: $filter).textFieldStyle(AyaFieldStyle(icon: "magnifyingglass"))
            ScrollViewReader { proxy in
                ScrollView {
                    LazyVStack(spacing: 2) {
                        ForEach(items) { s in
                            SessionRow(info: s, progress: state.progress[s.id], selected: state.selection == .session(s.id))
                                .id(s.id)
                                .onTapGesture { state.selection = .session(s.id); listFocused = true }
                                .contextMenu {
                                    Button("Transcribe Again") { state.enqueue(s.id) }
                                    Button("Show in Finder") { NSWorkspace.shared.activateFileViewerSelecting([AppPaths.session(s.id)]) }
                                    Divider()
                                    Button("Delete", role: .destructive) { state.delete(s.id) }
                                }
                        }
                        if items.isEmpty {
                            Text(filter.isEmpty ? "No recordings yet." : "Nothing matches.")
                                .font(.system(size: 13)).foregroundStyle(Aya.textMuted).padding(.top, 20)
                        }
                    }
                }
                .scrollIndicators(.automatic)
                .focusable()
                .focusEffectDisabled()
                .focused($listFocused)
                .onKeyPress(.downArrow) { move(1, proxy); return .handled }
                .onKeyPress(.upArrow) { move(-1, proxy); return .handled }
            }
        }
        .padding(14)
        .frame(maxHeight: .infinity, alignment: .top)
        .ayaSurface()
    }

    private func move(_ d: Int, _ proxy: ScrollViewProxy) {
        guard !items.isEmpty else { return }
        let i: Int
        if case .session(let id) = state.selection, let cur = items.firstIndex(where: { $0.id == id }) {
            i = max(0, min(items.count - 1, cur + d))
        } else {
            i = d > 0 ? 0 : items.count - 1
        }
        state.selection = .session(items[i].id)
        proxy.scrollTo(items[i].id)
    }

    private var newRecordingRow: some View {
        let on = state.selection == .recorder || state.selection == nil
        return Button { state.selection = .recorder } label: {
            HStack(spacing: 10) {
                ZStack {
                    Circle().fill(state.isRecording ? Color.red : Aya.primary).frame(width: 26, height: 26)
                    Image(systemName: state.isRecording ? "waveform" : "plus").font(.system(size: 12, weight: .bold)).foregroundStyle(.white)
                }
                VStack(alignment: .leading, spacing: 1) {
                    Text(state.isRecording ? "Recording…" : "New Recording").font(.system(size: 13, weight: .semibold))
                    Text(state.isRecording ? "\(Exporter.clock(state.elapsed)) · \(state.recordingMode.title)" : "⇧⌘R")
                        .font(.system(size: 11).monospacedDigit()).foregroundStyle(Aya.textMuted)
                }
                Spacer()
            }
            .padding(8)
            .background(on ? Aya.muted : .clear, in: RoundedRectangle(cornerRadius: Aya.radiusContainer, style: .continuous))
            .overlay(RoundedRectangle(cornerRadius: Aya.radiusContainer, style: .continuous).strokeBorder(Aya.border, lineWidth: 1))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }
}

struct SessionRow: View {
    let info: SessionInfo
    let progress: JobProgress?
    var selected = false
    @State private var hovering = false

    var body: some View {
        VStack(alignment: .leading, spacing: 3) {
            HStack(spacing: 6) {
                Text(info.title).font(.system(size: 13, weight: selected ? .semibold : .medium)).lineLimit(1)
                Spacer(minLength: 4)
                StatusBadge(status: info.status)
            }
            HStack(spacing: 5) {
                Image(systemName: modeIcon(info.mode)).font(.system(size: 9))
                Text(info.startedAt, format: .dateTime.day().month(.abbreviated).hour().minute())
                Text("· \(Exporter.clock(info.durationSec))")
            }
            .font(.system(size: 11)).foregroundStyle(Aya.textMuted)
            if let progress {
                ProgressView(value: progress.fraction).controlSize(.small).tint(Aya.primary)
            }
        }
        .padding(.horizontal, 10).padding(.vertical, 7)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(selected ? Aya.primary.opacity(0.10) : (hovering ? Aya.hover.opacity(0.6) : .clear),
                    in: RoundedRectangle(cornerRadius: Aya.radiusControl, style: .continuous))
        .contentShape(Rectangle())
        .onHover { hovering = $0 }
    }
}

func modeIcon(_ m: MeetingMode) -> String {
    switch m {
    case .call: return "phone"
    case .inPerson: return "person.3"
    case .hybrid: return "person.2.wave.2"
    }
}

struct StatusBadge: View {
    let status: SessionInfo.Status
    var body: some View {
        switch status {
        case .done: Image(systemName: "checkmark.circle.fill").foregroundStyle(Aya.success).font(.system(size: 11))
        case .failed: Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(Aya.warning).font(.system(size: 11))
        case .queued: Image(systemName: "clock").foregroundStyle(Aya.textSubtle).font(.system(size: 11))
        case .transcribing: Image(systemName: "waveform").foregroundStyle(Aya.primary).font(.system(size: 11))
        case .recording: Image(systemName: "record.circle").foregroundStyle(.red).font(.system(size: 11))
        case .recorded: Image(systemName: "circle.dashed").foregroundStyle(Aya.textSubtle).font(.system(size: 11))
        }
    }
}

/// Stable, distinguishable colour per speaker.
func speakerColor(_ id: String, isMe: Bool) -> Color {
    if isMe { return Aya.primary }
    let palette: [Color] = [.orange, .purple, .green, .pink, .teal, .brown, .indigo, .mint, .red, .cyan]
    var h: UInt64 = 1469598103934665603
    for b in id.utf8 { h = (h ^ UInt64(b)) &* 1099511628211 }
    return palette[Int(h % UInt64(palette.count))]
}

struct LevelMeter: View {
    let label: String
    let icon: String
    let level: Float
    var body: some View {
        HStack(spacing: 8) {
            Image(systemName: icon).frame(width: 18).foregroundStyle(Aya.textMuted)
            Text(label).frame(width: 90, alignment: .leading).foregroundStyle(Aya.textMuted)
            GeometryReader { g in
                ZStack(alignment: .leading) {
                    Capsule().fill(Aya.muted)
                    // Map RMS to a perceptual 0...1 scale (-60 dB ... 0 dB).
                    let db = 20 * log10(max(Double(level), 1e-6))
                    let v = max(0, min(1, (db + 60) / 60))
                    Capsule().fill(v > 0.9 ? Aya.destructive : Aya.agent).frame(width: g.size.width * v)
                        .animation(.linear(duration: 0.1), value: v)
                }
            }
            .frame(height: 8)
        }
        .font(.system(size: 13))
    }
}
