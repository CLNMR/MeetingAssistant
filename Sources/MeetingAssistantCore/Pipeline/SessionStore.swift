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

    /// Create a session from existing audio files (e.g. a recording made with another app).
    public static func importFiles(mic: URL, system: URL?, mode: MeetingMode, title: String? = nil,
                                   expectedSpeakers: Int? = nil) throws -> SessionInfo {
        try AppPaths.ensure()
        let fmt = DateFormatter(); fmt.dateFormat = "yyyy-MM-dd_HH-mm-ss"
        var id = "import_" + fmt.string(from: Date())
        var n = 1
        while FileManager.default.fileExists(atPath: AppPaths.session(id).path) { n += 1; id = "import_\(fmt.string(from: Date()))_\(n)" }
        try FileManager.default.createDirectory(at: AppPaths.session(id), withIntermediateDirectories: true)
        var duration = 0.0
        var tracks: [Track] = []
        for (url, track) in [(mic, Track.mic), (system, Track.system)] {
            guard let url else { continue }
            let samples = try AudioIO.load16k(url)
            let writer = try TrackWriter(url: AppPaths.track(id, track), sessionStartHostTime: 0)
            guard let buf = AVAudioPCMBuffer(pcmFormat: Resampler16k.format, frameCapacity: AVAudioFrameCount(max(1, samples.count))) else { continue }
            buf.frameLength = AVAudioFrameCount(samples.count)
            samples.withUnsafeBufferPointer { buf.floatChannelData![0].update(from: $0.baseAddress!, count: samples.count) }
            writer.append(buf, time: nil)
            duration = max(duration, Double(samples.count) / 16_000)
            tracks.append(track)
        }
        let info = SessionInfo(id: id, title: title ?? mic.deletingPathExtension().lastPathComponent, startedAt: Date(),
                               durationSec: duration, mode: system == nil && mode != .inPerson ? .inPerson : mode,
                               tracks: tracks, expectedSpeakers: expectedSpeakers, language: nil, status: .recorded)
        try save(info)
        return info
    }
}
