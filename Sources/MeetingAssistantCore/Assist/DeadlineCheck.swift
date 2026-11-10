import Foundation

/// Removes deadlines a model added to next steps or to-dos although nobody said them:
/// "- Angebot schicken (bis Freitag)" when no Friday came up in the meeting. In doubt a
/// deadline stays: when a word of it was said (in German or English), when it is only a
/// number or a date ("23.11."), or when it has no word that names a time.
public enum DeadlineCheck {
    public struct Result: Equatable, Sendable {
        public var text: String
        /// The deadlines that were removed, e.g. "bis Freitag".
        public var removed: [String]
    }

    /// Words that carry no time of their own.
    static let fillers: Set<String> = [
        "bis", "spätestens", "spaetestens", "zum", "zur", "am", "an", "im", "in", "ende", "anfang", "mitte", "des", "der",
        "die", "das", "dem", "den", "nächste", "nächsten", "nächster", "kommende", "kommenden", "diese", "dieser", "diesen",
        "vor", "nach", "ca", "etwa", "frist", "deadline", "termin", "fällig", "by", "until", "till", "due", "the", "end", "of",
        "next", "this", "start", "beginning", "middle", "before", "after", "on", "at", "eod", "asap", "latest", "at the latest",
    ]

    /// Time words and their meaning across German and English, so "(by Friday)" in an English
    /// summary of a German meeting finds "Freitag".
    static let canonical: [String: String] = {
        var m: [String: String] = [:]
        let groups: [(String, [String])] = [
            ("mon", ["montag", "monday"]), ("tue", ["dienstag", "tuesday"]), ("wed", ["mittwoch", "wednesday"]),
            ("thu", ["donnerstag", "thursday"]), ("fri", ["freitag", "friday"]), ("sat", ["samstag", "sonnabend", "saturday"]),
            ("sun", ["sonntag", "sunday"]), ("tomorrow", ["morgen", "tomorrow"]), ("dayafter", ["übermorgen"]),
            ("today", ["heute", "today", "tonight"]), ("week", ["woche", "week", "kw", "kalenderwoche"]),
            ("weekend", ["wochenende", "weekend"]), ("month", ["monat", "month", "monatsende"]),
            ("quarter", ["quartal", "quarter", "quartalsende"]), ("year", ["jahr", "jahresende", "year"]),
            ("jan", ["januar", "jänner", "january"]), ("feb", ["februar", "february"]), ("mar", ["märz", "march"]),
            ("apr", ["april"]), ("may", ["mai", "may"]), ("jun", ["juni", "june"]), ("jul", ["juli", "july"]),
            ("aug", ["august"]), ("sep", ["september"]), ("oct", ["oktober", "october"]), ("nov", ["november"]),
            ("dec", ["dezember", "december"]), ("christmas", ["weihnachten", "christmas"]), ("easter", ["ostern", "easter"]),
        ]
        for (c, words) in groups { for w in words { m[w] = c } }
        return m
    }()

    /// A bullet ending in a parenthesis, or in "bis/by …" after a dash or comma.
    private static let trailingParenthesis = try! NSRegularExpression(
        pattern: #"^(\s*(?:[-*•–]|\d+\.)\s+.*?)\s*\(([^()]{2,60})\)([.;]?)\s*$"#)
    private static let trailingBy = try! NSRegularExpression(
        pattern: #"^(\s*(?:[-*•–]|\d+\.)\s+.*?)(?:\s*[,–—-]\s*|\s+)((?:bis|spätestens|by|until|due)\s+[^,.;()]{2,40})([.;]?)\s*$"#,
        options: [.caseInsensitive])

    public static func filter(_ text: String, transcript: String) -> Result {
        let spoken = Set(words(transcript))
        let spokenCanonical = Set(spoken.compactMap { canonical[$0] ?? prefixCanonical($0) })
        var removed: [String] = []
        let lines = text.components(separatedBy: "\n").map { line -> String in
            for re in [trailingParenthesis, trailingBy] {
                let r = NSRange(line.startIndex..., in: line)
                guard let m = re.firstMatch(in: line, range: r),
                      let head = Range(m.range(at: 1), in: line), let dl = Range(m.range(at: 2), in: line),
                      let tail = Range(m.range(at: 3), in: line) else { continue }
                let deadline = String(line[dl])
                if isUnsupported(deadline, spoken: spoken, spokenCanonical: spokenCanonical) {
                    removed.append(deadline)
                    return String(line[head]) + String(line[tail])
                }
                return line
            }
            return line
        }
        return Result(text: lines.joined(separator: "\n"), removed: removed)
    }

    /// True when the deadline names a time and none of its time words was said.
    static func isUnsupported(_ deadline: String, spoken: Set<String>, spokenCanonical: Set<String>) -> Bool {
        if deadline.contains(where: \.isNumber) { return false }
        let content = words(deadline).filter { !fillers.contains($0) }
        let timeWords = content.filter { canonical[$0] != nil || prefixCanonical($0) != nil }
        // No time word at all ("(Max)", "(optional)"): not a deadline.
        guard !timeWords.isEmpty else { return false }
        for w in content {
            if spoken.contains(w) { return false }
            if spoken.contains(where: { $0.count >= 5 && w.count >= 5 && ($0.hasPrefix(w) || w.hasPrefix($0)) }) { return false }
            if let c = canonical[w] ?? prefixCanonical(w), spokenCanonical.contains(c) { return false }
        }
        return true
    }

    /// "freitags", "Freitagabend" → "fri".
    static func prefixCanonical(_ w: String) -> String? {
        guard w.count >= 5 else { return nil }
        for (k, c) in canonical where k.count >= 5 && w.hasPrefix(k) { return c }
        return nil
    }

    static func words(_ text: String) -> [String] {
        text.lowercased().split(whereSeparator: { !$0.isLetter }).map(String.init)
    }
}
