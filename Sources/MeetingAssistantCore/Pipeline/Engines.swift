import AudioCommon
import FluidAudio
import Foundation
import MLX
import NaturalLanguage
import Qwen3ASR

/// Which ASR engine produces the final transcript.
public enum ASREngineKind: String, Codable, CaseIterable, Identifiable, Sendable {
    case parakeetUltra = "parakeet-ultra"
    case qwen3Large = "qwen3-1.7b"
    public var id: String { rawValue }
    public var title: String {
        switch self {
        case .parakeetUltra: return "Parakeet Ultra — fast, 25 European languages (recommended)"
        case .qwen3Large: return "Qwen3-ASR 1.7B — slower, 30 languages incl. Asian"
        }
    }
    /// Longest chunk the engine handles well.
    var maxChunkSeconds: Double { self == .qwen3Large ? 28 : 14 }
}

public enum TranscriptLanguage: String, Codable, CaseIterable, Identifiable, Sendable {
    case auto, german, english
    public var id: String { rawValue }
    public var title: String { ["auto": "Automatic", "german": "German", "english": "English"][rawValue]! }
    var qwenName: String? { self == .auto ? nil : (self == .german ? "German" : "English") }
    var nlLanguage: NLLanguage? { self == .auto ? nil : (self == .german ? .german : .english) }

    /// True when `text` is clearly in another language than this one.
    func clearlyNot(_ text: String) -> Bool {
        guard let mine = nlLanguage, text.filter(\.isLetter).count >= 2 else { return false }
        let r = NLLanguageRecognizer()
        r.processString(text)
        guard let (top, p) = r.languageHypotheses(withMaximum: 1).first else { return false }
        return top != mine && p >= 0.5
    }
}

public typealias ProgressCallback = @Sendable (Double, String) -> Void

