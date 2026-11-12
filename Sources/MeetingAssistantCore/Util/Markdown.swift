import Foundation

/// Markdown as the recap fields use it (a GitHub-flavoured subset): the blocks and inline runs the
/// preview shows, the highlighting of the editor, and the editing commands of the Markdown editor
/// (spec 03 §8.2). All positions are UTF-16 offsets (`NSRange`), the same as on Windows.
public enum Markdown {}

// MARK: - Model

/// Inline style of a run of text.
public struct MarkdownStyle: OptionSet, Hashable, Sendable {
    public let rawValue: Int
    public init(rawValue: Int) { self.rawValue = rawValue }
    public static let bold = MarkdownStyle(rawValue: 1)
    public static let italic = MarkdownStyle(rawValue: 2)
    public static let strike = MarkdownStyle(rawValue: 4)
    public static let code = MarkdownStyle(rawValue: 8)
}

/// A piece of inline text with one style; `link` is the target of a `[text](url)` link.
public struct MarkdownRun: Equatable, Sendable {
    public var text: String
    public var style: MarkdownStyle
    public var link: String?
    public init(_ text: String, _ style: MarkdownStyle = [], link: String? = nil) { self.text = text; self.style = style; self.link = link }
}

public enum MarkdownMarker: Equatable, Sendable {
    case bullet
    case number(Int)
    case task(done: Bool)
}

/// One block of the preview. Paragraph, quote and item texts keep their line breaks ("\n").
public enum MarkdownBlock: Equatable, Sendable {
    case heading(level: Int, text: String)
    case paragraph(String)
    case item(depth: Int, marker: MarkdownMarker, text: String)
    case quote(String)
    case code(String)
    case table(header: [String], rows: [[String]])
    case rule
}

/// A highlighted range of the editor text. Line kinds cover the whole line; the renderer applies the
/// spans in order, so inline styles and syntax colouring come after the line they are on.
public struct MarkdownSpan: Equatable, Sendable {
    public enum Kind: Equatable, Sendable {
        case heading(Int)
        case quote
        case codeBlock
        case table
        /// Markup characters: `#`, `**`, `` ` ``, `|`, `](url)`, fences, rules.
        case syntax
        /// `-`, `1.`, `- [ ]`
        case listMarker
        case styled(MarkdownStyle, link: Bool)
    }
    public var location: Int
    public var length: Int
    public var kind: Kind
    public init(_ location: Int, _ length: Int, _ kind: Kind) { self.location = location; self.length = length; self.kind = kind }
    public var range: NSRange { NSRange(location: location, length: length) }
}

/// Text plus selection after an editing command.
public struct MarkdownEditResult: Equatable, Sendable {
    public var text: String
    public var selection: NSRange
    public init(text: String, selection: NSRange) { self.text = text; self.selection = selection }
}

// MARK: - Lines

private enum U {
    static let space: UInt16 = 32, tab: UInt16 = 9, nl: UInt16 = 10, cr: UInt16 = 13
    static let hash: UInt16 = 35, star: UInt16 = 42, under: UInt16 = 95, tilde: UInt16 = 126, dash: UInt16 = 45, plus: UInt16 = 43
    static let tick: UInt16 = 96, gt: UInt16 = 62, pipe: UInt16 = 124, backslash: UInt16 = 92
    static let lbr: UInt16 = 91, rbr: UInt16 = 93, lpar: UInt16 = 40, rpar: UInt16 = 41, dot: UInt16 = 46
    static let x: UInt16 = 120, X: UInt16 = 88

    static func isSpace(_ c: UInt16) -> Bool { c == space || c == tab || c == nl || c == cr }
    static func isDigit(_ c: UInt16) -> Bool { c >= 48 && c <= 57 }
    static func isAlnum(_ c: UInt16) -> Bool {
        if c < 128 { return isDigit(c) || (c >= 65 && c <= 90) || (c >= 97 && c <= 122) }
        guard let sc = Unicode.Scalar(c) else { return true }   // surrogate: part of a letter or emoji
        return CharacterSet.alphanumerics.contains(sc)
    }
    static func isPunct(_ c: UInt16) -> Bool { (c >= 33 && c <= 47) || (c >= 58 && c <= 64) || (c >= 91 && c <= 96) || (c >= 123 && c <= 126) }
}

