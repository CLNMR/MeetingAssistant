import AppKit
import Foundation

/// Names and terms spelled the user's way.
///
/// - Replacements are fixed rules: whenever the spoken words appear, the written form is used
///   ("n acht n" → "n8n"). Case, spaces and hyphens between the spoken words do not matter.
/// - Terms are spellings: words that are the same letters ("hubspot", "Hub Spot") or sound
///   very much like a term ("Clawd" → "Claude") get the term's spelling. A word that is a
///   correctly spelled German or English word is never changed by a similar-sounding term,
///   so "glaube" does not become "Claude" and the adjective "klein" stays lowercase even when
///   a contact is called Klein.
///
/// Terms come from the user and, optionally, from Aya (companies, contacts, projects, team).
public struct Vocabulary: Codable, Equatable, Sendable {
    public struct Replacement: Codable, Equatable, Hashable, Sendable, Identifiable {
        public var spoken: String
        public var written: String
        public var id: String { spoken + "→" + written }
        public init(spoken: String, written: String) { self.spoken = spoken; self.written = written }
    }

    public var terms: [String] = []
    public var replacements: [Replacement] = []
    /// Names from Aya, refreshed with the lists.
    public var ayaTerms: [String] = []
    public var ayaFetchedAt: Date?
    /// How the Aya names were cleaned; older lists are loaded again.
    public var ayaTermsVersion = 0
    public static let currentAyaTermsVersion = 2
    public var useAya = true
    /// Correct words that sound like a term (not only identical letters).
    public var fuzzy = true

    public init(terms: [String] = [], replacements: [Replacement] = [], ayaTerms: [String] = [], useAya: Bool = true, fuzzy: Bool = true) {
        self.terms = terms; self.replacements = replacements; self.ayaTerms = ayaTerms; self.useAya = useAya; self.fuzzy = fuzzy
    }

    public init(from d: Decoder) throws {
        let c = try d.container(keyedBy: CodingKeys.self)
        terms = try c.decodeIfPresent([String].self, forKey: .terms) ?? []
        replacements = try c.decodeIfPresent([Replacement].self, forKey: .replacements) ?? []
        ayaTerms = try c.decodeIfPresent([String].self, forKey: .ayaTerms) ?? []
        ayaFetchedAt = try c.decodeIfPresent(Date.self, forKey: .ayaFetchedAt)
        ayaTermsVersion = try c.decodeIfPresent(Int.self, forKey: .ayaTermsVersion) ?? 0
        useAya = try c.decodeIfPresent(Bool.self, forKey: .useAya) ?? true
        fuzzy = try c.decodeIfPresent(Bool.self, forKey: .fuzzy) ?? true
    }

    public var activeTerms: [String] { terms + (useAya ? ayaTerms : []) }
    public var isEmpty: Bool { activeTerms.isEmpty && replacements.isEmpty }

    // MARK: Storage

    public static var url: URL { AppPaths.root.appendingPathComponent("vocabulary.json") }
    public static func load() -> Vocabulary { (try? JSONIO.read(Vocabulary.self, from: url)) ?? Vocabulary() }
    public func save() {
        try? FileManager.default.createDirectory(at: AppPaths.root, withIntermediateDirectories: true)
        try? JSONIO.write(self, to: Vocabulary.url)
    }

    /// The matcher for the current terms, built once per vocabulary.
    public func matcher(isKnownWord: @escaping @Sendable (String) -> Bool = SpellCheck.isKnown) -> VocabularyMatcher {
        VocabularyMatcher(terms: activeTerms, replacements: replacements, fuzzy: fuzzy, isKnownWord: isKnownWord)
    }

    // MARK: Terms from Aya

