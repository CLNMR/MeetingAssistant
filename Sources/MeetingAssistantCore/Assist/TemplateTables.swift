import Foundation

/// A Markdown table in a meeting template that is filled in during the meeting, e.g.
///
///     | Bereich | Modul | Nutzen wir heute (System) | Relevant für uns |
///     | CRM & Vertrieb | CRM | | |
///
/// Columns that the template already fills (Bereich, Modul) name the rows; the empty columns
/// are answered in the meeting. Rows are T1.1, T1.2 … so Claude and the app can address cells.
public struct TemplateTable: Codable, Hashable, Identifiable, Sendable {
    public var id: String                  // "T1"
    public var section: String             // heading the table sits under
    public var prompt: String              // text right above the table (the question it asks)
    public var columns: [String]
    public var answerColumns: [Int]        // columns that are empty in the template
    public var rows: [Row]

    public struct Row: Codable, Hashable, Identifiable, Sendable {
        public var id: String              // "T1.3"
        public var cells: [String]
        /// Per answer column: a quote or reason for the value.
        public var evidence: [String: String] = [:]
        /// Answer columns last set by the user (Claude does not overwrite them).
        public var userSet: Set<String> = []
        /// Name columns that are open in the template ("Eigener Vorschlag:"), with their template text.
        public var openKeys: [Int: String] = [:]
        public init(id: String, cells: [String]) {
            self.id = id; self.cells = cells
            for (i, c) in cells.enumerated() where TemplateTable.isOpenKey(c) { openKeys[i] = c.trimmingCharacters(in: .whitespaces) }
        }
    }

    public init(id: String, section: String, prompt: String, columns: [String], answerColumns: [Int], rows: [Row]) {
        self.id = id; self.section = section; self.prompt = prompt; self.columns = columns
        self.answerColumns = answerColumns; self.rows = rows
    }

    public var keyColumns: [Int] { columns.indices.filter { !answerColumns.contains($0) } }

    /// "CRM & Vertrieb · CRM"
    public func label(_ row: Row) -> String {
        keyColumns.map { row.cells[$0] }.filter { !$0.isEmpty }.joined(separator: " · ")
    }

    /// Answered: an answer cell has a value, or an open row ("Eigener Vorschlag: …") got its proposal.
    public func isFilled(_ row: Row) -> Bool {
        answerColumns.contains { !row.cells[$0].trimmingCharacters(in: .whitespaces).isEmpty }
            || row.openKeys.contains { i, base in row.cells[i].trimmingCharacters(in: .whitespaces) != base }
    }
    public var filledCount: Int { rows.filter(isFilled).count }

    /// A row whose name ends in ":" ("Eigener Vorschlag:") takes its name from the meeting.
    public static func isOpenKey(_ s: String) -> Bool { s.trimmingCharacters(in: .whitespaces).hasSuffix(":") }

    public func column(named name: String) -> Int? {
        let n = name.trimmingCharacters(in: .whitespaces).lowercased()
        if let i = columns.firstIndex(where: { $0.lowercased() == n }) { return i }
        return columns.firstIndex { $0.lowercased().hasPrefix(n) || n.hasPrefix($0.lowercased()) }
    }

    /// Sets one cell. Answer columns take the value; a key column only when its template text
    /// ends in ":" (the value is appended). Claude's values never replace the user's.
    /// Returns whether something changed.
    @discardableResult
    public mutating func set(row id: String, column name: String, value: String, evidence: String? = nil, byUser: Bool = false) -> Bool {
        guard let r = rows.firstIndex(where: { $0.id == id }), let c = column(named: name) else { return false }
        let v = value.trimmingCharacters(in: .whitespacesAndNewlines)
        let col = columns[c]
        if answerColumns.contains(c) {
            if !byUser, rows[r].userSet.contains(col) { return false }
            guard rows[r].cells[c] != v else { return false }
            rows[r].cells[c] = v
            if byUser { rows[r].userSet.insert(col) } else if let evidence, !evidence.isEmpty { rows[r].evidence[col] = evidence }
            return true
        }
        // Only open name cells ("Eigener Vorschlag:") take a value; Claude fills them once, the user any time.
        guard let base = rows[r].openKeys[c] else { return false }
        let current = rows[r].cells[c].trimmingCharacters(in: .whitespaces)
        if !byUser, current != base { return false }
        let new = v.isEmpty ? base : base + " " + v
        guard new != current else { return false }
        rows[r].cells[c] = new
        return true
    }