/// How one line starts. `indent` and `prefix` are UTF-16 lengths; `column` counts a tab as 4.
struct MarkdownLine: Equatable {
    enum Kind: Equatable { case blank, text, heading(Int), bullet, number(Int), task(Bool), quote, fence, rule, table }
    var kind: Kind
    var indent: Int
    var column: Int
    /// End of the marker including its space (= indent for text lines).
    var prefix: Int
    /// End of the content (before a closing `#` run of a heading).
    var contentEnd: Int
    /// The bullet or number delimiter character (`-`, `*`, `+`, `.`, `)`).
    var delimiter: UInt16 = 0

    var isListItem: Bool {
        switch kind { case .bullet, .number, .task: return true; default: return false }
    }

    static func classify(_ u: ArraySlice<UInt16>) -> MarkdownLine {
        let b = u.startIndex, e = u.endIndex
        var i = b, col = 0
        while i < e, u[i] == U.space || u[i] == U.tab { col += u[i] == U.tab ? 4 : 1; i += 1 }
        let ind = i - b
        func line(_ k: Kind, _ p: Int, end: Int? = nil, delim: UInt16 = 0) -> MarkdownLine {
            MarkdownLine(kind: k, indent: ind, column: col, prefix: p, contentEnd: end ?? (e - b), delimiter: delim)
        }
        guard i < e else { return line(.blank, ind) }
        let c = u[i]
        func at(_ k: Int) -> UInt16? { i + k < e ? u[i + k] : nil }
        func spaceOrEnd(_ k: Int) -> Bool { at(k).map { $0 == U.space || $0 == U.tab } ?? true }

        if (c == U.tick || c == U.tilde), at(1) == c, at(2) == c { return line(.fence, ind) }
        if c == U.hash, col < 4 {
            var n = 0
            while at(n) == U.hash { n += 1 }
            if n <= 6, spaceOrEnd(n) {
                let p = min(i + n + 1, e) - b
                // Closing "#" run ("## Title ##").
                var end = e
                while end > b + p, U.isSpace(u[end - 1]) { end -= 1 }
                var h = end
                while h > b + p, u[h - 1] == U.hash { h -= 1 }
                if h < end, h == b + p || u[h - 1] == U.space { end = h; while end > b + p, U.isSpace(u[end - 1]) { end -= 1 } }
                return line(.heading(n), p, end: end - b)
            }
        }
        if c == U.dash || c == U.star || c == U.under {
            var n = 0, other = false
            for k in i..<e { if u[k] == c { n += 1 } else if u[k] != U.space && u[k] != U.tab { other = true; break } }
            if !other, n >= 3 { return line(.rule, ind) }
        }
        if c == U.dash || c == U.star || c == U.plus, spaceOrEnd(1) {
            if at(2) == U.lbr, let m = at(3), m == U.space || m == U.x || m == U.X, at(4) == U.rbr, spaceOrEnd(5) {
                return line(.task(m != U.space), min(i + 6, e) - b, delim: c)
            }
            return line(.bullet, min(i + 2, e) - b, delim: c)
        }
        if U.isDigit(c) {
            var n = 0
            while let d = at(n), U.isDigit(d), n < 9 { n += 1 }
            if let d = at(n), d == U.dot || d == U.rpar, spaceOrEnd(n + 1) {
                let num = Int(String(utf16CodeUnits: Array(u[i..<(i + n)]), count: n)) ?? 1
                return line(.number(num), min(i + n + 2, e) - b, delim: d)
            }
        }
        if c == U.gt { return line(.quote, (at(1) == U.space ? i + 2 : i + 1) - b) }
        if c == U.pipe { return line(.table, ind) }
        return line(.text, ind)
    }
}

