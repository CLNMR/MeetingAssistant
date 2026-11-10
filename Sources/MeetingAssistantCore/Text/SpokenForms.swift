import Foundation

/// Spoken forms that speech recognition writes down literally.
public enum SpokenForms {
    /// Everything below, in a safe order.
    public static func apply(_ text: String) -> String {
        times(emailAddresses(withoutHesitations(text)))
    }

    // MARK: Hesitations

    static let hesitations: Set<String> = ["äh", "ähm", "äähm", "öh", "öhm", "ehm", "hmm", "uh", "uhm", "erm"]

    /// "Ähm, also wir …" → "Also wir …". A lone "M," at the very start is a swallowed "ähm" too.
    public static func withoutHesitations(_ text: String) -> String {
        var words = text.split(separator: " ", omittingEmptySubsequences: false).map(String.init)
        var removedAtStart = false
        var i = 0
        while i < words.count {
            let p = VocabularyMatcher.split(words[i])
            let core = p.core.lowercased()
            let isFiller = hesitations.contains(core) || (i == 0 && core == "m" && p.trailing.hasPrefix(","))
            if isFiller, p.leading.isEmpty, p.trailing.isEmpty || p.trailing == "," || p.trailing == "…" {
                if i == 0 { removedAtStart = true }
                // "Wir, ähm, gehen" → "Wir gehen": the comma before it belonged to the pause.
                if p.trailing == ",", i > 0, words[i - 1].hasSuffix(",") { words[i - 1].removeLast() }
                words.remove(at: i)
                continue
            }
            i += 1
        }
        var out = words.joined(separator: " ").trimmingCharacters(in: .whitespaces)
        if removedAtStart, let f = out.first, f.isLowercase { out = f.uppercased() + out.dropFirst() }
        return out
    }

    // MARK: Times

    private static let hourMinute = try! NSRegularExpression(
        pattern: #"\b([01]?\d|2[0-3]) Uhr ([0-5]?\d)\b(?=\s*(?:$|[.,;:!?)]|\s[a-zäöüß]))"#)
    private static let rangeSameUnit = try! NSRegularExpression(
        pattern: #"\b(\d{1,2}:\d{2}) Uhr (bis|und|-|–) (\d{1,2}:\d{2}) Uhr\b"#)

    /// German "16 Uhr 45" → "16:45 Uhr", and "16:45 Uhr bis 17:05 Uhr" → "16:45 bis 17:05 Uhr".
    /// Only when the minutes end the clause or a lowercase word follows: in "um 10 Uhr 3 Personen"
    /// the number counts something.
    public static func times(_ text: String) -> String {
        var s = text
        var r = NSRange(s.startIndex..., in: s)
        for m in hourMinute.matches(in: s, range: r).reversed() {
            guard let all = Range(m.range, in: s), let h = Range(m.range(at: 1), in: s), let mi = Range(m.range(at: 2), in: s) else { continue }
            let minutes = String(s[mi])
            let formatted = "\(s[h]):\(minutes.count == 1 ? "0" + minutes : minutes) Uhr"
            s.replaceSubrange(all, with: formatted)
        }
        r = NSRange(s.startIndex..., in: s)
        s = rangeSameUnit.stringByReplacingMatches(in: s, range: r, withTemplate: "$1 $2 $3 Uhr")
        return s
    }

    // MARK: E-mail addresses

    static let atWords: Set<String> = ["at", "ät", "@"]
    static let dotWords: Set<String> = ["punkt", "dot"]
    static let joinWords: [String: String] = ["minus": "-", "bindestrich": "-", "dash": "-", "hyphen": "-",
                                              "unterstrich": "_", "underscore": "_"]
    static let topLevel: Set<String> = ["de", "com", "org", "net", "io", "ai", "eu", "ch", "at", "info", "biz", "co", "uk",
                                        "fr", "nl", "it", "es", "dev", "app", "online", "email", "cloud", "tech", "gmbh", "berlin"]

    /// "max punkt mustermann at firma punkt de" → "max.mustermann@firma.de". Only when the part
    /// after "at" has a "punkt"/"dot" and ends in a known top-level domain.
    public static func emailAddresses(_ text: String) -> String {
        var words = text.split(separator: " ", omittingEmptySubsequences: false).map(String.init)
        func parts(_ i: Int) -> (leading: String, core: String, trailing: String) { VocabularyMatcher.split(words[i]) }
        /// A plain word of letters/digits; punctuation before it only with `leading`, after it only with `last`.
        func word(_ i: Int, last: Bool = false, leading: Bool = false) -> String? {
            guard i >= 0, i < words.count else { return nil }
            let p = parts(i)
            guard p.leading.isEmpty || leading, !p.core.isEmpty, p.core.allSatisfy({ $0.isLetter || $0.isNumber }),
                  p.trailing.isEmpty || last else { return nil }
            return p.core.lowercased()
        }
        func separator(_ i: Int) -> String? {
            guard let w = word(i) else { return nil }
            if dotWords.contains(w) { return "." }
            return joinWords[w]
        }
        var i = 0
        while i < words.count {
            guard let at = word(i), atWords.contains(at) || words[i] == "@" else { i += 1; continue }
            // Domain after "at".
            var j = i + 1
            guard let first = word(j, last: true) else { i += 1; continue }
            var domain = first
            var end: (index: Int, domain: String)?
            while separator(j + 1) != nil, word(j) != nil, let next = word(j + 2, last: true) {
                let sep = separator(j + 1)!
                domain += sep + next
                j += 2
                if sep == ".", topLevel.contains(next) { end = (j, domain) }
                if word(j) == nil { break }               // punctuation after it ends the address
            }
            guard let e = end else { i += 1; continue }
            // Local part before "at".
            var k = i - 1
            guard var local = word(k, leading: true) else { i += 1; continue }
            while parts(k).leading.isEmpty, let sep = separator(k - 1), let prev = word(k - 2, leading: true) {
                local = prev + sep + local
                k -= 2
            }
            let address = parts(k).leading + local + "@" + e.domain + parts(e.index).trailing
            words.replaceSubrange(k...e.index, with: [address])
            i = k + 1
        }
        return words.joined(separator: " ")
    }
}
