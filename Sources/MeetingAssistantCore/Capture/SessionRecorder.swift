import AVFoundation
import CoreAudio
import Foundation

/// Records one meeting: microphone and (depending on the mode) system audio into
/// two time-aligned 16 kHz mono tracks, plus session.json.
public final class SessionRecorder: @unchecked Sendable {
    public let info: SessionInfo
    private let mic = MicCapture()
    private let tap = SystemAudioTap()
    public private(set) var micWriter: TrackWriter?
    public private(set) var systemWriter: TrackWriter?
    private let startHostTime: UInt64

    /// Live audio callbacks (16 kHz mono) for the live transcriber.
    public var onMicSamples: (@Sendable ([Float]) -> Void)? { didSet { micWriter?.onSamples = onMicSamples } }
    public var onSystemSamples: (@Sendable ([Float]) -> Void)? { didSet { systemWriter?.onSamples = onSystemSamples } }

    public init(mode: MeetingMode, title: String? = nil, expectedSpeakers: Int? = nil, language: String? = nil) throws {
        try AppPaths.ensure()
        let start = Date()
        // Unique even when two recordings start in the same second ("…_2").
        let id = try SessionStore.createSessionFolder(base: SessionStore.folderName(for: start))
        let tracks: [Track] = mode.recordsSystemAudio ? [.mic, .system] : [.mic]
        let df = DateFormatter(); df.dateStyle = .medium; df.timeStyle = .short
        info = SessionInfo(id: id, title: title ?? "Meeting \(df.string(from: start))", startedAt: start,
                           durationSec: 0, mode: mode, tracks: tracks, expectedSpeakers: expectedSpeakers,
                           language: language, status: .recording)
        startHostTime = mach_absolute_time()
    }

    public func start(inputDeviceID: AudioObjectID?, muteSystemAudio: Bool = false) throws {
        try JSONIO.write(info, to: AppPaths.info(info.id))
        let mw = try TrackWriter(url: AppPaths.newTrack(info.id, .mic), sessionStartHostTime: startHostTime)
        mw.onSamples = onMicSamples
        micWriter = mw
        try mic.start(deviceID: inputDeviceID) { buf, time in mw.append(buf, time: time) }

        if info.mode.recordsSystemAudio {
            let sw = try TrackWriter(url: AppPaths.newTrack(info.id, .system), sessionStartHostTime: startHostTime)
            sw.onSamples = onSystemSamples
            systemWriter = sw
            tap.muted = muteSystemAudio
            do {
                try tap.start { buf, time in sw.append(buf, time: time) }
            } catch {
                mic.stop()
                throw error
            }
        }
    }

    public var elapsed: Double { AVAudioTime.seconds(forHostTime: mach_absolute_time() - startHostTime) }
    public var micLevel: Float { micWriter?.level ?? 0 }
    public var systemLevel: Float { systemWriter?.level ?? 0 }

    @discardableResult
    public func stop() throws -> SessionInfo {
        mic.stop()
        tap.stop()
        let duration = max(micWriter?.durationSeconds ?? 0, systemWriter?.durationSeconds ?? 0)
        micWriter?.finish(padTo: duration)
        systemWriter?.finish(padTo: duration)
        micWriter = nil; systemWriter = nil   // closes the files
        var final = info
        final.durationSec = duration
        final.status = .recorded
        try JSONIO.write(final, to: AppPaths.info(info.id))
        return final
    }
}
