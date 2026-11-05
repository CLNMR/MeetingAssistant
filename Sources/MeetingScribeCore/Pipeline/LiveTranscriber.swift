import FluidAudio
import Foundation
import os

private let log = Logger(subsystem: "de.local.MeetingScribe", category: "Live")

/// One line of the live transcript.
public struct LiveLine: Identifiable, Hashable, Sendable {
    public var id: String
    public var track: Track
    public var start: Double
    public var end: Double
    public var text: String
    public var speaker: String?      // nil while the diarizer has not caught up
    public var isFinal: Bool
}

/// Live transcription while recording. Each track is cut into utterances by Silero VAD;
/// every finished utterance is transcribed by Parakeet (≈150× realtime on the Neural
/// Engine), and the growing utterance is re-transcribed every ~1.5 s as a grey preview.
/// Speakers come from streaming Nemotron 3 (≈3 s behind real time). After recording,
/// the offline pipeline replaces all of this with the accurate transcript.
public actor LiveTranscriber {
    public typealias Update = @Sendable ([LiveLine]) -> Void

    private struct Stream {
        var track: Track
        var pending: [Float] = []          // samples not yet run through VAD
        var position = 0                   // samples consumed by VAD so far
        var vad = VadStreamState.initial()
        var preroll: [Float] = []
        var utterance: [Float] = []
        var utteranceStart = 0.0
        var silentChunks = 0
        var lastPartialAt = 0
        var diarizer: Nemotron3Diarizer?
        var probs: [Float] = []            // Nemotron frame probabilities [frames * 8]
        var counter = 0
    }

    private let mode: MeetingMode
    private let onUpdate: Update
    private var streams: [Track: Stream] = [:]
    private struct Utterance { var track: Track; var words: [Word]; var isFinal: Bool }
    private var utterances: [String: Utterance] = [:]
    private var continuations: [Track: AsyncStream<[Float]>.Continuation] = [:]
    private var workers: [Task<Void, Never>] = []
    private let chunk = 4096

    public init(mode: MeetingMode, onUpdate: @escaping Update) {
        self.mode = mode
        self.onUpdate = onUpdate
    }

    public func start(tracks: [Track]) async {
        for t in tracks {
            var s = Stream(track: t)
            if diarizes(t) {
                do { s.diarizer = try await ModelStore.shared.liveDiarizer() }
                catch { log.error("live diarizer unavailable: \(error.localizedDescription)") }
            }
            streams[t] = s
            let (stream, cont) = AsyncStream<[Float]>.makeStream(bufferingPolicy: .unbounded)
            continuations[t] = cont
            workers.append(Task { [weak self] in
                for await samples in stream { await self?.process(t, samples) }
            })
        }
        do { _ = try await ModelStore.shared.parakeetManager() } catch { log.error("parakeet: \(error.localizedDescription)") }
    }

    /// Thread-safe entry point for the capture callbacks.
    public nonisolated func feed(_ track: Track, _ samples: [Float]) {
        Task { await self.enqueue(track, samples) }
    }
    private func enqueue(_ t: Track, _ s: [Float]) { continuations[t]?.yield(s) }

    public func stop() async {
        for c in continuations.values { c.finish() }
        for w in workers { await w.value }
        for t in Array(streams.keys) {
            await finalizeUtterance(t)
            if let d = streams[t]?.diarizer, let rest = try? d.finishStream() {
                for r in rest { streams[t]?.probs += r.probabilities }
            }
        }
        workers = []
        publish()
    }

    private func diarizes(_ t: Track) -> Bool {
        switch mode {
        case .call: return t == .system
        case .inPerson: return t == .mic
        case .hybrid: return true
        }
    }

    // MARK: - Processing

    private func process(_ t: Track, _ samples: [Float]) async {
        guard var s = streams[t] else { return }
        s.pending += samples
        if let d = s.diarizer {
            d.appendAudio(samples)
            if let results = try? d.processBufferedAudio() { for r in results { s.probs += r.probabilities } }
        }
        streams[t] = s
        while let st = streams[t], st.pending.count >= chunk {
            var cur = st
            let block = Array(cur.pending.prefix(chunk))
            cur.pending.removeFirst(chunk)
            let blockStart = Double(cur.position) / 16_000
            cur.position += chunk
            let (p, state) = (try? await ModelStore.shared.vadProbability(block, state: cur.vad)) ?? (0, cur.vad)
            cur.vad = state
            let speech = p >= 0.5
            if cur.utterance.isEmpty {
                if speech {
                    cur.utteranceStart = blockStart - Double(cur.preroll.count) / 16_000
                    cur.utterance = cur.preroll + block
                    cur.silentChunks = 0
                    cur.lastPartialAt = 0
                } else {
                    cur.preroll = Array((cur.preroll + block).suffix(chunk))
                }
                streams[t] = cur
            } else {
                cur.utterance += block
                cur.silentChunks = speech ? 0 : cur.silentChunks + 1
                streams[t] = cur
                let long = cur.utterance.count >= 14 * 16_000
                if cur.silentChunks >= 3 || long {
                    await finalizeUtterance(t)
                } else if cur.utterance.count - cur.lastPartialAt >= 24_000 {
                    streams[t]?.lastPartialAt = cur.utterance.count
                    await transcribeCurrent(t, final: false)
                }
            }
        }
        // Speaker labels catch up continuously; refreshing ~1.5×/s is plenty for the eye.
        if Date().timeIntervalSince(lastPublish) >= 0.7 { publish() }
    }

    private func finalizeUtterance(_ t: Track) async {
        guard let s = streams[t], !s.utterance.isEmpty else { return }
        await transcribeCurrent(t, final: true)
        streams[t]?.utterance = []
        streams[t]?.preroll = []
        streams[t]?.silentChunks = 0
        streams[t]?.counter += 1
        publish()
    }

    private func transcribeCurrent(_ t: Track, final: Bool) async {
        guard let s = streams[t] else { return }
        let id = "\(t.rawValue)-\(s.counter)"
        do {
            let words = try await ModelStore.shared.transcribe(s.utterance, offset: s.utteranceStart,
                                                               engine: .parakeetUltra, language: .auto)
            if words.isEmpty { if final { utterances[id] = nil }; return }
            utterances[id] = Utterance(track: t, words: words, isFinal: final)
        } catch {
            log.error("live transcription failed: \(error.localizedDescription)")
        }
    }

    /// Speaker of one word: fixed for single-speaker tracks, otherwise the dominant Nemotron
    /// slot over the word's span, or nil while the diarizer has not reached that time yet.
    private func speaker(for w: Word, track t: Track) -> String? {
        if mode == .call && t == .mic { return "me" }
        guard let s = streams[t], s.diarizer != nil else { return "\(t.rawValue)-0" }
        let frames = s.probs.count / 8
        let a = max(0, Int(w.start * 100) - 5), b = max(a + 1, Int(w.end * 100) + 5)
        guard b <= frames else { return nil }
        var sums = [Float](repeating: 0, count: 8)
        for f in a..<b { for k in 0..<8 { sums[k] += s.probs[f * 8 + k] } }
        guard let best = sums.indices.max(by: { sums[$0] < sums[$1] }), sums[best] > 0.1 * Float(b - a) else { return nil }
        return "\(t.rawValue)-\(best)"
    }

    /// Split every utterance at speaker changes and publish the resulting lines.
    private var lastPublish = Date.distantPast

    private func publish() {
        lastPublish = Date()
        var out: [LiveLine] = []
        // Without headphones the mic hears the call too: hide mic words that echo the system track.
        var echo = Set<Word>()
        if mode != .inPerson {
            let sys = utterances.values.filter { $0.track == .system }.flatMap(\.words)
            let mic = utterances.values.filter { $0.track == .mic }.flatMap(\.words).sorted { $0.start < $1.start }
            for (w, e) in zip(mic, Merger.echoMask(mic: mic, system: sys)) where e { echo.insert(w) }
        }
        for (id, u) in utterances {
            let words = u.track == .mic && !echo.isEmpty ? u.words.filter { !echo.contains($0) } : u.words
            var tagged = words.map { ($0, speaker(for: $0, track: u.track)) }
            // Unknown words inherit a known neighbour so a line is not split by gaps in coverage.
            for i in tagged.indices where tagged[i].1 == nil && i > 0 { tagged[i].1 = tagged[i - 1].1 }
            var part = 0
            var current: LiveLine?
            for (w, spk) in tagged {
                if var c = current, c.speaker == spk {
                    c.text += " " + w.w; c.end = w.end; current = c
                } else {
                    if let c = current { out.append(c) }
                    current = LiveLine(id: "\(id).\(part)", track: u.track, start: w.start, end: w.end, text: w.w,
                                       speaker: spk, isFinal: u.isFinal)
                    part += 1
                }
            }
            if let c = current { out.append(c) }
        }
        onUpdate(out.sorted { $0.start < $1.start })
    }
}