    /// Distinctive names from Aya records: company names without the legal form, full names of
    /// people and their last names, short project names. Titles are cleaned first (see `nameParts`).
    public static func termsFromAya(companies: [String], people: [String], projects: [String]) -> [String] {
        var out: [String] = []
        for c in companies {
            for part in nameParts(c) {
                let name = stripLegalForm(part)
                if !name.isEmpty { out.append(name) }
            }
        }
        for p in people {
            for name in nameParts(p) {
                out.append(name)
                let parts = name.split(separator: " ")
                if parts.count > 1, let last = parts.last, last.count >= 4 { out.append(String(last)) }
            }
        }
        for p in projects {
            out += nameParts(p).filter { $0.split(separator: " ").count <= 3 }
        }
        var seen = Set<String>()
        return out.filter { t in
            let k = VocabularyMatcher.letters(t)
            guard k.count >= 3, t.split(separator: " ").count <= 4, !t.contains("@"), !seen.contains(k) else { return false }
            seen.insert(k); return true
        }
    }

    /// "🤝 Muster GmbH (Notiz) – Werk Nord / Muster Süd" → ["Muster GmbH", "Werk Nord", "Muster Süd"]:
    /// no emoji or other symbols in front, no notes in parentheses, alternatives split apart,
    /// numbers such as "P0016" dropped.
    public static func nameParts(_ title: String) -> [String] {
        var t = title
        // Notes in parentheses or brackets.
        while let open = t.firstIndex(where: { $0 == "(" || $0 == "[" }) {
            let close = t[open...].firstIndex(where: { $0 == ")" || $0 == "]" }) ?? t.endIndex
            t.removeSubrange(open..<(close == t.endIndex ? close : t.index(after: close)))
        }
        for sep in [" – ", " — ", " - ", " / ", "/", ";", "|", ","] { t = t.replacingOccurrences(of: sep, with: "\u{1F}") }
        return t.split(separator: "\u{1F}").compactMap { raw -> String? in
            var p = String(raw).trimmingCharacters(in: .whitespacesAndNewlines)
            while let f = p.unicodeScalars.first, !CharacterSet.alphanumerics.contains(f) { p.unicodeScalars.removeFirst() }
            p = p.trimmingCharacters(in: .whitespacesAndNewlines)
            // Codes: a letter or two followed by digits ("P0016"), or digits only.
            let isCode = p.range(of: #"^[A-Za-z]{0,3}\d{2,}$"#, options: .regularExpression) != nil
            return p.isEmpty || isCode ? nil : p
        }
    }

    static let legalForms: [String] = [
        "gmbh & co. kg", "gmbh & co kg", "gmbh & co. kgaa", "ag & co. kg", "ug (haftungsbeschränkt)", "gmbh", "mbh", "ag", "kg",
        "kgaa", "ohg", "gbr", "ug", "se", "e.v.", "ev", "e.k.", "eg", "ltd.", "ltd", "inc.", "inc", "llc", "s.a.", "b.v.", "plc", "co.",
    ]

    /// "MUSTER SOLAR GmbH" → "MUSTER SOLAR".
    public static func stripLegalForm(_ name: String) -> String {
        var s = name.trimmingCharacters(in: .whitespacesAndNewlines)
        var changed = true
        while changed {
            changed = false
            let lower = s.lowercased()
            for f in legalForms where lower.hasSuffix(" " + f) || lower == f {
                s = String(s.dropLast(f.count)).trimmingCharacters(in: CharacterSet(charactersIn: " ,-–"))
                changed = true
                break
            }
        }
        return s
    }
}

/// Applies a vocabulary to text or to timed words.
public struct VocabularyMatcher: Sendable {
    struct Entry: Sendable { var text: String; var key: String; var sound: String; var words: Int }
    private let exact: [String: String]          // letters → term
    private let spoken: [String: String]         // letters of a replacement's spoken form → written
    private let fuzzyEntries: [Entry]
    private let isKnownWord: @Sendable (String) -> Bool
    private let maxWindow: Int

    /// Minimum similarity of the sound keys for a fuzzy correction (Jaro-Winkler, 0…1).
    public static let threshold = 0.9
    /// Terms shorter than this (letters) are only matched exactly.
    public static let fuzzyMinLetters = 6