/// The text split into lines: UTF-16 units of each line (without the line break) and where it starts.
private struct Lines {
    let u: [UInt16]
    var starts: [Int] = []
    var ends: [Int] = []   // before "\n" (and a "\r" before it)
    init(_ text: String) {
        u = Array(text.utf16)
        var s = 0
        for (k, c) in u.enumerated() where c == U.nl {
            starts.append(s); ends.append(k > s && u[k - 1] == U.cr ? k - 1 : k); s = k + 1
        }
        starts.append(s); ends.append(u.count)
    }
    var count: Int { starts.count }
    func slice(_ i: Int) -> ArraySlice<UInt16> { u[starts[i]..<ends[i]] }
    func string(_ i: Int) -> String { str(starts[i], ends[i]) }
    func str(_ a: Int, _ b: Int) -> String { a < b ? String(utf16CodeUnits: Array(u[a..<b]), count: b - a) : "" }
    func classify(_ i: Int) -> MarkdownLine { MarkdownLine.classify(slice(i)) }
    /// Index of the line containing offset `o`.
    func line(at o: Int) -> Int {
        var lo = 0, hi = count - 1
        while lo < hi { let m = (lo + hi + 1) / 2; if starts[m] <= o { lo = m } else { hi = m - 1 } }
        return lo
    }
}

// MARK: - Blocks

extension Markdown {
    /// The blocks of a Markdown text for the preview.
    public static func blocks(_ text: String) -> [MarkdownBlock] {
        let L = Lines(text)
        var out: [MarkdownBlock] = []
        var para: [String] = []
        var listColumns: [Int] = []
        var lastWasBlank = false
        func flush() { if !para.isEmpty { out.append(.paragraph(para.joined(separator: "\n"))); para = [] } }
        func trimmed(_ i: Int) -> String { L.string(i).trimmingCharacters(in: .whitespaces) }

        var i = 0
        while i < L.count {
            let ln = L.classify(i)
            let s = L.starts[i]
            defer { i += 1 }
            switch ln.kind {
            case .blank:
                flush(); lastWasBlank = true; continue
            case .fence:
                flush(); listColumns = []
                let open = L.u[s + ln.indent]
                var body: [String] = []
                i += 1
                while i < L.count {
                    let l2 = L.classify(i)
                    if l2.kind == .fence, L.u[L.starts[i] + l2.indent] == open { break }
                    body.append(L.string(i)); i += 1
                }
                out.append(.code(body.joined(separator: "\n")))
            case .heading(let n):
                flush(); listColumns = []
                out.append(.heading(level: n, text: L.str(s + ln.prefix, s + ln.contentEnd).trimmingCharacters(in: .whitespaces)))
            case .rule:
                flush(); listColumns = []
                out.append(.rule)
            case .bullet, .number, .task:
                flush()
                while let last = listColumns.last, last > ln.column { listColumns.removeLast() }
                if listColumns.last != ln.column { listColumns.append(ln.column) }
                let marker: MarkdownMarker
                switch ln.kind { case .number(let n): marker = .number(n); case .task(let d): marker = .task(done: d); default: marker = .bullet }
                out.append(.item(depth: listColumns.count - 1, marker: marker, text: L.str(s + ln.prefix, L.ends[i]).trimmingCharacters(in: .whitespaces)))
            case .quote:
                flush(); listColumns = []
                var body = [L.str(s + ln.prefix, L.ends[i])]
                while i + 1 < L.count, L.classify(i + 1).kind == .quote {
                    i += 1; let l2 = L.classify(i); body.append(L.str(L.starts[i] + l2.prefix, L.ends[i]))
                }
                out.append(.quote(body.joined(separator: "\n")))
            case .text, .table:
                if i + 1 < L.count, L.string(i).contains("|"), isDelimiterRow(L.string(i + 1)) {
                    flush(); listColumns = []
                    let header = cells(L.string(i))
                    var rows: [[String]] = []
                    i += 1
                    while i + 1 < L.count, L.classify(i + 1).kind != .blank, L.string(i + 1).contains("|") {
                        i += 1
                        var r = cells(L.string(i))
                        if r.count < header.count { r += Array(repeating: "", count: header.count - r.count) }
                        rows.append(Array(r.prefix(header.count)))
                    }
                    out.append(.table(header: header, rows: rows))
                } else if para.isEmpty, !lastWasBlank, ln.column > 0, case .item(let d, let m, let t)? = out.last {
                    // An indented line right after a list item continues it.
                    out[out.count - 1] = .item(depth: d, marker: m, text: t + "\n" + trimmed(i))
                } else {
                    if para.isEmpty { listColumns = [] }
                    para.append(trimmed(i))
                }
            }
            lastWasBlank = false
        }
        flush()
        return out
    }

