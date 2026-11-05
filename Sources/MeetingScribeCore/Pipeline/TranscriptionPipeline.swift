import Foundation
import os

private let log = Logger(subsystem: "de.local.MeetingScribe", category: "Pipeline")

public struct PipelineSettings: Codable, Sendable {
    public var engine: ASREngineKind = .parakeetUltra
    public var language: TranscriptLanguage = .auto
    public var myName: String = "Me"
    /// Cosine similarity needed to call a speaker "me" / a known person.
    public var meThreshold: Float = 0.50
    public var libraryThreshold: Float = 0.55
    /// The best match must also beat the runner-up by this much. Embedding similarity
    /// shifts with room acoustics, so a margin is more reliable than a fixed cut-off.
    public var matchMargin: Float = 0.08
    public var autoMergeThreshold: Float = 0.80
    public init() {}
}

/// Turns a recorded session into a speaker-labelled transcript.
public enum TranscriptionPipeline {

    public static func run(sessionID: String, settings: PipelineSettings,
                           progress: ProgressCallback? = nil) async throws -> Transcript {
        let t0 = Date()
        let info = try JSONIO.read(SessionInfo.self, from: AppPaths.info(sessionID))
        let store = ModelStore.shared
        let mode = info.mode
        let language = info.language.flatMap(TranscriptLanguage.init(rawValue:)) ?? settings.language

        if settings.engine == .qwen3Large {
            progress?(0.01, "Loading models")
            _ = try await store.qwenModels { p, m in progress?(0.01 + p * 0.04, m) }
        }

        // Which tracks need diarization in this mode.
        func diarize(_ t: Track) -> Bool {
            switch mode {
            case .call: return t == .system
            case .inPerson: return t == .mic
            case .hybrid: return true
            }
        }

        var results: [TrackResult] = []
        var centroids: [String: [Float]] = [:]
        var speechSeconds: [String: Double] = [:]
        let tracks = info.tracks.filter { FileManager.default.fileExists(atPath: AppPaths.track(sessionID, $0).path) }
        guard !tracks.isEmpty else { throw PipelineError.noAudio }

        for (ti, track) in tracks.enumerated() {
            let base = 0.05 + 0.9 * Double(ti) / Double(tracks.count)
            let span = 0.9 / Double(tracks.count)
            let name = track == .mic ? "microphone" : "call audio"
            progress?(base, "Reading \(name)")
            let samples = try AudioIO.load16k(AppPaths.track(sessionID, track))
            guard AudioIO.rms(samples[...]) > 1e-5 else { log.info("track \(track.rawValue) is silent"); continue }

            progress?(base + span * 0.03, "Detecting speech in \(name)")
            let regions = try await store.speechRegions(samples)
            let chunks = Chunking.chunks(from: regions, maxLength: settings.engine.maxChunkSeconds,
                                         totalDuration: Double(samples.count) / 16_000)
            var words: [Word] = []
            for (ci, c) in chunks.enumerated() {
                try Task.checkCancellation()
                progress?(base + span * (0.05 + 0.75 * Double(ci) / Double(max(1, chunks.count))),
                          "Transcribing \(name) (\(ci + 1)/\(chunks.count))")
                words += try await store.transcribe(AudioIO.slice(samples, c.start, c.end), offset: c.start,
                                                    engine: settings.engine, language: language)
            }

            var segments: [(speaker: String, span: TimeSpan)]? = nil
            if diarize(track), !words.isEmpty {
                progress?(base + span * 0.82, "Identifying speakers in \(name)")
                let diar = try await store.diarize(samples, speech: regions)
                segments = diar.map { ("\(track.rawValue)-\($0.speaker)", $0.span) }
                // Speaker embeddings from each speaker's non-overlapping speech.
                let ids = Set(diar.map(\.speaker))
                for id in ids.sorted() {
                    let own = diar.filter { $0.speaker == id }.map(\.span)
                    let others = diar.filter { $0.speaker != id }.map(\.span)
                    var audio: [Float] = []
                    for s in own.sorted(by: { $0.duration > $1.duration }) {
                        if others.contains(where: { $0.overlap(s) > 0.2 * s.duration }) { continue }
                        audio += AudioIO.slice(samples, s.start, s.end)
                        if audio.count > 45 * 16_000 { break }
                    }
                    let secs = own.reduce(0) { $0 + $1.duration }
                    let key = "\(track.rawValue)-\(id)"
                    speechSeconds[key] = secs
                    if audio.count >= 16_000 { centroids[key] = try await store.embedding(audio) }
                }
            }
            let fixed: String? = (track == .mic && mode == .call) ? "me" : (segments == nil ? "\(track.rawValue)-0" : nil)
            results.append(TrackResult(track: track, words: words, segments: segments, fixedSpeaker: fixed))
            progress?(base + span, "Finished \(name)")
        }

        // Merge clusters: Nemotron sometimes splits one voice over two slots, and the user
        // may have told us how many people took part.
        var expected: Int? = info.expectedSpeakers
        if let e = expected, mode == .call { expected = max(1, e - 1) }    // the mic is "me"
        if mode == .hybrid { expected = nil }
        let mapping = SpeakerMath.mergeClusters(centroids, weights: speechSeconds, target: expected,
                                                autoMergeAbove: settings.autoMergeThreshold)
        func canonical(_ id: String) -> String { mapping[id] ?? id }

        // Speaker assignment per track.
        var tagged: [(Word, String, Track)] = []
        var perTrack: [Track: [(Word, String)]] = [:]
        for r in results { perTrack[r.track] = Merger.assignSpeakers(r).map { ($0.0, canonical($0.1)) } }
        if mode != .inPerson, let mic = perTrack[.mic], let sys = results.first(where: { $0.track == .system }) {
            perTrack[.mic] = Merger.removeEcho(mic: mic, system: sys.words)
        }
        for (t, ws) in perTrack { tagged += ws.map { ($0.0, $0.1, t) } }

        // Final centroids after merging.
        var merged: [String: [[Float]]] = [:]
        for (id, c) in centroids { merged[canonical(id), default: []].append(c) }
        let finalCentroids = merged.mapValues { SpeakerMath.mean($0) }

        // Who is "me"?
        var meID: String? = mode == .call ? "me" : nil
        if meID == nil, let vp = VoiceprintStore.load() {
            let candidates = finalCentroids.filter { $0.key.hasPrefix("mic-") }.map { ($0.key, $0.value) }
            meID = SpeakerMatch.best(for: vp.embedding, among: candidates,
                                     threshold: settings.meThreshold, margin: settings.matchMargin,
                                     reference: Array(finalCentroids.values))
        }

        let turns = Merger.turns(tagged)
        // Labels in order of first appearance; known voices get their names.
        let library = SpeakerLibrary.load()
        var speakers: [Speaker] = []
        var counter = 0
        var usedNames = Set<String>()
        for t in turns where !speakers.contains(where: { $0.id == t.speaker }) {
            let emb = finalCentroids[t.speaker]
            let isMe = t.speaker == meID
            var label: String
            if isMe {
                label = settings.myName
            } else if let emb, let name = SpeakerMatch.best(
                        for: emb, among: library.entries.map { ($0.name, $0.embedding) },
                        threshold: settings.libraryThreshold, margin: settings.matchMargin, reference: []),
                      !usedNames.contains(name) {
                label = name
            } else {
                counter += 1
                label = "Speaker \(counter)"
            }
            usedNames.insert(label)
            speakers.append(Speaker(id: t.speaker, label: label, track: t.track, embedding: emb, isMe: isMe))
        }

        let transcript = Transcript(turns: turns, speakers: speakers, engine: settings.engine.rawValue,
                                    createdAt: Date(), processingSeconds: Date().timeIntervalSince(t0))
        try JSONIO.write(transcript, to: AppPaths.transcript(sessionID))
        progress?(1, "Done")
        return transcript
    }
}