    public init(terms: [String], replacements: [Vocabulary.Replacement], fuzzy: Bool = true,
                isKnownWord: @escaping @Sendable (String) -> Bool = SpellCheck.isKnown) {
        var exact: [String: String] = [:]
        var entries: [Entry] = []
        var maxWords = 1
        for t in terms {
            let term = t.trimmingCharacters(in: .whitespacesAndNewlines)
            let key = Self.letters(term)
            guard !key.isEmpty, exact[key] == nil else { continue }
            exact[key] = term
            let words = term.split(whereSeparator: { $0 == " " || $0 == "-" }).count
            maxWords = max(maxWords, words)
            if fuzzy, key.count >= Self.fuzzyMinLetters { entries.append(Entry(text: term, key: key, sound: Self.sound(term), words: words)) }
        }
        var spoken: [String: String] = [:]
        for r in replacements {
            let key = Self.letters(r.spoken)
            guard !key.isEmpty else { continue }
            spoken[key] = r.written
            maxWords = max(maxWords, r.spoken.split(whereSeparator: { $0 == " " || $0 == "-" }).count)
        }
        self.exact = exact
        self.spoken = spoken
        self.fuzzyEntries = entries
        self.isKnownWord = isKnownWord
        self.maxWindow = min(4, maxWords + 1)
    }

    public var isEmpty: Bool { exact.isEmpty && spoken.isEmpty }

    // MARK: Apply

    /// Text with the vocabulary applied. Only the words that change are touched; other
    /// spacing and line breaks stay as they are.
    public func apply(_ text: String) -> String {
        guard !isEmpty, !text.isEmpty else { return text }
        // Split into words and the whitespace between them: gaps[i] comes before tokens[i],
        // the last gap trails.
        var tokens: [String] = [], gaps: [String] = []
        var cur = "", gap = ""
        for ch in text {
            if ch.isWhitespace {
                if !cur.isEmpty { tokens.append(cur); cur = "" }
                gap.append(ch)
            } else {
                if cur.isEmpty { gaps.append(gap); gap = "" }
                cur.append(ch)
            }
        }
        if !cur.isEmpty { tokens.append(cur) }
        gaps.append(gap)
        let edits = self.edits(tokens)
        guard !edits.isEmpty else { return text }
        var out = ""
        var i = 0, e = 0
        while i < tokens.count {
            out += gaps[i]
            if e < edits.count, edits[e].range.lowerBound == i {
                out += edits[e].text
                i = edits[e].range.upperBound
                e += 1
            } else {
                out += tokens[i]
                i += 1
            }
        }
        out += gaps[tokens.count]
        return out
    }

    /// Words with the vocabulary applied; words joined into one term keep the first start and last end.
    public func apply(_ words: [Word]) -> [Word] {
        guard !isEmpty, !words.isEmpty else { return words }
        let edits = self.edits(words.map(\.w))
        guard !edits.isEmpty else { return words }
        var out: [Word] = []
        var i = 0, e = 0
        while i < words.count {
            if e < edits.count, edits[e].range.lowerBound == i {
                let r = edits[e].range
                out.append(Word(w: edits[e].text, start: words[r.lowerBound].start, end: words[r.upperBound - 1].end))
                i = r.upperBound; e += 1
            } else {
                out.append(words[i]); i += 1
            }
        }
        return out
    }

    struct Edit { var range: Range<Int>; var text: String }

