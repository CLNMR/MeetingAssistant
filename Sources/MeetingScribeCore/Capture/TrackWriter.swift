import AVFoundation
import Foundation

/// Writes one 16 kHz mono track to disk, keeping it aligned to the session clock:
/// silence is inserted for the delay before the first buffer and for any dropout
/// (e.g. while the system tap is rebuilt after an output-device change).
public final class TrackWriter: @unchecked Sendable {
    public let url: URL?
    private let file: AVAudioFile?   // nil: listen only, samples go to `onSamples` alone
    private let sessionStartHostTime: UInt64
    private var written: Int64 = 0
    private let lock = NSLock()
    private let resampler = Resampler16k()
    private var _level: Float = 0
    private var levelTime = Date.distantPast
    /// Smoothed RMS (0...1); decays to 0 when no audio arrives (the system tap is silent
    /// while nothing plays).
    public var level: Float {
        let age = Date().timeIntervalSince(levelTime)
        return age > 0.3 ? 0 : _level
    }
    public var onSamples: (@Sendable ([Float]) -> Void)?

    public init(url: URL?, sessionStartHostTime: UInt64) throws {
        self.url = url
        self.sessionStartHostTime = sessionStartHostTime
        guard let url else { file = nil; return }
        let settings: [String: Any] = [
            AVFormatIDKey: kAudioFormatLinearPCM,
            AVSampleRateKey: Resampler16k.sampleRate,
            AVNumberOfChannelsKey: 1,
            AVLinearPCMBitDepthKey: 16,
            AVLinearPCMIsFloatKey: false,
            AVLinearPCMIsBigEndianKey: false,
        ]
        file = try AVAudioFile(forWriting: url, settings: settings, commonFormat: .pcmFormatFloat32, interleaved: false)
    }

    public var durationSeconds: Double { Double(written) / Resampler16k.sampleRate }

    public func append(_ buffer: AVAudioPCMBuffer, time: AVAudioTime?) {
        let samples = resampler.convert(buffer)
        guard !samples.isEmpty else { return }
        lock.lock(); defer { lock.unlock() }

        if let time, time.isHostTimeValid, time.hostTime > sessionStartHostTime {
            let elapsed = AVAudioTime.seconds(forHostTime: time.hostTime - sessionStartHostTime)
            let expected = Int64(elapsed * Resampler16k.sampleRate)
            let gap = expected - written
            // Pad only for real gaps (> 100 ms); small jitter is ignored.
            if gap > 1600 { writeLocked([Float](repeating: 0, count: Int(gap))) }
        }
        writeLocked(samples)

        var sum: Float = 0
        for s in samples { sum += s * s }
        let rms = (sum / Float(samples.count)).squareRoot()
        _level = max(rms, _level * 0.8)
        levelTime = Date()
    }

    private func writeLocked(_ samples: [Float]) {
        guard let file else { written += Int64(samples.count); onSamples?(samples); return }
        guard let buf = AVAudioPCMBuffer(pcmFormat: Resampler16k.format, frameCapacity: AVAudioFrameCount(samples.count)) else { return }
        buf.frameLength = AVAudioFrameCount(samples.count)
        samples.withUnsafeBufferPointer { src in
            buf.floatChannelData![0].update(from: src.baseAddress!, count: samples.count)
        }
        do { try file.write(from: buf) } catch { NSLog("TrackWriter write failed: \(error)") }
        written += Int64(samples.count)
        onSamples?(samples)
    }

    /// Pad to a final length so both tracks end at the same time.
    public func finish(padTo seconds: Double) {
        lock.lock(); defer { lock.unlock() }
        let target = Int64(seconds * Resampler16k.sampleRate)
        if target > written { writeLocked([Float](repeating: 0, count: Int(target - written))) }
    }
}