    static func isDelimiterRow(_ s: String) -> Bool {
        let t = s.trimmingCharacters(in: .whitespaces)
        guard t.contains("-"), !t.isEmpty else { return false }
        var parts = t.split(separator: "|", omittingEmptySubsequences: false).map { $0.trimmingCharacters(in: .whitespaces) }
        if t.hasPrefix("|") { parts.removeFirst() }
        if t.hasSuffix("|"), !parts.isEmpty { parts.removeLast() }
        guard !parts.isEmpty else { return false }
        return parts.allSatisfy { p in
            var q = Substring(p)
            if q.hasPrefix(":") { q = q.dropFirst() }
            if q.hasSuffix(":") { q = q.dropLast() }
            return !q.isEmpty && q.allSatisfy { $0 == "-" }
        }
    }

    /// Cells of a table row: outer pipes dropped, split at unescaped pipes, trimmed, `\|` unescaped.
    static func cells(_ row: String) -> [String] {
        var t = row.trimmingCharacters(in: .whitespaces)
        if t.hasPrefix("|") { t.removeFirst() }
        if t.hasSuffix("|"), !t.hasSuffix("\\|") { t.removeLast() }
        var out: [String] = [], cur = "", esc = false
        for ch in t {
            if esc { cur.append(ch == "|" ? "|" : "\\" + String(ch)); esc = false; continue }
            if ch == "\\" { esc = true; continue }
            if ch == "|" { out.append(cur.trimmingCharacters(in: .whitespaces)); cur = ""; continue }
            cur.append(ch)
        }
        if esc { cur.append("\\") }
        out.append(cur.trimmingCharacters(in: .whitespaces))
        return out
    }
}

// MARK: - Inline

private struct Piece {
    var lo: Int, hi: Int
    var style: MarkdownStyle
    var link: String?
    var syntax: Bool
}

private struct InlineScanner {
    let u: [UInt16]
    var out: [Piece] = []

    mutating func scan(_ lo: Int, _ hi: Int, _ style: MarkdownStyle = [], _ link: String? = nil) {
        var i = lo, textStart = lo
        func run(_ k: Int, _ c: UInt16) -> Int { var n = 0; while k + n < hi, u[k + n] == c { n += 1 }; return n }
        func flush(_ end: Int) { if end > textStart { out.append(Piece(lo: textStart, hi: end, style: style, link: link, syntax: false)) } }
        func syntax(_ a: Int, _ b: Int) { out.append(Piece(lo: a, hi: b, style: style, link: link, syntax: true)) }

        while i < hi {
            let c = u[i]
            if c == U.backslash, i + 1 < hi, U.isPunct(u[i + 1]) {
                flush(i); syntax(i, i + 1); textStart = i + 1; i += 2; continue
            }
            if c == U.tick {
                let n = run(i, c)
                if let j = closingTicks(from: i + n, n, hi) {
                    flush(i); syntax(i, i + n)
                    out.append(Piece(lo: i + n, hi: j, style: style.union(.code), link: link, syntax: false))
                    syntax(j, j + n); i = j + n; textStart = i
                } else { i += n }
                continue
            }
            if c == U.star || c == U.under || c == U.tilde {
                let n = run(i, c)
                let d = c == U.tilde ? (n == 2 ? 2 : 0) : min(n, 3)
                let prevOK = c != U.under || i == 0 || !U.isAlnum(u[i - 1])
                if d > 0, n == d, prevOK, i + d < hi, !U.isSpace(u[i + d]), let k = closingRun(c, d, from: i + d, hi) {
                    let add: MarkdownStyle = c == U.tilde ? .strike : d == 1 ? .italic : d == 2 ? .bold : [.bold, .italic]
                    flush(i); syntax(i, i + d)
                    scan(i + d, k, style.union(add), link)
                    syntax(k, k + d); i = k + d; textStart = i
                } else { i += n }
                continue
            }
            if c == U.lbr, let (j, k) = linkEnds(i, hi) {
                flush(i); syntax(i, i + 1)
                let url = String(utf16CodeUnits: Array(u[(j + 2)..<k]), count: k - j - 2).trimmingCharacters(in: .whitespaces)
                scan(i + 1, j, style, url)
                syntax(j, k + 1); i = k + 1; textStart = i
                continue
            }
            i += 1
        }
        flush(hi)
    }

