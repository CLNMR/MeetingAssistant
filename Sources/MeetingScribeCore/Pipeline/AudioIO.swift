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