    /// Non-overlapping replacements, left to right; at each position the longest match wins.
    func edits(_ tokens: [String]) -> [Edit] {
        let parts = tokens.map(Self.split)
        var out: [Edit] = []
        var i = 0
        while i < tokens.count {
            var found: Edit?
            for n in stride(from: min(maxWindow, tokens.count - i), through: 1, by: -1) {
                let window = Array(parts[i..<(i + n)])
                // Inner punctuation ("Hub, Spot") separates words.
                if n > 1, window.dropLast().contains(where: { !$0.trailing.isEmpty }) || window.dropFirst().contains(where: { !$0.leading.isEmpty }) { continue }
                let cores = window.map(\.core)
                guard cores.allSatisfy({ !$0.isEmpty }) else { continue }
                let key = Self.letters(cores.joined())
                guard !key.isEmpty else { continue }
                let original = cores.joined(separator: " ")
                if let w = spoken[key] {
                    found = Edit(range: i..<(i + n), text: window[0].leading + w + window[n - 1].trailing)
                    break
                }
                if let term = exact[key] {
                    if term == original { found = Edit(range: i..<(i + n), text: tokens[i..<(i + n)].joined(separator: " ")); break }
                    // Only the case differs and these are ordinary words: leave them ("klein" / Klein,
                    // "Sonnen" / SONNEN). Brand spellings with a capital inside ("HubSpot") still apply.
                    let caseOnly = term.lowercased() == original.lowercased()
                    if caseOnly, !Self.hasInnerCapital(term), cores.allSatisfy({ isKnownWord($0) }) { continue }
                    found = Edit(range: i..<(i + n), text: window[0].leading + term + window[n - 1].trailing)
                    break
                }
            }
            // No exact match: the window that sounds most like a term, the shorter one on a tie.
            if found == nil, !fuzzyEntries.isEmpty {
                var best: (n: Int, term: String, score: Double)?
                for n in 1...min(maxWindow, tokens.count - i) {
                    let window = Array(parts[i..<(i + n)])
                    if n > 1, window.dropLast().contains(where: { !$0.trailing.isEmpty }) || window.dropFirst().contains(where: { !$0.leading.isEmpty }) { break }
                    let cores = window.map(\.core)
                    guard cores.allSatisfy({ !$0.isEmpty }) else { break }
                    if let m = fuzzyMatch(cores: cores, key: Self.letters(cores.joined())), m.score > (best?.score ?? 0) {
                        best = (n, m.term, m.score)
                    }
                }
                if let b = best {
                    found = Edit(range: i..<(i + b.n), text: parts[i].leading + b.term + parts[i + b.n - 1].trailing)
                }
            }
            if let f = found {
                // An exact match that changes nothing is consumed but not reported.
                if tokens[f.range].joined(separator: " ") != f.text { out.append(f) }
                i = f.range.upperBound
            } else {
                i += 1
            }
        }
        return out
    }

    private func fuzzyMatch(cores: [String], key: String) -> (term: String, score: Double)? {
        guard !fuzzyEntries.isEmpty, key.count >= Self.fuzzyMinLetters - 1 else { return nil }
        // Ordinary words are never "corrected" into a name.
        if cores.allSatisfy({ isKnownWord($0) }) { return nil }
        // Digits mean something else ("A4", "2025").
        if key.contains(where: \.isNumber) { return nil }
        let s = Self.sound(cores.joined())
        let known = cores.count > 1 ? cores.filter { isKnownWord($0) }.map { $0.lowercased() } : []
        var best: (term: String, score: Double)?
        for e in fuzzyEntries {
            guard cores.count <= e.words + 1, abs(e.key.count - key.count) <= max(1, e.key.count / 5),
                  s.first == e.sound.first else { continue }
            // In a window of several words, ordinary words must be part of the term ("von Beispillop"
            // is not "Info Beispilop", but "Max Musterman" is "Max Mustermann").
            if !known.isEmpty {
                let termWords = Set(e.text.lowercased().split(whereSeparator: { !$0.isLetter && !$0.isNumber }).map(String.init))
                if !known.allSatisfy(termWords.contains) { continue }
            }
            let score = Self.jaroWinkler(s, e.sound)
            if score >= Self.threshold, score > (best?.score ?? 0) { best = (e.text, score) }
        }
        return best
    }

    // MARK: Keys

    /// "HubSpot", "iPhone": an uppercase letter right after a lowercase one.
    static func hasInnerCapital(_ s: String) -> Bool {
        zip(s, s.dropFirst()).contains { $0.isLowercase && $1.isUppercase }
    }

    /// Lowercased letters and digits only: "Hub-Spot" → "hubspot".
    public static func letters(_ s: String) -> String {
        String(s.lowercased().filter { $0.isLetter || $0.isNumber })
    }

