import MeetingScribeCore
import SwiftUI

/// A time in a recording: slider plus an editable "h:mm:ss" / "mm:ss" field.
struct TimeChooser: View {
    @Binding var value: Double
    let range: ClosedRange<Double>
    @State private var text = ""

    var body: some View {
        HStack(spacing: 10) {
            Slider(value: $value, in: range)
            TextField("mm:ss", text: $text)
                .frame(width: 80)
                .multilineTextAlignment(.trailing)
                .font(.body.monospacedDigit())
                .onSubmit(apply)
        }
        .onAppear { text = Exporter.clock(value) }
        .onChange(of: value) { _, v in text = Exporter.clock(v) }
    }

    private func apply() {
        if let t = Self.parse(text) { value = min(max(t, range.lowerBound), range.upperBound) }
        text = Exporter.clock(value)
    }

    /// "1:02:03", "62:03", "45" -> seconds.
    static func parse(_ s: String) -> Double? {
        let parts = s.split(separator: ":").map { Double($0.trimmingCharacters(in: .whitespaces)) }
        guard !parts.isEmpty, parts.count <= 3, parts.allSatisfy({ $0 != nil }) else { return nil }
        return parts.reduce(0) { $0 * 60 + $1! }
    }
}

/// Shown from the recording screen: stop now, but drop everything after a chosen time.
struct StopEarlierSheet: View {
    @EnvironmentObject var state: AppState
    @Environment(\.dismiss) private var dismiss
    @State private var keepUntil: Double = 0
    @State private var maxTime: Double = 1

    private var lastSpeech: Double? {
        guard let end = state.liveLines.last?.end else { return nil }
        return min(maxTime, end + 2)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("Stop and Remove the End").font(.title3.weight(.semibold))
            Text("Forgot to stop? Choose where the meeting ended. The recording stops now and everything after that point is deleted.")
                .foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            TimeChooser(value: $keepUntil, range: 0...maxTime)
            HStack(spacing: 8) {
                if let t = lastSpeech {
                    Button("Last speech (\(Exporter.clock(t)))") { keepUntil = t }
                }
                ForEach([5.0, 15, 30, 60], id: \.self) { m in
                    if maxTime > m * 60 { Button("−\(Int(m)) min") { keepUntil = maxTime - m * 60 } }
                }
            }
            .controlSize(.small)
            Text("Keeps \(Exporter.clock(keepUntil)) of \(Exporter.clock(maxTime)).").font(.callout).foregroundStyle(.secondary)
            HStack {
                Spacer()
                Button("Cancel", role: .cancel) { dismiss() }.keyboardShortcut(.cancelAction)
                Button("Stop and Keep \(Exporter.clock(keepUntil))") {
                    let t = keepUntil
                    dismiss()
                    Task { await state.stopRecording(keepUntil: t) }
                }
                .buttonStyle(.borderedProminent)
                .keyboardShortcut(.defaultAction)
                .disabled(keepUntil < 1)
            }
        }
        .padding(20)
        .frame(width: 520)
        .onAppear {
            maxTime = max(1, state.elapsed)
            keepUntil = lastSpeech ?? maxTime
        }
    }
}

/// Trim the start and/or end of a finished recording.
struct TrimSheet: View {
    @EnvironmentObject var state: AppState
    @Environment(\.dismiss) private var dismiss
    let info: SessionInfo
    let playhead: Double
    let lastSpeech: Double?
    @State private var start: Double = 0
    @State private var end: Double = 0

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Trim Recording").font(.title3.weight(.semibold))
            Text("Keep only the part between start and end. The rest of the audio is deleted; the transcript is cut to match.")
                .foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            row("Start", value: $start)
            row("End", value: $end)
            if let t = lastSpeech {
                Button("End after last speech (\(Exporter.clock(t)))") { end = t }.controlSize(.small)
            }
            Text("Keeps \(Exporter.clock(max(0, end - start))) of \(Exporter.clock(info.durationSec)).")
                .font(.callout).foregroundStyle(.secondary)
            HStack {
                Spacer()
                Button("Cancel", role: .cancel) { dismiss() }.keyboardShortcut(.cancelAction)
                Button("Trim") { dismiss(); state.trim(info.id, start: start, end: end) }
                    .buttonStyle(.borderedProminent)
                    .keyboardShortcut(.defaultAction)
                    .disabled(end - start < 1 || (start < 0.5 && end > info.durationSec - 0.5))
            }
        }
        .padding(20)
        .frame(width: 540)
        .onAppear { end = info.durationSec }
    }

    private func row(_ label: String, value: Binding<Double>) -> some View {
        HStack {
            Text(label).frame(width: 40, alignment: .leading)
            TimeChooser(value: value, range: 0...info.durationSec)
            Button("Playhead") { value.wrappedValue = playhead }.controlSize(.small)
                .help("Use the current playback position (\(Exporter.clock(playhead)))")
        }
    }
}

/// Split a finished recording in two.
struct SplitSheet: View {
    @EnvironmentObject var state: AppState
    @Environment(\.dismiss) private var dismiss
    let info: SessionInfo
    let playhead: Double
    @State private var at: Double = 0

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Split Recording").font(.title3.weight(.semibold))
            Text("Everything after the split point becomes a separate recording, “\(info.title) (Part 2)”. The transcript is split too.")
                .foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            HStack {
                TimeChooser(value: $at, range: 0...info.durationSec)
                Button("Playhead") { at = playhead }.controlSize(.small)
                    .help("Use the current playback position (\(Exporter.clock(playhead)))")
            }
            Text("Part 1: \(Exporter.clock(at)) · Part 2: \(Exporter.clock(max(0, info.durationSec - at)))")
                .font(.callout).foregroundStyle(.secondary)
            HStack {
                Spacer()
                Button("Cancel", role: .cancel) { dismiss() }.keyboardShortcut(.cancelAction)
                Button("Split") { dismiss(); state.split(info.id, at: at) }
                    .buttonStyle(.borderedProminent)
                    .keyboardShortcut(.defaultAction)
                    .disabled(at < 1 || info.durationSec - at < 1)
            }
        }
        .padding(20)
        .frame(width: 540)
        .onAppear { at = playhead > 1 ? playhead : info.durationSec / 2 }
    }
}
