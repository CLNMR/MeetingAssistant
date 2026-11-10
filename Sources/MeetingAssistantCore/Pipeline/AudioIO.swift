import AVFoundation
import Foundation

public enum AudioIO {
    /// Load any audio file as 16 kHz mono Float32 samples.
    public static func load16k(_ url: URL) throws -> [Float] {
        let file = try AVAudioFile(forReading: url)
        let fmt = file.processingFormat
        let total = AVAudioFrameCount(file.length)
        guard total > 0 else { return [] }
        let resampler = Resampler16k()
        var out: [Float] = []
        out.reserveCapacity(Int(Double(total) * 16_000 / fmt.sampleRate) + 1024)
        let block: AVAudioFrameCount = 16_000 * 30
        while file.framePosition < file.length {
            guard let buf = AVAudioPCMBuffer(pcmFormat: fmt, frameCapacity: block) else { break }
            try file.read(into: buf, frameCount: block)
            if buf.frameLength == 0 { break }
            if fmt.sampleRate == 16_000 && fmt.channelCount == 1 && fmt.commonFormat == .pcmFormatFloat32 {
                out.append(contentsOf: UnsafeBufferPointer(start: buf.floatChannelData![0], count: Int(buf.frameLength)))
            } else {
                out.append(contentsOf: resampler.convert(buf))
            }
        }
        return out
    }

    /// Writes 16 kHz mono samples as a Float32 CAF file.
    public static func writeCAF(_ samples: [Float], to url: URL) throws {
        let fmt = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: 16_000, channels: 1, interleaved: false)!
        let file = try AVAudioFile(forWriting: url, settings: fmt.settings, commonFormat: .pcmFormatFloat32, interleaved: false)
        let block = 16_000 * 30
        var i = 0
        while i < samples.count {
            let n = min(block, samples.count - i)
            guard let buf = AVAudioPCMBuffer(pcmFormat: fmt, frameCapacity: AVAudioFrameCount(n)) else { break }
            buf.frameLength = AVAudioFrameCount(n)
            samples.withUnsafeBufferPointer { src in buf.floatChannelData![0].update(from: src.baseAddress! + i, count: n) }
            try file.write(from: buf)
            i += n
        }
    }

    public static func slice(_ s: [Float], _ start: Double, _ end: Double) -> [Float] {
        let a = max(0, min(s.count, Int(start * 16_000)))
        let b = max(a, min(s.count, Int(end * 16_000)))
        return Array(s[a..<b])
    }

    public static func rms(_ s: ArraySlice<Float>) -> Float {
        guard !s.isEmpty else { return 0 }
        var sum: Float = 0
        for x in s { sum += x * x }
        return (sum / Float(s.count)).squareRoot()
    }
}