    /// Tables up to this many rows are written whole; longer ones only with the rows that were answered.
    public static let writeAllRowsUpTo = 30

    /// The filled table as Markdown.
    public func markdown() -> String {
        let shown = rows.count <= Self.writeAllRowsUpTo ? rows : rows.filter(isFilled)
        func esc(_ s: String) -> String { s.replacingOccurrences(of: "|", with: "\\|").replacingOccurrences(of: "\n", with: " ") }
        var out = ""
        if !prompt.isEmpty { out += prompt + "\n\n" }
        out += "| " + columns.map(esc).joined(separator: " | ") + " |\n"
        out += "|" + columns.map { _ in "---" }.joined(separator: "|") + "|\n"
        for r in shown { out += "| " + r.cells.map(esc).joined(separator: " | ") + " |\n" }
        if shown.count < rows.count { out += "\n_\(rows.count - shown.count) rows not discussed._\n" }
        return out
    }
}

/// A meeting template's agenda, split into checklist items, fill-in tables and free-text notes.
public struct ParsedTemplate: Sendable, Equatable {
    public var items: [PlaybookItem] = []
    public var tables: [TemplateTable] = []
    /// Paragraphs that are neither items nor table questions (context for Claude), "Section: text".
    public var notes: [String] = []
}

public enum TemplateParser {
    public static func parse(_ markdown: String) -> ParsedTemplate {
        var out = ParsedTemplate()
        var section = "", sectionNo = 0, itemNo = 0
        var flat = true
        var paragraph: [String] = []        // plain text lines since the last item/table/heading
        var tableLines: [String] = []

        func flushParagraph() {
            let text = paragraph.joined(separator: " ").trimmingCharacters(in: .whitespaces)
            if !text.isEmpty { out.notes.append(section.isEmpty ? text : "\(section): \(text)") }
            paragraph = []
        }
        func flushTable() {
            defer { tableLines = [] }
            guard tableLines.count >= 2 else { return }
            func cells(_ line: String) -> [String] {
                var s = line.trimmingCharacters(in: .whitespaces)
                if s.hasPrefix("|") { s.removeFirst() }
                if s.hasSuffix("|") { s.removeLast() }
                // Split on unescaped pipes.
                var parts: [String] = [], cur = "", prev: Character = " "
                for ch in s {
                    if ch == "|" && prev != "\\" { parts.append(cur); cur = "" } else { cur.append(ch) }
                    prev = ch
                }
                parts.append(cur)
                return parts.map { $0.replacingOccurrences(of: "\\|", with: "|").trimmingCharacters(in: .whitespaces) }
            }
            let header = cells(tableLines[0])
            let isSeparator = { (l: String) in cells(l).allSatisfy { !$0.isEmpty && $0.allSatisfy { "-: ".contains($0) } } }
            let body = tableLines.dropFirst().filter { !isSeparator($0) }.map(cells)
            guard !header.isEmpty, !body.isEmpty else { return }
            let width = header.count
            let tid = "T\(out.tables.count + 1)"
            let rows = body.enumerated().map { i, c in
                TemplateTable.Row(id: "\(tid).\(i + 1)", cells: (c + Array(repeating: "", count: max(0, width - c.count))).prefix(width).map { $0 })
            }
            let answers = (0..<width).filter { col in rows.allSatisfy { $0.cells[col].isEmpty } }
            guard !answers.isEmpty else {
                // A filled reference table, nothing to answer: keep it as a note.
                out.notes.append((section.isEmpty ? "" : "\(section): ") + tableLines.joined(separator: "\n"))
                return
            }
            let prompt = paragraph.joined(separator: " ").trimmingCharacters(in: .whitespaces)
            paragraph = []
            out.tables.append(TemplateTable(id: tid, section: section, prompt: prompt, columns: header, answerColumns: answers, rows: rows))
        }

        var lead: String?                  // "Pro genutztem Modul:" — prefixes the items below it
        for raw in markdown.components(separatedBy: .newlines) {
            let line = raw.trimmingCharacters(in: .whitespaces)
            if line.hasPrefix("|") { tableLines.append(line); lead = nil; continue }
            if !tableLines.isEmpty { flushTable() }
            if line.isEmpty { continue }
            if line.hasPrefix("#") {
                flushParagraph(); lead = nil
                flat = false
                section = PlaybookParser.stripNumber(String(line.drop(while: { $0 == "#" })).trimmingCharacters(in: .whitespaces))
                sectionNo += 1; itemNo = 0
                continue
            }
            if let text = PlaybookParser.bulletText(line) {
                if !paragraph.isEmpty {
                    let p = paragraph.joined(separator: " ").trimmingCharacters(in: .whitespaces)
                    if p.hasSuffix(":") { lead = p; paragraph = [] } else { flushParagraph(); lead = nil }
                }
                itemNo += 1
                let id = flat ? "P\(out.items.count + 1)" : "P\(max(sectionNo, 1)).\(itemNo)"
                out.items.append(PlaybookItem(id: id, section: section, text: lead.map { "\($0) \(text)" } ?? text))
                continue
            }
            lead = nil
            paragraph.append(line)
        }
        flushTable()
        flushParagraph()
        out.items = dropOverview(out.items)
        return out
    }

