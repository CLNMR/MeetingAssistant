import Foundation

// MARK: - Context

/// One checklist item of a playbook, e.g. P3.2 = section 3, item 2.
public struct PlaybookItem: Codable, Hashable, Identifiable, Sendable {
    public enum Status: String, Codable, Sendable { case missing, partial, covered }
    public var id: String
    public var section: String
    public var text: String
    public var status: Status = .missing
    public var evidence: String?
    public init(id: String, section: String, text: String) { self.id = id; self.section = section; self.text = text }
}

public enum PlaybookParser {
    /// Turns playbook markdown into checklist items. Headings (`### 1. Einstieg`) start a section,
    /// bullets or numbered lines become items. Text without headings becomes P1…Pn.
    public static func items(_ markdown: String) -> [PlaybookItem] {
        var out: [PlaybookItem] = []
        var section = "", sectionNo = 0, itemNo = 0
        var flat = true
        for raw in markdown.components(separatedBy: .newlines) {
            let line = raw.trimmingCharacters(in: .whitespaces)
            if line.isEmpty { continue }
            if line.hasPrefix("#") {
                flat = false
                section = stripNumber(String(line.drop(while: { $0 == "#" })).trimmingCharacters(in: .whitespaces))
                sectionNo += 1; itemNo = 0
                continue
            }
            guard let text = bulletText(line) else { continue }
            itemNo += 1
            let id = flat ? "P\(out.count + 1)" : "P\(max(sectionNo, 1)).\(itemNo)"
            out.append(PlaybookItem(id: id, section: section, text: text))
        }
        return out
    }

    static func bulletText(_ line: String) -> String? {
        for p in ["- [ ] ", "- [x] ", "* [ ] ", "- ", "* ", "• "] where line.hasPrefix(p) {
            return String(line.dropFirst(p.count)).trimmingCharacters(in: .whitespaces)
        }
        // "1. text" / "1) text"
        let digits = line.prefix { $0.isNumber }
        if !digits.isEmpty, line.count > digits.count + 1 {
            let rest = line.dropFirst(digits.count)
            if rest.hasPrefix(". ") || rest.hasPrefix(") ") { return String(rest.dropFirst(2)).trimmingCharacters(in: .whitespaces) }
        }
        return nil
    }

    static func stripNumber(_ s: String) -> String {
        var t = Substring(s)
        let digits = t.prefix { $0.isNumber || $0 == "." }
        if !digits.isEmpty { t = t.dropFirst(digits.count) }
        return t.trimmingCharacters(in: .whitespaces)
    }
}

/// Everything Claude is told before the meeting starts. Built from Aya, stored with the session.
public struct AssistContext: Codable, Sendable {
    public var clientID: String
    public var clientTitle: String
    public var clientFieldList: [LabeledValue]
    public var contacts: [ContactInfo]
    public var playbookID: String?
    public var playbookTitle: String?
    public var playbookGoal: String?
    public var playbookItems: [PlaybookItem]
    public var recaps: [RecapSummary]
    public var goal: String
    public var mode: MeetingMode
    public var myName: String
    public var date: Date

    public struct LabeledValue: Codable, Hashable, Sendable { public var key: String; public var label: String; public var value: String }
    public struct ContactInfo: Codable, Hashable, Sendable, Identifiable {
        public var id: String; public var name: String; public var details: [LabeledValue]
    }
    public struct RecapSummary: Codable, Hashable, Sendable {
        public var title: String; public var date: String; public var fields: [LabeledValue]
    }

    public init(clientID: String, clientTitle: String, clientFieldList: [LabeledValue], contacts: [ContactInfo],
                playbookID: String?, playbookTitle: String?, playbookGoal: String?, playbookItems: [PlaybookItem],
                recaps: [RecapSummary], goal: String, mode: MeetingMode, myName: String, date: Date) {
        self.clientID = clientID; self.clientTitle = clientTitle; self.clientFieldList = clientFieldList
        self.contacts = contacts; self.playbookID = playbookID; self.playbookTitle = playbookTitle
        self.playbookGoal = playbookGoal; self.playbookItems = playbookItems; self.recaps = recaps
        self.goal = goal; self.mode = mode; self.myName = myName; self.date = date
    }

    /// Tracking, usage and bookkeeping fields that don't help in a conversation.
    static func isTechnical(_ key: String) -> Bool {
        let k = key.lowercased()
        let parts = ["utm_", "gclid", "ga_client", "consent", "opt_out", "external_id", "ai_enriched", "sitzungen",
                     "letzte_anmeldung", "erstellt_am", "createdat", "updatedat", "anz_", "aya_nutzer", "marketing",
                     "ist_aktiviert", "ist_intern", "nutzungsphase", "aktivitaets", "beta_", "intern_manuell", "do_not_contact",
                     "demo_reif", "source", "__record_spec", "kunde_praefix", "firma_unbekannt", "abrechnungskonten",
                     "organisationen", "_at__value", "owner"]
        return parts.contains { k.contains($0) }
    }

