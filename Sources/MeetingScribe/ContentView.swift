import MeetingScribeCore
import SwiftUI

struct ContentView: View {
    @EnvironmentObject var state: AppState

    var body: some View {
        NavigationSplitView {
            List(selection: $state.selection) {
                Section {
                    Label(state.isRecording ? "Recording…" : "New Recording",
                          systemImage: state.isRecording ? "record.circle.fill" : "mic.badge.plus")
                        .foregroundStyle(state.isRecording ? .red : .primary)
                        .tag(Selection.recorder)
                }
                Section("Recordings") {
                    ForEach(state.sessions.filter { $0.id != state.recordingSessionID }) { s in
                        SessionRow(info: s, progress: state.progress[s.id])
                            .tag(Selection.session(s.id))
                            .contextMenu {
                                Button("Transcribe Again") { state.enqueue(s.id) }
                                Button("Show in Finder") { NSWorkspace.shared.activateFileViewerSelecting([AppPaths.session(s.id)]) }
                                Divider()
                                Button("Delete", role: .destructive) { state.delete(s.id) }
                            }
                    }
                }
            }
            .navigationSplitViewColumnWidth(min: 230, ideal: 260)
            .toolbar {
                ToolbarItem {
                    Button { state.importAudio() } label: { Label("Import Audio", systemImage: "square.and.arrow.down") }
                        .help("Transcribe an existing audio file")
                }
            }
        } detail: {
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
        .alert("MeetingScribe", isPresented: Binding(get: { state.errorMessage != nil },
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
}

struct SessionRow: View {
    let info: SessionInfo
    let progress: JobProgress?

    var body: some View {
        VStack(alignment: .leading, spacing: 3) {
            Text(info.title).lineLimit(1)
            HStack(spacing: 6) {
                Image(systemName: modeIcon(info.mode)).font(.caption2)
                Text(info.startedAt, format: .dateTime.day().month(.abbreviated).hour().minute())
                Text("· \(Exporter.clock(info.durationSec))")
                Spacer()
                StatusBadge(status: info.status)
            }
            .font(.caption).foregroundStyle(.secondary)
            if let progress {
                ProgressView(value: progress.fraction).controlSize(.small)
            }
        }
        .padding(.vertical, 2)
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
        case .done: Image(systemName: "checkmark.circle.fill").foregroundStyle(.green)
        case .failed: Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.orange)
        case .queued: Image(systemName: "clock").foregroundStyle(.secondary)
        case .transcribing: Image(systemName: "waveform").foregroundStyle(.blue)
        case .recording: Image(systemName: "record.circle").foregroundStyle(.red)
        case .recorded: Image(systemName: "circle.dashed").foregroundStyle(.secondary)
        }
    }
}

/// Stable, distinguishable colour per speaker.
func speakerColor(_ id: String, isMe: Bool) -> Color {
    if isMe { return .accentColor }
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
            Image(systemName: icon).frame(width: 18)
            Text(label).frame(width: 90, alignment: .leading)
            GeometryReader { g in
                ZStack(alignment: .leading) {
                    Capsule().fill(.quaternary)
                    // Map RMS to a perceptual 0...1 scale (-60 dB ... 0 dB).
                    let db = 20 * log10(max(Double(level), 1e-6))
                    let v = max(0, min(1, (db + 60) / 60))
                    Capsule().fill(v > 0.9 ? Color.red : Color.green).frame(width: g.size.width * v)
                        .animation(.linear(duration: 0.1), value: v)
                }
            }
            .frame(height: 8)
        }
        .font(.callout)
    }
}