    /// An "Agenda" list that only repeats the section headings below it is not a checklist of its own.
    static func dropOverview(_ items: [PlaybookItem]) -> [PlaybookItem] {
        let sections = Set(items.map(\.section))
        var out = items
        for s in sections {
            let mine = items.filter { $0.section == s }
            let others = Set(items.filter { $0.section != s }.map { $0.section.lowercased() })
            // "Abschluss" repeats "Abschluss (in den letzten Minuten)": a prefix match is enough.
            func repeatsHeading(_ text: String) -> Bool {
                let t = text.lowercased()
                return others.contains { $0.hasPrefix(t) || t.hasPrefix($0) }
            }
            if mine.count >= 2, mine.allSatisfy({ repeatsHeading($0.text) }) {
                out.removeAll { $0.section == s }
            }
        }
        return out
    }

    /// Replaces the block that starts with `block`'s first line (up to the next level-2 heading)
    /// in `existing`, or appends it. Saving twice therefore does not duplicate the tables.
    public static func merge(existing: String, block: String) -> String {
        let heading = block.components(separatedBy: .newlines).first ?? ""
        var lines = existing.components(separatedBy: .newlines)
        guard !heading.isEmpty, let start = lines.firstIndex(where: { $0.trimmingCharacters(in: .whitespaces) == heading }) else {
            let base = existing.trimmingCharacters(in: .whitespacesAndNewlines)
            return base.isEmpty ? block : base + "\n\n" + block
        }
        let end = lines[(start + 1)...].firstIndex { $0.hasPrefix("## ") } ?? lines.count
        lines.replaceSubrange(start..<end, with: block.components(separatedBy: .newlines) + (end < lines.count ? [""] : []))
        return lines.joined(separator: "\n").trimmingCharacters(in: .whitespacesAndNewlines)
    }

    /// All filled tables of a meeting as one Markdown block under a heading naming the template.
    public static func block(templateTitle: String, tables: [TemplateTable]) -> String? {
        let used = tables.filter { $0.filledCount > 0 }
        guard !used.isEmpty else { return nil }
        var s = "## \(templateTitle)\n"
        var section = ""
        for t in used {
            if t.section != section, !t.section.isEmpty { section = t.section; s += "\n### \(section)\n" }
            s += "\n" + t.markdown()
        }
        return s.trimmingCharacters(in: .whitespacesAndNewlines)
    }
}