    func closingTicks(from a: Int, _ n: Int, _ hi: Int) -> Int? {
        var k = a
        while k < hi {
            if u[k] == U.tick {
                var m = 0; while k + m < hi, u[k + m] == U.tick { m += 1 }
                if m == n, k > a { return k }
                k += m
            } else { k += 1 }
        }
        return nil
    }

    /// The closing delimiter: a run of exactly `d` characters `c`, not after whitespace, with content before it.
    func closingRun(_ c: UInt16, _ d: Int, from a: Int, _ hi: Int) -> Int? {
        var k = a
        while k < hi {
            let ch = u[k]
            if ch == U.backslash { k += 2; continue }
            if ch == U.tick {
                var m = 0; while k + m < hi, u[k + m] == U.tick { m += 1 }
                if let j = closingTicks(from: k + m, m, hi) { k = j + m } else { k += m }
                continue
            }
            if ch == c {
                var m = 0; while k + m < hi, u[k + m] == c { m += 1 }
                let nextOK = c != U.under || k + m >= hi || !U.isAlnum(u[k + m])
                if m == d, k > a, !U.isSpace(u[k - 1]), nextOK { return k }
                k += m; continue
            }
            k += 1
        }
        return nil
    }

    /// `[text](url)`: the positions of `]` and `)`.
    func linkEnds(_ i: Int, _ hi: Int) -> (Int, Int)? {
        var depth = 0, j = i
        while j < hi {
            if u[j] == U.backslash { j += 2; continue }
            if u[j] == U.lbr { depth += 1 }
            if u[j] == U.rbr { depth -= 1; if depth == 0 { break } }
            j += 1
        }
        guard j < hi, j > i + 1, j + 1 < hi, u[j + 1] == U.lpar else { return nil }
        var k = j + 2
        while k < hi, u[k] != U.rpar, u[k] != U.space { k += 1 }
        guard k < hi, u[k] == U.rpar, k > j + 2 else { return nil }
        return (j, k)
    }
}

extension Markdown {
    /// The inline runs of a text (bold, italic, strikethrough, code, links), with the markup removed and
    /// adjacent runs of the same style joined.
    public static func inline(_ text: String) -> [MarkdownRun] {
        let u = Array(text.utf16)
        var sc = InlineScanner(u: u)
        sc.scan(0, u.count)
        var runs: [MarkdownRun] = []
        for p in sc.out where !p.syntax {
            let s = String(utf16CodeUnits: Array(u[p.lo..<p.hi]), count: p.hi - p.lo)
            if let last = runs.last, last.style == p.style, last.link == p.link { runs[runs.count - 1].text += s }
            else { runs.append(MarkdownRun(s, p.style, link: p.link)) }
        }
        return runs
    }

