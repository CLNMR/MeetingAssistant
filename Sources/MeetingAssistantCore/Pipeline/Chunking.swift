import Foundation

public struct TimeSpan: Codable, Hashable, Sendable {
    public var start: Double
    public var end: Double
    public init(_ start: Double, _ end: Double) { self.start = start; self.end = end }
    public var duration: Double { end - start }
    public func overlap(_ o: TimeSpan) -> Double { max(0, min(end, o.end) - max(start, o.start)) }
}

public enum Chunking {
    /// Merge VAD speech regions into ASR chunks: neighbours closer than `maxGap` are joined
    /// as long as the chunk stays below `maxLength`, so the model sees whole sentences
    /// instead of isolated words. Regions longer than `maxLength` are split evenly.
    public static func chunks(from regions: [TimeSpan], maxLength: Double, maxGap: Double = 1.2,
                              padding: Double = 0.15, totalDuration: Double) -> [TimeSpan] {
        var out: [TimeSpan] = []
        for r in regions.sorted(by: { $0.start < $1.start }) where r.duration > 0.05 {
            let padded = TimeSpan(max(0, r.start - padding), min(totalDuration, r.end + padding))
            if var last = out.last, padded.start - last.end <= maxGap, padded.end - last.start <= maxLength {
                last.end = max(last.end, padded.end)
                out[out.count - 1] = last
            } else {
                out.append(padded)
            }
        }
        var split: [TimeSpan] = []
        for c in out {
            if c.duration <= maxLength { split.append(c); continue }
            let n = Int((c.duration / maxLength).rounded(.up))
            let step = c.duration / Double(n)
            for i in 0..<n { split.append(TimeSpan(c.start + Double(i) * step, c.start + Double(i + 1) * step)) }
        }
        return split
    }
}