/// Owns every loaded model so each is loaded once per app run. All inference is
/// serialised through this actor, which also keeps peak memory predictable.
public actor ModelStore {
    public static let shared = ModelStore()

    private var parakeet: AsrManager?
    private var qwen: Qwen3ASRModel?
    private var aligner: Qwen3ForcedAligner?
    private var vad: VadManager?
    private var nemotronOffline: Nemotron3Models?
    private var campPlus: CampPlusEmbedder?

    // MARK: Loading

    public func vadManager() async throws -> VadManager {
        if let vad { return vad }
        let v = try await VadManager(config: VadConfig(defaultThreshold: 0.5))
        vad = v
        return v
    }

    public func parakeetManager(progress: ProgressCallback? = nil) async throws -> AsrManager {
        if let parakeet { return parakeet }
        progress?(0, "Loading Parakeet Ultra")
        let version = AsrModelVersion.ultra
        let models = try await AsrModels.downloadAndLoad(version: version)
        let config = ASRConfig(tdtConfig: TdtConfig(blankId: version.blankId),
                               encoderHiddenSize: version.encoderHiddenSize)
        let m = AsrManager(config: config)
        try await m.loadModels(models)
        parakeet = m
        return m
    }

    public func qwenModels(progress: ProgressCallback?) async throws -> (Qwen3ASRModel, Qwen3ForcedAligner) {
        if let qwen, let aligner { return (qwen, aligner) }
        let q = try await Qwen3ASRModel.fromPretrained(modelId: Qwen3ASRModel.largeModelId) { p, msg in
            progress?(p * 0.8, "Qwen3-ASR: \(msg)")
        }
        let a = try await Qwen3ForcedAligner.fromPretrained { p, msg in
            progress?(0.8 + p * 0.2, "Aligner: \(msg)")
        }
        qwen = q; aligner = a
        return (q, a)
    }

    /// Free the models only the full transcription uses. Qwen3-ASR alone holds ~4 GB of
    /// GPU memory; keeping it loaded while the app only live-transcribes wastes that.
    /// They are loaded again (a few seconds) when the next transcription starts.
    public func unloadTranscriptionModels() {
        qwen = nil
        aligner = nil
        nemotronOffline = nil
        Memory.clearCache()
    }

        func nemotronModels(_ config: Nemotron3Config) async throws -> Nemotron3Models {
        if config.modelFileName == Nemotron3Config.offline.modelFileName, let nemotronOffline { return nemotronOffline }
        let m = try await Nemotron3Models.loadFromHuggingFace(config: config)
        if config.modelFileName == Nemotron3Config.offline.modelFileName { nemotronOffline = m }
        return m
    }

    public func liveDiarizer() async throws -> Nemotron3Diarizer {
        Nemotron3Diarizer(config: .fast32, models: try await nemotronModels(.fast32))
    }

    func embedder() async throws -> CampPlusEmbedder {
        if let campPlus { return campPlus }
        let e = try await CampPlusEmbedder.load()
        campPlus = e
        return e
    }

    /// Download and warm up everything the app needs (first-run setup).
    public func prepareAll(engine: ASREngineKind, progress: ProgressCallback?) async throws {
        progress?(0.02, "Voice activity detection")
        _ = try await vadManager()
        progress?(0.08, "Speaker diarization (Nemotron 3)")
        _ = try await nemotronModels(.offline)
        _ = try await nemotronModels(.fast32)
        progress?(0.2, "Speaker embeddings (CAM++)")
        _ = try await embedder()
        progress?(0.3, "Parakeet Ultra (live transcription)")
        _ = try await parakeetManager()
        if engine == .qwen3Large {
            _ = try await qwenModels { p, msg in progress?(0.45 + p * 0.55, msg) }
        }
        progress?(1, "Ready")
    }

    /// True when the models for live + default transcription are already in the local cache.
    public nonisolated static func coreModelsCached() -> Bool {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("FluidAudio/Models")
        return ["parakeet-ultra", "silero-vad", "campplus", "nemotron-3-diarization"].allSatisfy {
            FileManager.default.fileExists(atPath: base.appendingPathComponent($0).path)
        }
    }

    /// Free the large final-pass models (e.g. after a batch finished).
    public func unloadLarge() {
        qwen = nil
        aligner = nil
    }

    // MARK: Inference

    public func speechRegions(_ samples: [Float]) async throws -> [TimeSpan] {
        let vad = try await vadManager()
        var regions: [TimeSpan] = []
        // Bounded segments avoid IOSurface exhaustion on long recordings.
        let seg = 300 * 16_000
        var start = 0
        let cfg = VadSegmentationConfig(minSpeechDuration: 0.2, minSilenceDuration: 0.5, maxSpeechDuration: 60, speechPadding: 0.1)
        while start < samples.count {
            let end = min(start + seg, samples.count)
            let off = Double(start) / 16_000
            let segs = try await vad.segmentSpeech(Array(samples[start..<end]), config: cfg)
            regions += segs.map { TimeSpan($0.startTime + off, $0.endTime + off) }
            start = end
        }
        return regions
    }

    /// Speech probability for one 4096-sample (256 ms) chunk, used by live mode.
    public func vadProbability(_ chunk: [Float], state: VadStreamState) async throws -> (Float, VadStreamState) {
        let vad = try await vadManager()
        let r = try await vad.processStreamingChunk(chunk, state: state)
        return (r.probability, r.state)
    }

    /// Transcribe one chunk and return words with absolute timestamps.
    public func transcribe(_ audio: [Float], offset: Double, engine: ASREngineKind,
                           language: TranscriptLanguage) async throws -> [Word] {
        guard audio.count > 1600 else { return [] }
        switch engine {
        case .parakeetUltra:
            let m = try await parakeetManager()
            var state = TdtDecoderState.make(decoderLayers: await m.decoderLayerCount)
            let r = try await m.transcribe(audio, decoderState: &state)
            // Parakeet cannot be told the language. When the user chose one and Parakeet
            // heard another (typically short German replies read as English), redo that
            // chunk with Qwen3-ASR, which accepts the language.
            if language.clearlyNot(r.text) {
                return try await transcribe(audio, offset: offset, engine: .qwen3Large, language: language)
            }
            let timed = buildWordTimings(from: r.tokenTimings ?? [])
            if timed.isEmpty, !r.text.isEmpty {
                return WordAlignment.spread(text: r.text, start: offset, end: offset + Double(audio.count) / 16_000)
            }
            return timed.map { Word(w: $0.word, start: offset + $0.startTime, end: offset + $0.endTime) }
        case .qwen3Large:
            let (q, a) = try await qwenModels(progress: nil)
            var opts = Qwen3DecodingOptions(maxTokens: 1024, language: language.qwenName)
            opts.longInputNoRepeatNgramSize = 4
            let text = try q.transcribeThrowing(audio: audio, sampleRate: 16_000, options: opts)
                .trimmingCharacters(in: .whitespacesAndNewlines)
            guard !text.isEmpty else { return [] }
            let aligned = a.align(audio: audio, text: text, sampleRate: 16_000,
                                  language: language == .german ? "German" : "English")
            return WordAlignment.attach(text: text, aligned: aligned.map { ($0.text, Double($0.startTime), Double($0.endTime)) },
                                        offset: offset, chunkEnd: offset + Double(audio.count) / 16_000)
        }
    }

    /// Offline Nemotron 3 diarization -> segments (speaker index in arrival order).
    public func diarize(_ samples: [Float], speech: [TimeSpan]) async throws -> [DiarSegment] {
        let models = try await nemotronModels(.offline)
        let diarizer = Nemotron3Diarizer(config: .offline, models: models)
        // Per-10ms speech mask with 1 s padding lets the model skip silence.
        let frames = (samples.count + 159) / 160
        var mask = [Bool](repeating: false, count: frames)
        for s in speech {
            let a = max(0, Int((s.start - 1) * 100)), b = min(frames, Int((s.end + 1) * 100))
            if a < b { for f in a..<b { mask[f] = true } }
        }
        let (probs, count) = try diarizer.processComplete(samples, speechMask: mask)
        return Nemotron3Diarizer.segments(probabilities: probs, frameCount: count, threshold: 0.5, minDurationSeconds: 0.25)
            .map { DiarSegment(speaker: $0.speakerIndex, span: TimeSpan(Double($0.startSeconds), Double($0.endSeconds))) }
    }

    /// 192-d L2-normalised speaker embedding (CAM++).
    public func embedding(_ audio: [Float]) async throws -> [Float] {
        let e = try await embedder()
        return try await e.embed(audio: audio)
    }
}

public struct DiarSegment: Codable, Hashable, Sendable {
    public var speaker: Int
    public var span: TimeSpan
}
