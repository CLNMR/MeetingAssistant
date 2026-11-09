import Foundation

public enum WordAlignment {
    static func norm(_ s: String) -> String {
        String(s.lowercased().unicodeScalars.filter { CharacterSet.alphanumerics.contains($0) })
    }

    /// Evenly spread words over a time span (fallback when no timings exist).
    public static func spread(text: String, start: Double, end: Double) -> [Word] {
        let tokens = text.split(whereSeparator: \.isWhitespace).map(String.init)
        guard !tokens.isEmpty else { return [] }
        let step = max(0.01, (end - start) / Double(tokens.count))
        return tokens.enumerated().map { i, t in Word(w: t, start: start + Double(i) * step, end: start + Double(i + 1) * step) }
    }

    /// The forced aligner returns bare words (no punctuation, sometimes split differently).
    /// Map its timings onto the punctuated transcript tokens in order, so the final transcript
    /// keeps the ASR's punctuation and casing. Unmatched tokens get interpolated times.
    public static func attach(text: String, aligned: [(String, Double, Double)], offset: Double, chunkEnd: Double) -> [Word] {
        let tokens = text.split(whereSeparator: \.isWhitespace).map(String.init)
        guard !tokens.isEmpty else { return [] }
        var times = [(Double, Double)?](repeating: nil, count: tokens.count)
        var j = 0
        for (i, tok) in tokens.enumerated() {
            let target = norm(tok)
            guard !target.isEmpty else { continue }
            // Search a small window ahead for the same word; aligner may merge or split tokens.
            var k = j
            var found: Int?
            while k < aligned.count && k < j + 4 {
                let a = norm(aligned[k].0)
                if a == target || (!a.isEmpty && (target.hasPrefix(a) || a.hasPrefix(target))) { found = k; break }
                k += 1
            }
            if let f = found {
                var end = aligned[f].2
                // Consume continuation pieces if the aligner split one token in several.
                var consumed = norm(aligned[f].0)
                var n = f + 1
                while consumed.count < target.count, n < aligned.count, target.hasPrefix(consumed + norm(aligned[n].0)) {
                    consumed += norm(aligned[n].0); end = aligned[n].2; n += 1
                }
                times[i] = (aligned[f].1 + offset, end + offset)
                j = n
            }
        }
        // Interpolate missing times between known neighbours.
        var result: [Word] = []
        for i in tokens.indices {
            if let t = times[i] { result.append(Word(w: tokens[i], start: t.0, end: max(t.1, t.0))); continue }
            let prevEnd = result.last?.end ?? offset
            var nextStart = chunkEnd
            if let n = times[(i + 1)...].firstIndex(where: { $0 != nil }), let t = times[n] { nextStart = t.0 }
            let missingRun = (times[i...].firstIndex(where: { $0 != nil }) ?? tokens.count) - i
            let step = max(0.01, (nextStart - prevEnd) / Double(missingRun + 1))
            result.append(Word(w: tokens[i], start: prevEnd, end: prevEnd + step))
        }
        // Enforce monotonic times.
        for i in 1..<max(1, result.count) where result[i].start < result[i - 1].start {
            result[i].start = result[i - 1].start
            result[i].end = max(result[i].end, result[i].start)
        }
        return result
    }
}
