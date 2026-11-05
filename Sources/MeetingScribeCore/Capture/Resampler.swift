import AVFoundation

/// Converts arbitrary PCM buffers to 16 kHz mono Float32, the format every model uses.
public final class Resampler16k: @unchecked Sendable {
    public static let sampleRate: Double = 16_000
    public static let format = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: sampleRate,
                                             channels: 1, interleaved: false)!
    private var converter: AVAudioConverter?
    private var inputFormat: AVAudioFormat?

    public init() {}

    public func convert(_ buffer: AVAudioPCMBuffer) -> [Float] {
        if inputFormat != buffer.format {
            inputFormat = buffer.format
            converter = AVAudioConverter(from: buffer.format, to: Self.format)
            converter?.downmix = true
            converter?.sampleRateConverterQuality = AVAudioQuality.high.rawValue
        }
        guard let converter else { return [] }
        let ratio = Self.sampleRate / buffer.format.sampleRate
        let capacity = AVAudioFrameCount(Double(buffer.frameLength) * ratio + 64)
        guard let out = AVAudioPCMBuffer(pcmFormat: Self.format, frameCapacity: capacity) else { return [] }
        var consumed = false
        var error: NSError?
        converter.convert(to: out, error: &error) { _, status in
            if consumed { status.pointee = .noDataNow; return nil }
            consumed = true
            status.pointee = .haveData
            return buffer
        }
        guard error == nil, let ch = out.floatChannelData else { return [] }
        return Array(UnsafeBufferPointer(start: ch[0], count: Int(out.frameLength)))
    }
}