    /// Highlighting of the editor text.
    public static func highlight(_ text: String) -> [MarkdownSpan] {
        let L = Lines(text)
        var spans: [MarkdownSpan] = []
        var fence: UInt16? = nil
        var tableRows = Set<Int>()
        // Table rows: a header with a delimiter row below it, and the rows that follow.
        var i = 0
        while i < L.count {
            if i + 1 < L.count, L.string(i).contains("|"), isDelimiterRow(L.string(i + 1)) {
                tableRows.insert(i); tableRows.insert(i + 1); i += 2
                while i < L.count, L.classify(i).kind != .blank, L.string(i).contains("|") { tableRows.insert(i); i += 1 }
            } else { i += 1 }
        }
        func inline(_ a: Int, _ b: Int) {
            guard b > a else { return }
            var sc = InlineScanner(u: L.u)
            sc.scan(a, b)
            for p in sc.out where p.hi > p.lo {
                if p.syntax { spans.append(MarkdownSpan(p.lo, p.hi - p.lo, .syntax)) }
                else if !p.style.isEmpty || p.link != nil { spans.append(MarkdownSpan(p.lo, p.hi - p.lo, .styled(p.style, link: p.link != nil))) }
            }
        }
        for i in 0..<L.count {
            let s = L.starts[i], e = L.ends[i], ln = L.classify(i)
            if let f = fence {
                spans.append(MarkdownSpan(s, e - s, .codeBlock))
                if ln.kind == .fence, L.u[s + ln.indent] == f { spans.append(MarkdownSpan(s, e - s, .syntax)); fence = nil }
                continue
            }
            if tableRows.contains(i) {
                spans.append(MarkdownSpan(s, e - s, .table))
                inline(s, e)
                for k in s..<e where L.u[k] == U.pipe && (k == s || L.u[k - 1] != U.backslash) { spans.append(MarkdownSpan(k, 1, .syntax)) }
                if isDelimiterRow(L.string(i)) { spans.append(MarkdownSpan(s, e - s, .syntax)) }
                continue
            }
            switch ln.kind {
            case .fence:
                fence = L.u[s + ln.indent]
                spans.append(MarkdownSpan(s, e - s, .codeBlock)); spans.append(MarkdownSpan(s, e - s, .syntax))
            case .heading(let n):
                spans.append(MarkdownSpan(s, e - s, .heading(n)))
                spans.append(MarkdownSpan(s, ln.prefix, .syntax))
                inline(s + ln.prefix, s + ln.contentEnd)
                if s + ln.contentEnd < e { spans.append(MarkdownSpan(s + ln.contentEnd, e - s - ln.contentEnd, .syntax)) }
            case .rule:
                spans.append(MarkdownSpan(s, e - s, .syntax))
            case .bullet, .number, .task:
                spans.append(MarkdownSpan(s + ln.indent, ln.prefix - ln.indent, .listMarker))
                inline(s + ln.prefix, e)
            case .quote:
                spans.append(MarkdownSpan(s, e - s, .quote))
                spans.append(MarkdownSpan(s + ln.indent, ln.prefix - ln.indent, .syntax))
                inline(s + ln.prefix, e)
            case .text, .table:
                inline(s + ln.indent, e)
            case .blank:
                break
            }
        }
        return spans
    }
}

// MARK: - Editing commands

extension Markdown {
    public enum LineStyle: Equatable, Sendable { case heading(Int), bullet, numbered, task, quote }

    /// ⌘B / ⌘I / strikethrough / code: wraps the selection in `marker`, or removes the marker when the
    /// selection (or the text around it) already has it. With nothing selected, inserts the pair and puts
    /// the cursor between it.
    public static func wrap(_ text: String, _ sel: NSRange, marker: String) -> MarkdownEditResult {
        let ns = text as NSString
        let m = marker as NSString, L = m.length
        var r = sel
        // Leave surrounding whitespace (a double-clicked word with its space, a line with its break) outside.
        while r.length > 0, let c = Unicode.Scalar(ns.character(at: r.location + r.length - 1)), CharacterSet.whitespacesAndNewlines.contains(c) { r.length -= 1 }
        while r.length > 0, let c = Unicode.Scalar(ns.character(at: r.location)), CharacterSet.whitespacesAndNewlines.contains(c) { r.location += 1; r.length -= 1 }
        let c = m.character(at: 0)
        func runLeft(_ o: Int) -> Int { var n = 0; while o - n - 1 >= 0, ns.character(at: o - n - 1) == c { n += 1 }; return n }
        func runRight(_ o: Int) -> Int { var n = 0; while o + n < ns.length, ns.character(at: o + n) == c { n += 1 }; return n }
        // A run of the marker character has the marker when it is exactly as long, or 3 long (bold italic).
        func has(_ n: Int) -> Bool { n == L || (n == 3 && L < 3) }

        if r.length == 0 {
            let o = sel.location
            if runLeft(o) == L, runRight(o) == L, o - L >= 0 {
                return MarkdownEditResult(text: ns.replacingCharacters(in: NSRange(location: o - L, length: 2 * L), with: ""), selection: NSRange(location: o - L, length: 0))
            }
            return MarkdownEditResult(text: ns.replacingCharacters(in: NSRange(location: o, length: 0), with: marker + marker), selection: NSRange(location: o + L, length: 0))
        }
        let inner = ns.substring(with: r) as NSString
        var lead = 0; while lead < inner.length, inner.character(at: lead) == c { lead += 1 }
        var trail = 0; while trail < inner.length - lead, inner.character(at: inner.length - 1 - trail) == c { trail += 1 }
        if inner.length > 2 * L, has(lead), has(trail) {
            let core = inner.substring(with: NSRange(location: L, length: inner.length - 2 * L))
            return MarkdownEditResult(text: ns.replacingCharacters(in: r, with: core), selection: NSRange(location: r.location, length: (core as NSString).length))
        }
        if has(runLeft(r.location)), has(runRight(r.location + r.length)) {
            let out = NSMutableString(string: text)
            out.deleteCharacters(in: NSRange(location: r.location + r.length, length: L))
            out.deleteCharacters(in: NSRange(location: r.location - L, length: L))
            return MarkdownEditResult(text: out as String, selection: NSRange(location: r.location - L, length: r.length))
        }
        return MarkdownEditResult(text: ns.replacingCharacters(in: r, with: marker + (inner as String) + marker), selection: NSRange(location: r.location + L, length: r.length))
    }