    /// Builds the context from Aya records. Only fields with content are kept.
    public init(aya: AyaMeetingContext, config: AyaConfig, goal: String, mode: MeetingMode, myName: String, date: Date) {
        func values(_ r: AyaRecord, schema: AyaListSchema?, skip: Set<String> = []) -> [LabeledValue] {
            var out: [LabeledValue] = []
            for (k, v) in r.values.sorted(by: { $0.key < $1.key }) {
                guard !skip.contains(k), !v.isEmpty, !AssistContext.isTechnical(k) else { continue }
                let f = schema?.field(k)
                // Relation ids mean nothing to Claude; their titles do.
                if k.hasSuffix("__id") || k.hasSuffix("__ids") || k.hasSuffix("_color") || k.hasSuffix("__file_count") { continue }
                if ["id", "computedTitle", "createdBy", "modifiedBy", "locked"].contains(k) { continue }
                let label = k.contains("__computed_title")
                    ? AyaField.humanize(k.components(separatedBy: "__").first ?? k)
                    : (f?.label ?? AyaField.humanize(k.components(separatedBy: "__").first ?? k))
                var text = v.description
                if k == "createdAt" || k == "updatedAt" || f?.kind == .datetime { text = String(text.prefix(10)) }
                if k == "createdByTitle" || k == "modifiedByTitle" { continue }
                out.append(LabeledValue(key: k, label: label, value: text))
            }
            return out
        }
        clientID = aya.client.id
        clientTitle = aya.client.title
        clientFieldList = values(aya.client, schema: aya.clientSchema)
        contacts = aya.contacts.map { c in
            ContactInfo(id: c.id, name: c.title, details: values(c, schema: aya.contactSchema,
                                                                 skip: ["gclid__value", "ga_client_id__value", "utm_source__value", "utm_medium__value"]))
        }
        if let p = aya.playbook {
            playbookID = p.id
            playbookTitle = p.title
            playbookGoal = p[config.playbookGoalField]?.string
            playbookItems = PlaybookParser.items(p[config.playbookStepsField]?.string ?? "")
        } else {
            playbookID = nil; playbookTitle = nil; playbookGoal = nil; playbookItems = []
        }
        recaps = aya.recaps.map { r in
            let date = r[config.recapDateField]?.string ?? r.values["createdAt"]?.string ?? ""
            return RecapSummary(title: r.title, date: String(date.prefix(10)),
                                fields: values(r, schema: aya.recapSchema,
                                               skip: [config.recapTitleField, config.recapDateField, config.recapExternalIdField,
                                                      "createdAt", "updatedAt"]))
        }
        self.goal = goal; self.mode = mode; self.myName = myName; self.date = date
    }
}

// MARK: - State shown in the Assist window

public struct AssistState: Codable, Sendable, Equatable {
    public struct Question: Codable, Hashable, Identifiable, Sendable {
        public enum Status: String, Codable, Sendable { case suggested, asked, dismissed }
        public var id: String
        public var text: String
        public var why: String?
        public var playbookItem: String?
        public var status: Status = .suggested
    }
    public struct OpenPoint: Codable, Hashable, Identifiable, Sendable {
        public enum Status: String, Codable, Sendable { case open, answered, parked }
        public var id: String
        public var text: String
        public var raisedBy: String?
        public var status: Status = .open
    }
    public struct Attendee: Codable, Hashable, Identifiable, Sendable {
        public var name: String
        public var role: String?
        public var company: String?
        public var notes: String?
        public var speaker: String?
        public var contactID: String?       // set when the person is a known Aya contact
        public var present: Bool?           // false: only mentioned or expected
        public var id: String { name.lowercased() }
    }

    public var questions: [Question] = []          // current suggestions, best first
    public var askedQuestions: [Question] = []
    public var summary: [String] = []
    public var openPoints: [OpenPoint] = []
    public var attendees: [Attendee] = []
    public var playbook: [PlaybookItem] = []
    public var updates = 0
    public var costUSD = 0.0                        // API-equivalent cost, reported by the CLI
    public var changed: Set<String> = []            // ids touched by the latest update (for highlighting)

    public init() {}
}

