import MeetingScribeCore
import SwiftUI

@main
struct MeetingScribeApp: App {
    @StateObject private var state = AppState()

    var body: some Scene {
        WindowGroup("MeetingScribe", id: "main") {
            ContentView()
                .environmentObject(state)
                .frame(minWidth: 900, minHeight: 600)
        }
        .commands {
            CommandGroup(after: .newItem) {
                Button(state.isRecording ? "Stop Recording" : "Start Recording") {
                    Task { await state.toggleRecording() }
                }
                .keyboardShortcut("r", modifiers: [.command, .shift])
                Button("Import Audio…") { state.importAudio() }
                    .keyboardShortcut("i", modifiers: [.command])
                Button("Floating Live Transcript") { state.floating.toggle() }
                    .keyboardShortcut("l", modifiers: [.command, .shift])
            }
        }

        Settings {
            SettingsView().environmentObject(state)
        }

        MenuBarExtra {
            MenuBarContent().environmentObject(state)
        } label: {
            Image(systemName: state.isRecording ? "record.circle.fill" : "waveform")
        }
    }
}

struct MenuBarContent: View {
    @EnvironmentObject var state: AppState
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        if state.isRecording {
            Text("Recording \(Exporter.clock(state.elapsed)) · \(state.recordingMode.title)")
            Button("Stop Recording") { Task { await state.stopRecording() } }
        } else {
            ForEach(MeetingMode.allCases) { mode in
                Button("Record \(mode.title)") {
                    state.recordingMode = mode
                    Task { await state.startRecording() }
                }
            }
        }
        if state.prefs.dictationEnabled, let k = state.prefs.dictationHotKey {
            Divider()
            Text("Dictation: hold \(k.display)")
        }
        if !state.progress.isEmpty {
            Divider()
            Text("Transcribing \(state.progress.count) recording(s)…")
        }
        Divider()
        Button("Floating Live Transcript") { state.floating.toggle() }
        Button("Open MeetingScribe") {
            openWindow(id: "main")
            NSApp.activate(ignoringOtherApps: true)
        }
        Button("Quit") { NSApp.terminate(nil) }.keyboardShortcut("q")
    }
}