    /// Lines touched by a selection: a selection that ends at the start of a line leaves that line out.
    private static func touchedLines(_ L: Lines, _ sel: NSRange) -> ClosedRange<Int> {
        let first = L.line(at: sel.location)
        var endO = sel.location + sel.length
        if sel.length > 0, endO > sel.location, L.starts.contains(endO) { endO -= 1 }
        return first...max(first, L.line(at: endO))
    }

    /// Heading, bullet, numbered, task or quote for the selected lines. When every selected line already has
    /// the style, it is removed; otherwise it replaces the lines' current markers. Numbers count 1, 2, 3 ….
    public static func toggleLines(_ text: String, _ sel: NSRange, style: LineStyle) -> MarkdownEditResult {
        let L = Lines(text)
        let range = touchedLines(L, sel)
        let lines = range.map { L.classify($0) }
        let skipBlank = range.count > 1
        func matches(_ ln: MarkdownLine) -> Bool {
            switch (style, ln.kind) {
            case (.heading(let a), .heading(let b)): return a == b
            case (.bullet, .bullet), (.numbered, .number), (.task, .task), (.quote, .quote): return true
            default: return false
            }
        }
        let considered = zip(range, lines).filter { !(skipBlank && $0.1.kind == .blank) }
        let remove = !considered.isEmpty && considered.allSatisfy { matches($0.1) }
        var n = 0
        var newLines: [String] = []
        var cursorPrefix = (old: 0, new: 0)
        let cursorLine = L.line(at: sel.location)
        for (idx, ln) in zip(range, lines) {
            let s = L.starts[idx]
            let indent = L.str(s, s + ln.indent)
            let hasMarker = ln.kind != .text && ln.kind != .blank && ln.kind != .table && ln.kind != .rule && ln.kind != .fence
            let contentStart = hasMarker ? ln.prefix : ln.indent
            let content = L.str(s + contentStart, L.ends[idx])
            var newPrefix: String
            if skipBlank && ln.kind == .blank {
                newLines.append(L.string(idx)); continue
            } else if remove {
                newPrefix = { if case .heading = style { return "" } else { return indent } }()
            } else {
                n += 1
                switch style {
                case .heading(let h): newPrefix = String(repeating: "#", count: h) + " "
                case .bullet: newPrefix = indent + "- "
                case .numbered: newPrefix = indent + "\(n). "
                case .task: newPrefix = indent + "- [ ] "
                case .quote: newPrefix = indent + "> "
                }
            }
            if idx == cursorLine { cursorPrefix = (contentStart, (newPrefix as NSString).length) }
            newLines.append(newPrefix + content)
        }
        let a = L.starts[range.lowerBound], b = L.ends[range.upperBound]
        let ns = text as NSString
        let joined = newLines.joined(separator: "\n")
        let out = ns.replacingCharacters(in: NSRange(location: a, length: b - a), with: joined)
        if sel.length == 0 {
            // The cursor keeps its place in the content; inside the old marker it moves to the content start.
            let lineStart = L.starts[cursorLine]
            let loc = lineStart + cursorPrefix.new + max(0, sel.location - lineStart - cursorPrefix.old)
            return MarkdownEditResult(text: out, selection: NSRange(location: min(loc, (out as NSString).length), length: 0))
        }
        return MarkdownEditResult(text: out, selection: NSRange(location: a, length: (joined as NSString).length))
    }

