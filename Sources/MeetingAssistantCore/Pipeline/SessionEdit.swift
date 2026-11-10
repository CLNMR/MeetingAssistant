import AVFoundation
import Foundation

/// Cutting recordings: trim away the start/end, or split one recording into two.
/// Audio files are rewritten; an existing transcript is cut the same way (with times
/// shifted) so speaker names and manual fixes survive without transcribing again.
public enum SessionEdit {
    public enum EditError: LocalizedError {
        case busy, invalidRange
        public var errorDescription: String? {
            switch self {
            case .busy: return "The recording is being recorded or transcribed right now."
            case .invalidRange: return "The chosen part is empty."
            }
        }
    }

    /// Keep only `start ..< end` (seconds) of the recording.
    @discardableResult
    public static func trim(_ id: String, start: Double, end: Double) throws -> SessionInfo {
        var info = try JSONIO.read(SessionInfo.self, from: AppPaths.info(id))
        guard ![.recording, .queued, .transcribing].contains(info.status) else { throw EditError.busy }
        let s = max(0, start), e = min(end, info.durationSec)
        guard e - s >= 1 else { throw EditError.invalidRange }

        for t in info.tracks {
            let url = AppPaths.track(id, t)
            guard FileManager.default.fileExists(atPath: url.path) else { continue }
            // Same container as the original (a .wav from Windows stays a .wav).
            let tmp = url.deletingLastPathComponent().appendingPathComponent(".trim-\(t.rawValue).\(url.pathExtension)")
            try copyAudio(from: url, to: tmp, start: s, end: e)
            _ = try FileManager.default.replaceItemAt(url, withItemAt: tmp)
        }
        if let tr = SessionStore.transcript(id) {
            try SessionStore.saveTranscript(slice(tr, start: s, end: e), id: id)
        }
        if var record = AssistRecord.load(id) {
            // Live lines can start a moment after the saved length; keep them when the end stays.
            record.lines = slice(record.lines, start: s, end: e >= info.durationSec ? .infinity : e)
            try record.save(id)
        }
        info.startedAt = info.startedAt.addingTimeInterval(s)
        info.durationSec = e - s
        try SessionStore.save(info)
        return info
    }

    /// Split at `time` seconds: the original keeps the first part, a new recording gets the rest.
    /// Returns the new (second) recording.
    @discardableResult
    public static func split(_ id: String, at time: Double) throws -> SessionInfo {
        let info = try JSONIO.read(SessionInfo.self, from: AppPaths.info(id))
        guard ![.recording, .queued, .transcribing].contains(info.status) else { throw EditError.busy }
        guard time >= 1, info.durationSec - time >= 1 else { throw EditError.invalidRange }

        let startedAt = info.startedAt.addingTimeInterval(time)
        let newID = try SessionStore.createSessionFolder(base: SessionStore.folderName(for: startedAt))

        do {
            for t in info.tracks {
                let url = AppPaths.track(id, t)
                guard FileManager.default.fileExists(atPath: url.path) else { continue }
                try copyAudio(from: url, to: AppPaths.newTrack(newID, t), start: time, end: info.durationSec)
            }
            let transcript = SessionStore.transcript(id)
            if let transcript {
                try SessionStore.saveTranscript(slice(transcript, start: time, end: info.durationSec), id: newID)
            }
            var second = info
            second.id = newID
            second.title = info.title + " (Part 2)"
            second.startedAt = startedAt
            second.durationSec = info.durationSec - time
            if transcript == nil, second.status == .done { second.status = .recorded }
            if let record = AssistRecord.load(id) {
                try secondPart(of: record, from: time, end: .infinity).save(newID)
            }
            try SessionStore.save(second)
            try trim(id, start: 0, end: time)
            return second
        } catch {
            try? FileManager.default.removeItem(at: AppPaths.session(newID))
            throw error
        }
    }

    /// Assist lines starting between `start` and `end`, moved to start at 0.
    public static func slice(_ lines: [AssistLine], start: Double, end: Double) -> [AssistLine] {
        lines.filter { $0.start >= start && $0.start < end }
             .map { AssistLine(start: $0.start - start, speaker: $0.speaker, text: $0.text) }
    }

    /// The Assist record for part 2 of a split: the same context and panels, the lines after the
    /// split point, and nothing that belongs to the first meeting's write-up (it is a new meeting:
    /// no Aya meeting, upload times, created contacts, filled tables or recap draft).
    public static func secondPart(of record: AssistRecord, from time: Double, end: Double) -> AssistRecord {
        var r = AssistRecord(context: record.context, state: record.state, lines: slice(record.lines, start: time, end: end))
        r.speakersCarried = record.speakersCarried
        return r
    }

    /// The part of a transcript between `start` and `end`, moved to start at 0.
    public static func slice(_ t: Transcript, start: Double, end: Double) -> Transcript {
        var out = t
        out.turns = t.turns.compactMap { turn in
            let words = turn.words
                .filter { ($0.start + $0.end) / 2 >= start && ($0.start + $0.end) / 2 < end }
                .map { Word(w: $0.w, start: max(0, $0.start - start), end: min(end, $0.end) - start) }
            if turn.words.isEmpty {
                // No word timings: keep the turn if its middle lies inside.
                let mid = (turn.start + turn.end) / 2
                guard mid >= start && mid < end else { return nil }
                var c = turn; c.start = max(0, turn.start - start); c.end = min(end, turn.end) - start
                return c
            }
            guard let first = words.first, let last = words.last else { return nil }
            var c = turn
            c.words = words; c.start = first.start; c.end = last.end
            c.text = words.map(\.w).joined(separator: " ")
            return c
        }
        let used = Set(out.turns.map(\.speaker))
        out.speakers = t.speakers.filter { used.contains($0.id) }
        out.normalize()
        return out
    }

    /// Copy `start ..< end` seconds of an audio file into a new file of the same format.
    static func copyAudio(from src: URL, to dst: URL, start: Double, end: Double) throws {
        let input = try AVAudioFile(forReading: src)
        let rate = input.processingFormat.sampleRate
        let first = AVAudioFramePosition(start * rate)
        let last = min(input.length, AVAudioFramePosition(end * rate))
        try? FileManager.default.removeItem(at: dst)
        let output = try AVAudioFile(forWriting: dst, settings: input.fileFormat.settings,
                                     commonFormat: input.processingFormat.commonFormat,
                                     interleaved: input.processingFormat.isInterleaved)
        guard last > first else { return }
        input.framePosition = first
        let block: AVAudioFrameCount = 160_000
        guard let buf = AVAudioPCMBuffer(pcmFormat: input.processingFormat, frameCapacity: block) else { return }
        var remaining = last - first
        while remaining > 0 {
            try input.read(into: buf, frameCount: AVAudioFrameCount(min(Int64(block), remaining)))
            if buf.frameLength == 0 { break }
            try output.write(from: buf)
            remaining -= AVAudioFramePosition(buf.frameLength)
        }
    }
}