/// One line of Claude's output. Claude writes one JSON object per line.
public enum AssistOp: Equatable, Sendable {
    case nextQuestions([AssistState.Question])
    case summaryAppend([String])
    case summaryReplace(index: Int, text: String)
    case openPoint(AssistState.OpenPoint)
    case attendee(AssistState.Attendee)
    case playbook(item: String, status: PlaybookItem.Status, evidence: String?)

    /// Parses one output line. Returns nil for anything that isn't a valid op (prose, fences, partial JSON).
    public static func parse(_ line: String) -> AssistOp? {
        var s = line.trimmingCharacters(in: .whitespaces)
        if s.hasPrefix("```") || s.isEmpty { return nil }
        if s.hasSuffix(",") { s.removeLast() }
        guard s.hasPrefix("{"), let v = try? JSONValue.parse(s), let op = v["op"]?.string else { return nil }
        func str(_ k: String, _ o: JSONValue = v) -> String? {
            let t = o[k]?.string?.trimmingCharacters(in: .whitespacesAndNewlines)
            return t?.isEmpty == false ? t : nil
        }
        switch op {
        case "next_questions":
            let items = (v["items"]?.array ?? []).enumerated().compactMap { i, q -> AssistState.Question? in
                guard let text = str("text", q) else { return nil }
                return AssistState.Question(id: str("id", q) ?? "Q\(i + 1)", text: text, why: str("why", q),
                                            playbookItem: str("playbook_item", q))
            }
            return items.isEmpty ? nil : .nextQuestions(items)
        case "summary_append":
            let b = (v["bullets"]?.array ?? []).compactMap { $0.string?.trimmingCharacters(in: .whitespacesAndNewlines) }.filter { !$0.isEmpty }
            return b.isEmpty ? nil : .summaryAppend(b)
        case "summary_replace":
            guard let i = v["index"]?.number, let t = str("text") else { return nil }
            return .summaryReplace(index: Int(i), text: t)
        case "open_point":
            guard let id = str("id"), let text = str("text") else { return nil }
            return .openPoint(.init(id: id, text: text, raisedBy: str("raised_by"),
                                    status: AssistState.OpenPoint.Status(rawValue: str("status") ?? "open") ?? .open))
        case "attendee":
            guard let name = str("name") else { return nil }
            return .attendee(.init(name: name, role: str("role"), company: str("company"), notes: str("notes"),
                                   speaker: str("speaker"), contactID: str("contact_id"), present: v["present"]?.bool))
        case "playbook":
            guard let item = str("item"), let st = PlaybookItem.Status(rawValue: str("status") ?? "") else { return nil }
            return .playbook(item: item, status: st, evidence: str("evidence"))
        default:
            return nil
        }
    }
}

extension AssistState {
    /// Applies one op. Returns the ids it changed.
    @discardableResult
    public mutating func apply(_ op: AssistOp) -> [String] {
        switch op {
        case .nextQuestions(let qs):
            // Questions are numbered per update by Claude; give them unique ids.
            let stamp = updates
            questions = qs.enumerated().map { i, q in var q = q; q.id = "u\(stamp)-\(i)"; return q }
            return questions.map(\.id) + ["questions"]
        case .summaryAppend(let b):
            let start = summary.count
            summary += b
            return (start..<summary.count).map { "s\($0)" }
        case .summaryReplace(let i, let t):
            guard summary.indices.contains(i) else { summary.append(t); return ["s\(summary.count - 1)"] }
            summary[i] = t
            return ["s\(i)"]
        case .openPoint(let p):
            if let i = openPoints.firstIndex(where: { $0.id == p.id }) {
                var n = p
                if n.raisedBy == nil { n.raisedBy = openPoints[i].raisedBy }
                openPoints[i] = n
            } else { openPoints.append(p) }
            return ["o:\(p.id)"]
        case .attendee(let a):
            if let i = attendees.firstIndex(where: { $0.id == a.id || ($0.speaker != nil && $0.speaker == a.speaker && a.speaker != nil && $0.contactID == nil) }) {
                var m = attendees[i]
                m.name = a.name
                m.role = a.role ?? m.role; m.company = a.company ?? m.company
                m.notes = a.notes ?? m.notes; m.speaker = a.speaker ?? m.speaker; m.contactID = a.contactID ?? m.contactID
                m.present = a.present ?? m.present ?? (a.speaker != nil ? true : nil)
                attendees[i] = m
            } else { attendees.append(a) }
            return ["a:\(a.id)"]
        case .playbook(let item, let status, let evidence):
            guard let i = playbook.firstIndex(where: { $0.id == item }) else { return [] }
            playbook[i].status = status
            if let evidence { playbook[i].evidence = evidence }
            return ["p:\(item)"]
        }
    }
}