    /// Return in a list item or quote: continues it with the next marker (next number, an open task box).
    /// Return in an empty item ends the list (the marker is removed). nil = an ordinary line break.
    public static func newline(_ text: String, _ sel: NSRange) -> MarkdownEditResult? {
        guard sel.length == 0 else { return nil }
        let L = Lines(text)
        let i = L.line(at: sel.location)
        let ln = L.classify(i)
        let s = L.starts[i]
        guard ln.isListItem || ln.kind == .quote, sel.location >= s + ln.prefix else { return nil }
        let ns = text as NSString
        let content = L.str(s + ln.prefix, L.ends[i]).trimmingCharacters(in: .whitespaces)
        if content.isEmpty {
            let out = ns.replacingCharacters(in: NSRange(location: s, length: L.ends[i] - s), with: "")
            return MarkdownEditResult(text: out, selection: NSRange(location: s, length: 0))
        }
        let indent = L.str(s, s + ln.indent)
        let d = String(utf16CodeUnits: [ln.delimiter], count: 1)
        let next: String
        switch ln.kind {
        case .number(let n): next = indent + "\(n + 1)" + d + " "
        case .task: next = indent + d + " [ ] "
        case .bullet: next = indent + d + " "
        default: next = indent + "> "
        }
        let ins = "\n" + next
        let out = ns.replacingCharacters(in: sel, with: ins)
        return MarkdownEditResult(text: out, selection: NSRange(location: sel.location + (ins as NSString).length, length: 0))
    }

    /// Tab / Shift-Tab on list items: nests the selected items one level deeper (by the width of the item
    /// above's marker) or moves them out one level. nil when a selected line is not a list item.
    public static func indent(_ text: String, _ sel: NSRange, outdent: Bool) -> MarkdownEditResult? {
        let L = Lines(text)
        let range = touchedLines(L, sel)
        let lines = range.map { L.classify($0) }
        guard lines.allSatisfy({ $0.isListItem || $0.kind == .blank }), lines.contains(where: { $0.isListItem }) else { return nil }
        // List items above the selection (for the indent widths), nearest first.
        func above(_ idx: Int) -> [MarkdownLine] {
            var r: [MarkdownLine] = [], k = idx - 1
            while k >= 0 { let l = L.classify(k); if l.isListItem { r.append(l) } else if l.kind != .blank { break }; k -= 1 }
            return r
        }
        let ns = NSMutableString(string: text)
        var delta = 0, firstDelta = 0
        for (idx, ln) in zip(range, lines).reversed() where ln.isListItem {
            let prev = above(idx)
            var newIndent: Int
            if outdent {
                newIndent = prev.first(where: { $0.indent < ln.indent })?.indent ?? 0
                if newIndent >= ln.indent { continue }
            } else {
                let parent = prev.first(where: { $0.indent == ln.indent }) ?? ln
                newIndent = ln.indent + (parent.prefix - parent.indent)
            }
            let s = L.starts[idx]
            ns.replaceCharacters(in: NSRange(location: s, length: ln.indent), with: String(repeating: " ", count: newIndent))
            delta += newIndent - ln.indent
            if idx == range.lowerBound { firstDelta = newIndent - ln.indent }
        }
        let out = ns as String
        if sel.length == 0 {
            let lineStart = L.starts[range.lowerBound]
            return MarkdownEditResult(text: out, selection: NSRange(location: max(lineStart, sel.location + firstDelta), length: 0))
        }
        let a = L.starts[range.lowerBound]
        let b = L.ends[range.upperBound] + delta
        return MarkdownEditResult(text: out, selection: NSRange(location: a, length: b - a))
    }
}
