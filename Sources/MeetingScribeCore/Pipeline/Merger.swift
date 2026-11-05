import Foundation

/// Words of one track plus who spoke when on that track.
public struct TrackResult: Sendable {
    public var track: Track
    public var words: [Word]
    /// Diarized segments with *global* speaker ids, or nil if the whole track is one speaker.
    public var segments: [(speaker: String, span: TimeSpan)]?
    /// Speaker id used when `segments` is nil (e.g. "me" for the mic in Call mode).
    public var fixedSpeaker: String?
    public init(track: Track, words: [Word], segments: [(speaker: String, span: TimeSpan)]?, fixedSpeaker: String?) {
        self.track = track; self.words = words; self.segments = segments; self.fixedSpeaker = fixedSpeaker
    }
}

public enum Merger {
    /// Assign each word to the speaker with the most overlap; words in gaps go to the
    /// nearest segment (within 1.5 s) or else stay with the previous word's speaker.
    public static func assignSpeakers(_ r: TrackResult) -> [(Word, String)] {
        guard let raw = r.segments, !raw.isEmpty else {
            return r.words.map { ($0, r.fixedSpeaker ?? "unknown") }
        }
        let segs = raw.sorted { $0.span.start < $1.span.start }
        var out: [(Word, String)] = []
        var previous: String?
        for w in r.words {
            let ws = TimeSpan(w.start, max(w.end, w.start + 0.02))
            var best: (String, Double)?
            for s in segs where s.span.end > ws.start - 0.01 && s.span.start < ws.end + 0.01 {
                let o = s.span.overlap(ws)
                if o > (best?.1 ?? -1) { best = (s.speaker, o) }
            }
            if best == nil || best!.1 <= 0 {
                let mid = (ws.start + ws.end) / 2
                let nearest = segs.min { dist(mid, $0.span) < dist(mid, $1.span) }
                if let n = nearest, dist(mid, n.span) < 1.5 { best = (n.speaker, 0) }
            }
            let spk = best?.0 ?? previous ?? segs.first?.speaker ?? "unknown"
            out.append((w, spk))
            previous = spk
        }
        return smoothSingletons(out)
    }

    static func dist(_ t: Double, _ s: TimeSpan) -> Double {
        t < s.start ? s.start - t : (t > s.end ? t - s.end : 0)
    }

    /// A single word flipping speaker inside another speaker's run is almost always a
    /// boundary error, so it is folded into the surrounding speaker.
    static func smoothSingletons(_ a: [(Word, String)]) -> [(Word, String)] {
        guard a.count >= 3 else { return a }
        var r = a
        for i in 1..<(a.count - 1) where a[i - 1].1 == a[i + 1].1 && a[i].1 != a[i - 1].1 {
            if a[i + 1].0.start - a[i - 1].0.end < 1.0 { r[i].1 = a[i - 1].1 }
        }
        return r
    }

    /// When the user is on speakers instead of headphones, the microphone also records the
    /// remote side. Drop mic words that repeat system-track words spoken just before.
    public static func removeEcho(mic: [(Word, String)], system: [Word]) -> [(Word, String)] {
        let order = mic.indices.sorted { mic[$0].0.start < mic[$1].0.start }
        let mask = echoMask(mic: order.map { mic[$0].0 }, system: system)
        return zip(order, mask).filter { !$0.1 }.map { mic[$0.0] }
    }

    /// For mic words sorted by start time: true where the word is echo of the system track.
    /// A mic word matches a system word that is (nearly) the same and started between 1 s
    /// after and 2 s before it (the echo arrives after the original). Up to two unmatched
    /// words between matches count as garbled echo. A lone match of a short word ("ja")
    /// is kept, because people say those themselves all the time.
    public static func echoMask(mic: [Word], system: [Word]) -> [Bool] {
        guard !mic.isEmpty, !system.isEmpty else { return [Bool](repeating: false, count: mic.count) }
        let sys = system.sorted { $0.start < $1.start }
        let sysStarts = sys.map(\.start)
        let sysNorm = sys.map { Array(WordAlignment.norm($0.w)) }
        func lowerBound(_ t: Double) -> Int {
            var lo = 0, hi = sysStarts.count
            while lo < hi { let mid = (lo + hi) / 2; if sysStarts[mid] < t { lo = mid + 1 } else { hi = mid } }
            return lo
        }
        let norms = mic.map { Array(WordAlignment.norm($0.w)) }
        var matched = [Bool](repeating: false, count: mic.count)
        for (i, w) in mic.enumerated() where !norms[i].isEmpty {
            var j = lowerBound(w.start - 2.0)
            while j < sys.count, sysStarts[j] < w.start + 1.0 {
                if similar(norms[i], sysNorm[j]) { matched[i] = true; break }
                j += 1
            }
        }
        var echo = matched
        let hits = matched.indices.filter { matched[$0] }
        for (a, b) in zip(hits, hits.dropFirst()) where b - a > 1 && b - a <= 3 && mic[b].start - mic[a].end < 2 {
            for k in (a + 1)..<b { echo[k] = true }
        }
        var i = 0
        while i < echo.count {
            guard echo[i] else { i += 1; continue }
            var j = i
            while j + 1 < echo.count, echo[j + 1] { j += 1 }
            let count = (i...j).filter { matched[$0] }.count
            if count < 2 && norms[i].count < 4 { for k in i...j { echo[k] = false } }
            i = j + 1
        }
        return echo
    }

    /// Equal, or for longer words within a small edit distance (ASR spells echo slightly
    /// differently: "Stoppbild" / "Stockbild").
    static func similar(_ a: [Character], _ b: [Character]) -> Bool {
        if a == b { return true }
        let longest = max(a.count, b.count)
        guard min(a.count, b.count) >= 4, abs(a.count - b.count) <= longest / 4 else { return false }
        let limit = max(1, longest / 4)
        var prev = Array(0...b.count)
        for (i, ca) in a.enumerated() {
            var cur = [i + 1] + [Int](repeating: 0, count: b.count)
            for (j, cb) in b.enumerated() {
                cur[j + 1] = min(prev[j + 1] + 1, cur[j] + 1, prev[j] + (ca == cb ? 0 : 1))
            }
            if cur.min()! > limit { return false }
            prev = cur
        }
        return prev[b.count] <= limit
    }

    /// Build speaker turns from speaker-tagged words of all tracks. Words are grouped into
    /// runs per speaker first, so overlapping talk on two tracks does not shred turns into
    /// alternating fragments; runs are then ordered by start time.
    public static func turns(_ tagged: [(Word, String, Track)], maxGap: Double = 1.5) -> [Turn] {
        var bySpeaker: [String: [(Word, Track)]] = [:]
        for (w, spk, t) in tagged { bySpeaker[spk, default: []].append((w, t)) }
        var runs: [Turn] = []
        for (spk, items) in bySpeaker {
            var current: Turn?
            for (w, track) in items.sorted(by: { $0.0.start < $1.0.start }) {
                if var c = current, w.start - c.end < maxGap {
                    c.words.append(w); c.end = max(c.end, w.end); c.text += " " + w.w
                    current = c
                } else {
                    if let c = current { runs.append(c) }
                    current = Turn(id: 0, speaker: spk, start: w.start, end: w.end, text: w.w, track: track, words: [w])
                }
            }
            if let c = current { runs.append(c) }
        }
        runs.sort { $0.start < $1.start }
        for i in runs.indices { runs[i].id = i }
        return runs
    }
}
