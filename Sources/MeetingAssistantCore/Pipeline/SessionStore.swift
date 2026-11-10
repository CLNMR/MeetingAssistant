import AVFoundation
import Foundation

public enum SessionStore {
    public static func list() -> [SessionInfo] {
        let fm = FileManager.default
        guard let dirs = try? fm.contentsOfDirectory(at: AppPaths.sessions, includingPropertiesForKeys: nil) else { return [] }
        return dirs.compactMap { try? JSONIO.read(SessionInfo.self, from: $0.appendingPathComponent("session.json")) }
            .sorted { $0.startedAt > $1.startedAt }
    }

    public static func save(_ info: SessionInfo) throws { try JSONIO.write(info, to: AppPaths.info(info.id)) }

    public static func transcript(_ id: String) -> Transcript? {
        try? JSONIO.read(Transcript.self, from: AppPaths.transcript(id))
    }

    public static func saveTranscript(_ t: Transcript, id: String) throws { try JSONIO.write(t, to: AppPaths.transcript(id)) }

    public static func delete(_ id: String) throws { try FileManager.default.removeItem(at: AppPaths.session(id)) }

    /// "yyyy-MM-dd_HH-mm-ss" of `date`, the base of a session folder name.
    public static func folderName(for date: Date) -> String {
        let fmt = DateFormatter()
        fmt.locale = Locale(identifier: "en_US_POSIX")
        fmt.dateFormat = "yyyy-MM-dd_HH-mm-ss"
        return fmt.string(from: date)
    }

    /// Creates a new, empty session folder named `base`, or `base_2`, `base_3`… when that name is
    /// taken, and returns the id. Creating the folder is the reservation, so two sessions started
    /// in the same second never share a folder.
    public static func createSessionFolder(base: String) throws -> String {
        try AppPaths.ensure()
        var id = base
        var n = 1
        while true {
            do {
                try FileManager.default.createDirectory(at: AppPaths.session(id), withIntermediateDirectories: false)
                return id
            } catch {
                guard FileManager.default.fileExists(atPath: AppPaths.session(id).path) else { throw error }
                n += 1
                id = "\(base)_\(n)"
            }
        }
    }

    /// Length in seconds of the longest track on disk (0 when there is none). Used when a
    /// session was interrupted before its length was saved.
    public static func audioDuration(_ id: String, tracks: [Track]) -> Double {
        var longest = 0.0
        for t in tracks {
            let url = AppPaths.track(id, t)
            guard FileManager.default.fileExists(atPath: url.path) else { continue }
            if let f = try? AVAudioFile(forReading: url), f.fileFormat.sampleRate > 0 {
                longest = max(longest, Double(f.length) / f.fileFormat.sampleRate)
            } else if let size = (try? FileManager.default.attributesOfItem(atPath: url.path))?[.size] as? Int {
                // Unreadable header: estimate from the size (16 kHz, 16-bit mono as recorded).
                longest = max(longest, Double(max(0, size - 4096)) / 32_000)
            }
        }
        return longest
    }

    /// A session that was still recording when the app quit or crashed: it becomes "recorded"
    /// and gets its length from the audio files, so it can be played, trimmed and split.
    public static func recoverInterrupted(_ info: SessionInfo) -> SessionInfo {
        var s = info
        if s.status == .recording { s.status = .recorded }
        if s.durationSec <= 0 { s.durationSec = audioDuration(s.id, tracks: s.tracks) }
        return s
    }

    /// Create a session from existing audio files (e.g. a recording made with another app).
    public static func importFiles(mic: URL, system: URL?, mode: MeetingMode, title: String? = nil,
                                   expectedSpeakers: Int? = nil, startedAt: Date? = nil) throws -> SessionInfo {
        let id = try createSessionFolder(base: "import_" + folderName(for: Date()))
        var duration = 0.0
        var tracks: [Track] = []
        for (url, track) in [(mic, Track.mic), (system, Track.system)] {
            guard let url else { continue }
            let samples = try AudioIO.load16k(url)
            let writer = try TrackWriter(url: AppPaths.newTrack(id, track), sessionStartHostTime: 0)
            guard let buf = AVAudioPCMBuffer(pcmFormat: Resampler16k.format, frameCapacity: AVAudioFrameCount(max(1, samples.count))) else { continue }
            buf.frameLength = AVAudioFrameCount(samples.count)
            samples.withUnsafeBufferPointer { buf.floatChannelData![0].update(from: $0.baseAddress!, count: samples.count) }
            writer.append(buf, time: nil)
            duration = max(duration, Double(samples.count) / 16_000)
            tracks.append(track)
        }
        let info = SessionInfo(id: id, title: title ?? mic.deletingPathExtension().lastPathComponent, startedAt: startedAt ?? Date(),
                               durationSec: duration, mode: system == nil && mode != .inPerson ? .inPerson : mode,
                               tracks: tracks, expectedSpeakers: expectedSpeakers, language: nil, status: .recorded)
        try save(info)
        return info
    }
}