    /// A rough sound key for German and English names: "Claude" and "Clawd" both become "klaud".
    static func sound(_ s: String) -> String {
        var t = letters(s)
        let rules: [(String, String)] = [
            ("ä", "e"), ("ö", "o"), ("ü", "u"), ("ß", "ss"), ("é", "e"), ("è", "e"),
            ("sch", "s"), ("ph", "f"), ("th", "t"), ("ck", "k"), ("qu", "kv"), ("dt", "t"),
            ("ch", "k"), ("c", "k"), ("aw", "au"), ("ou", "u"), ("ey", "ai"), ("ay", "ai"), ("ei", "ai"),
            ("ie", "i"), ("y", "i"), ("w", "v"), ("v", "f"), ("z", "ts"), ("x", "ks"),
        ]
        for (a, b) in rules { t = t.replacingOccurrences(of: a, with: b) }
        // An "h" after a vowel is silent ("Mahler" / "Maler").
        var out = ""
        for ch in t {
            if ch == "h", let last = out.last, "aeiou".contains(last) { continue }
            if ch == out.last { continue }             // double letters
            out.append(ch)
        }
        if out.count > 3, out.hasSuffix("e") { out.removeLast() }
        return out
    }

    static func jaroWinkler(_ a: String, _ b: String) -> Double {
        let s1 = Array(a), s2 = Array(b)
        if s1.isEmpty || s2.isEmpty { return s1.isEmpty && s2.isEmpty ? 1 : 0 }
        if s1 == s2 { return 1 }
        let range = max(0, max(s1.count, s2.count) / 2 - 1)
        var m1 = [Bool](repeating: false, count: s1.count), m2 = [Bool](repeating: false, count: s2.count)
        var matches = 0
        for i in s1.indices {
            let lo = max(0, i - range), hi = min(s2.count - 1, i + range)
            guard lo <= hi else { continue }
            for j in lo...hi where !m2[j] && s1[i] == s2[j] { m1[i] = true; m2[j] = true; matches += 1; break }
        }
        guard matches > 0 else { return 0 }
        var t = 0, k = 0
        for i in s1.indices where m1[i] {
            while !m2[k] { k += 1 }
            if s1[i] != s2[k] { t += 1 }
            k += 1
        }
        let m = Double(matches)
        let jaro = (m / Double(s1.count) + m / Double(s2.count) + (m - Double(t) / 2) / m) / 3
        var prefix = 0
        for (x, y) in zip(s1, s2).prefix(4) { if x == y { prefix += 1 } else { break } }
        return jaro + Double(prefix) * 0.1 * (1 - jaro)
    }

    /// "„Hub-Spot,“" → leading "„", core "Hub-Spot", trailing ",“".
    static func split(_ token: String) -> (leading: String, core: String, trailing: String) {
        let chars = Array(token)
        var a = 0, b = chars.count
        while a < b, !(chars[a].isLetter || chars[a].isNumber) { a += 1 }
        while b > a, !(chars[b - 1].isLetter || chars[b - 1].isNumber) { b -= 1 }
        return (String(chars[0..<a]), String(chars[a..<b]), String(chars[b...]))
    }
}

/// Whether a word is a correctly spelled German or English word (macOS spell checker).
public enum SpellCheck {
    private static let lock = NSLock()
    private static var cache: [String: Bool] = [:]

    public static let isKnown: @Sendable (String) -> Bool = { word in
        let w = word.trimmingCharacters(in: .punctuationCharacters)
        guard w.count > 1, !w.contains(where: \.isNumber) else { return false }
        lock.lock(); defer { lock.unlock() }
        if let c = cache[w] { return c }
        let checker = NSSpellChecker.shared
        var known = false
        for lang in ["de", "en"] {
            let r = checker.checkSpelling(of: w, startingAt: 0, language: lang, wrap: false, inSpellDocumentWithTag: 0, wordCount: nil)
            if r.location == NSNotFound { known = true; break }
        }
        cache[w] = known
        return known
    }
}
