import Foundation

public struct Voiceprint: Codable, Sendable {
    public var name: String
    public var embedding: [Float]
    public var seconds: Double
    public var createdAt: Date
}

/// Named voices the user has labelled before ("Anna", "Eddy"), learned from renames.
public struct SpeakerLibrary: Codable, Sendable {
    public struct Entry: Codable, Sendable { public var name: String; public var embedding: [Float]; public var count: Int }
    public var entries: [Entry] = []

    public static func load() -> SpeakerLibrary {
        (try? JSONIO.read(SpeakerLibrary.self, from: AppPaths.speakerLibrary)) ?? SpeakerLibrary()
    }
    public func save() throws { try JSONIO.write(self, to: AppPaths.speakerLibrary) }

    /// Add or refine a named voice (running average of normalised embeddings).
    public mutating func learn(name: String, embedding: [Float]) {
        let n = name.trimmingCharacters(in: .whitespaces)
        guard !n.isEmpty, !embedding.isEmpty else { return }
        if let i = entries.firstIndex(where: { $0.name.caseInsensitiveCompare(n) == .orderedSame }) {
            let c = Float(entries[i].count)
            entries[i].embedding = SpeakerMath.normalize(zip(entries[i].embedding, embedding).map { ($0 * c + $1) / (c + 1) })
            entries[i].count += 1
        } else {
            entries.append(Entry(name: n, embedding: embedding, count: 1))
        }
    }

    public mutating func forget(name: String) { entries.removeAll { $0.name == name } }
}

public enum SpeakerMath {
    public static func cosine(_ a: [Float], _ b: [Float]) -> Float {
        guard a.count == b.count, !a.isEmpty else { return -1 }
        var d: Float = 0, na: Float = 0, nb: Float = 0
        for i in a.indices { d += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i] }
        return d / max(1e-9, (na * nb).squareRoot())
    }
    public static func normalize(_ v: [Float]) -> [Float] {
        let n = max(1e-9, v.reduce(0) { $0 + $1 * $1 }.squareRoot())
        return v.map { $0 / n }
    }
    public static func mean(_ vs: [[Float]]) -> [Float] {
        guard let f = vs.first else { return [] }
        var acc = [Float](repeating: 0, count: f.count)
        for v in vs where v.count == f.count { for i in v.indices { acc[i] += v[i] } }
        return normalize(acc)
    }

    /// Greedily merge the most similar clusters until at most `target` remain.
    /// Returns a mapping old id -> surviving id.
    public static func mergeClusters(_ centroids: [String: [Float]], weights: [String: Double],
                                     target: Int?, autoMergeAbove: Float) -> [String: String] {
        var c = centroids
        var w = weights
        var map = Dictionary(uniqueKeysWithValues: centroids.keys.map { ($0, $0) })
        while c.count > 1 {
            var best: (String, String, Float)?
            let keys = c.keys.sorted()
            for i in keys.indices { for j in (i + 1)..<keys.count {
                let s = cosine(c[keys[i]]!, c[keys[j]]!)
                if s > (best?.2 ?? -2) { best = (keys[i], keys[j], s) }
            } }
            guard let (a, b, s) = best else { break }
            let overTarget = target.map { c.count > $0 } ?? false
            guard overTarget || s >= autoMergeAbove else { break }
            // Keep the cluster with more speech; fold the other into it.
            let (keep, drop) = (w[a] ?? 0) >= (w[b] ?? 0) ? (a, b) : (b, a)
            let wk = w[keep] ?? 1, wd = w[drop] ?? 1
            c[keep] = normalize(zip(c[keep]!, c[drop]!).map { ($0 * Float(wk) + $1 * Float(wd)) / Float(wk + wd) })
            w[keep] = wk + wd
            c[drop] = nil; w[drop] = nil
            for (k, v) in map where v == drop { map[k] = keep }
        }
        return map
    }
}

public enum SpeakerMatch {
    /// Best-matching candidate for `target`, or nil if the match is weak or ambiguous.
    /// The runner-up is the next best candidate or, if larger, the most similar vector in
    /// `reference` (e.g. other speakers of the same meeting) that is not the winner.
    public static func best(for target: [Float], among candidates: [(String, [Float])],
                            threshold: Float, margin: Float, reference: [[Float]]) -> String? {
        let scored = candidates.map { ($0.0, SpeakerMath.cosine($0.1, target)) }.sorted { $0.1 > $1.1 }
        guard let top = scored.first, top.1 >= threshold else { return nil }
        let winner = candidates.first { $0.0 == top.0 }!.1
        var runnerUp = scored.dropFirst().first?.1 ?? -1
        for r in reference where r != winner { runnerUp = max(runnerUp, SpeakerMath.cosine(r, target)) }
        return top.1 - runnerUp >= margin ? top.0 : nil
    }
}

public enum VoiceprintStore {
    public static func load() -> Voiceprint? { try? JSONIO.read(Voiceprint.self, from: AppPaths.voiceprint) }
    public static func save(_ v: Voiceprint) throws { try AppPaths.ensure(); try JSONIO.write(v, to: AppPaths.voiceprint) }
    public static func delete() { try? FileManager.default.removeItem(at: AppPaths.voiceprint) }

    /// Enroll from a recording of the user's voice (needs at least ~10 s of speech).
    public static func enroll(name: String, samples: [Float]) async throws -> Voiceprint {
        let regions = try await ModelStore.shared.speechRegions(samples)
        var speech: [Float] = []
        for r in regions { speech += AudioIO.slice(samples, r.start, r.end) }
        let seconds = Double(speech.count) / 16_000
        guard seconds >= 8 else { throw PipelineError.enrollmentTooShort(seconds) }
        // Average embeddings of ~6 s windows for a stable print.
        var embs: [[Float]] = []
        let win = 6 * 16_000
        var i = 0
        while i + win / 2 < speech.count {
            embs.append(try await ModelStore.shared.embedding(Array(speech[i..<min(speech.count, i + win)])))
            i += win
        }
        let v = Voiceprint(name: name, embedding: SpeakerMath.mean(embs), seconds: seconds, createdAt: Date())
        try save(v)
        return v
    }
}

public enum PipelineError: LocalizedError {
    case enrollmentTooShort(Double)
    case noAudio
    public var errorDescription: String? {
        switch self {
        case .enrollmentTooShort(let s): return String(format: "Only %.0f s of speech detected. Please speak for at least 15 seconds.", s)
        case .noAudio: return "The recording contains no audio."
        }
    }
}
