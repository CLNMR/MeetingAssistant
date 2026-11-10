import MeetingAssistantCore
import SwiftUI

@main
struct MeetingAssistantApp: App {
    @StateObject private var state = AppState()

    var body: some Scene {
        WindowGroup("MeetingAssistant", id: "main") {
            ContentView()
                .environmentObject(state)
                .frame(minWidth: 980, minHeight: 620)
        }
        .windowStyle(.hiddenTitleBar)
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
            CommandGroup(before: .sidebar) {
                Button("Scribe") { state.appMode = .scribe }.keyboardShortcut("1", modifiers: [.command])
                Button("Assist") { state.appMode = .assist }.keyboardShortcut("2", modifiers: [.command])
                Divider()
            }
        }

        Settings {
            SettingsView().environmentObject(state).tint(Aya.primary)
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
        if state.prefs.dictationEnabled {
            Divider()
            if let k = state.prefs.dictationHotKey { Text("Dictation: hold \(k.display)") }
            if let k = state.prefs.scribePlusHotKey { Text("Scribe+: hold \(k.display)") }
            DictationMenuItems(dictation: state.dictation)
        }
        if !state.progress.isEmpty {
            Divider()
            Text("Transcribing \(state.progress.count) recording(s)…")
        }
        Divider()
        Button("Floating Live Transcript") { state.floating.toggle() }
        Button("Open MeetingAssistant") {
            openWindow(id: "main")
            NSApp.activate(ignoringOtherApps: true)
        }
        Button("Quit") { NSApp.terminate(nil) }.keyboardShortcut("q")
    }
}

/// Last text and dictations whose transcription failed (kept for 24 hours).
private struct DictationMenuItems: View {
    @ObservedObject var dictation: DictationController

    var body: some View {
        if dictation.lastText != nil {
            Button("Copy Last Dictation") { dictation.copyLast() }
        }
        if !dictation.unfinished.isEmpty {
            Menu("Unfinished Dictations (\(dictation.unfinished.count))") {
                ForEach(dictation.unfinished) { item in
                    Menu(Self.label(item)) {
                        Button("Transcribe Again and Copy") { dictation.retry(item) }
                        Button("Delete") { dictation.discard(item) }
                    }
                }
            }
        }
        EmptyView().onAppear { dictation.refreshUnfinished() }
    }

    static func label(_ item: DictationRecovery.Item) -> String {
        let f = DateFormatter()
        f.dateStyle = Calendar.current.isDateInToday(item.recordedAt) ? .none : .short
        f.timeStyle = .short
        return "\(f.string(from: item.recordedAt)) · \(Exporter.clock(item.seconds))"
    }
}
