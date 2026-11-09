import AVFoundation
import CoreAudio
import Foundation

/// Captures the microphone and (optionally) the system audio without saving anything,
/// for the always-on live transcript. Audio goes to the current "segment": a pair of
/// sample callbacks with their own time origin, so the transcriber can be replaced
/// (to keep memory bounded over hours) without restarting the capture.
public final class LiveListener: @unchecked Sendable {
    public typealias Sink = @Sendable ([Float]) -> Void
    private let mic = MicCapture()
    private let tap = SystemAudioTap()
    private let lock = NSLock()
    private var micWriter: TrackWriter?
    private var systemWriter: TrackWriter?
    private var capturesSystem = false
    public private(set) var isRunning = false

    public init() {}

    public func start(inputDeviceID: AudioObjectID?, systemAudio: Bool) throws {
        guard !isRunning else { return }
        capturesSystem = systemAudio
        try mic.start(deviceID: inputDeviceID) { [weak self] buf, time in
            guard let self else { return }
            self.lock.lock(); let w = self.micWriter; self.lock.unlock()
            w?.append(buf, time: time)
        }
        if systemAudio {
            do {
                try tap.start { [weak self] buf, time in
                    guard let self else { return }
                    self.lock.lock(); let w = self.systemWriter; self.lock.unlock()
                    w?.append(buf, time: time)
                }
            } catch {
                mic.stop()
                throw error
            }
        }
        isRunning = true
    }

    /// Route all audio from now on to new callbacks; times restart at 0.
    public func beginSegment(mic onMic: @escaping Sink, system onSystem: Sink?) {
        let now = mach_absolute_time()
        let mw = try? TrackWriter(url: nil, sessionStartHostTime: now)
        mw?.onSamples = onMic
        var sw: TrackWriter?
        if capturesSystem, let onSystem {
            sw = try? TrackWriter(url: nil, sessionStartHostTime: now)
            sw?.onSamples = onSystem
        }
        lock.lock(); micWriter = mw; systemWriter = sw; lock.unlock()
    }

    public func stop() {
        guard isRunning else { return }
        mic.stop()
        if capturesSystem { tap.stop() }
        lock.lock(); micWriter = nil; systemWriter = nil; lock.unlock()
        isRunning = false
    }

    public var micLevel: Float { lock.lock(); defer { lock.unlock() }; return micWriter?.level ?? 0 }
    public var systemLevel: Float { lock.lock(); defer { lock.unlock() }; return systemWriter?.level ?? 0 }
}
