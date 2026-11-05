import FluidAudio
import Foundation

extension ModelStore {
    /// Push-to-talk dictation: transcribe a short 16 kHz clip to plain text with Parakeet
    /// (fast enough that the text appears a fraction of a second after the key is released).
    /// Clips longer than one model window are split at pauses found by Silero VAD.
    public func dictate(_ samples: [Float]) async throws -> String {
        guard samples.count >= 4_000, AudioIO.rms(samples[...]) > 1e-4 else { return "" }
        let total = Double(samples.count) / 16_000
        let window = ASREngineKind.parakeetUltra.maxChunkSeconds
        let spans: [TimeSpan]
        if total <= window {
            spans = [TimeSpan(0, total)]
        } else {
            let regions = try await speechRegions(samples)
            spans = Chunking.chunks(from: regions, maxLength: window, totalDuration: total)
        }
        let m = try await parakeetManager()
        var parts: [String] = []
        for s in spans {
            var audio = AudioIO.slice(samples, s.start, s.end)
            guard audio.count > 1_600 else { continue }
            // Short clips are padded with silence; the encoder is unreliable below ~1 s.
            if audio.count < 24_000 { audio += [Float](repeating: 0, count: 24_000 - audio.count) }
            var state = TdtDecoderState.make(decoderLayers: await m.decoderLayerCount)
            let r = try await m.transcribe(audio, decoderState: &state)
            let text = r.text.trimmingCharacters(in: .whitespacesAndNewlines)
            if !text.isEmpty { parts.append(text) }
        }
        return parts.joined(separator: " ")
    }
}
