import Foundation

/// What Assist keeps with a recording: the context, the final panels, the transcript Claude saw,
/// and what was written to Aya. Stored as `assist.json` in the session folder.
public struct AssistRecord: Codable, Sendable {
    public var context: AssistContext
    public var state: AssistState
    public var lines: [AssistLine]
    /// The Aya meeting the recap was written to (an existing one, or one created for it).
    public var recapRecordID: String?
    public var recapUploadedAt: Date?
    public var crmAppliedAt: Date?
    public var createdContactIDs: [String] = []
    /// The template's tables after the meeting (completed by Claude, edited by the user).
    public var tables: [TemplateTable]?
    /// The recap as last drafted or edited, so it is still there when the meeting is opened again.
    public var recapDraft: [ProposedValue]?
    /// Live speaker names were carried over to the final transcript once.
    public var speakersCarried: Bool?

    public init(context: AssistContext, state: AssistState, lines: [AssistLine]) {
        self.context = context; self.state = state; self.lines = lines
    }

    public static func url(_ sessionID: String) -> URL { AppPaths.session(sessionID).appendingPathComponent("assist.json") }
    public static func load(_ sessionID: String) -> AssistRecord? { try? JSONIO.read(AssistRecord.self, from: url(sessionID)) }
    public func save(_ sessionID: String) throws { try JSONIO.write(self, to: AssistRecord.url(sessionID)) }
}

/// A value proposed for one Aya field, editable in the review screen.
public struct ProposedValue: Codable, Hashable, Identifiable, Sendable {
    public var key: String
    public var label: String
    public var kind: AyaField.Kind
    public var options: [String]
    public var old: String
    public var value: String
    public var reason: String?
    public var include: Bool
    public var id: String { key }
    public init(key: String, label: String, kind: AyaField.Kind, options: [String], old: String, value: String, reason: String?, include: Bool) {
        self.key = key; self.label = label; self.kind = kind; self.options = options
        self.old = old; self.value = value; self.reason = reason; self.include = include
    }
}

public struct ContactProposal: Codable, Hashable, Identifiable, Sendable {
    public var contactID: String?            // nil = new contact
    public var name: String
    public var changes: [ProposedValue]
    public var include: Bool
    public var id: String { contactID ?? "new-" + name }
    public init(contactID: String?, name: String, changes: [ProposedValue], include: Bool) {
        self.contactID = contactID; self.name = name; self.changes = changes; self.include = include
    }
}

public struct CRMProposal: Codable, Sendable {
    public var client: [ProposedValue]
    public var contacts: [ContactProposal]
    public init(client: [ProposedValue], contacts: [ContactProposal]) { self.client = client; self.contacts = contacts }
}

public enum RecapBuilder {

    /// The transcript as text lines, from the final transcript when there is one.
    public static func transcriptLines(sessionID: String, fallback: [AssistLine], myName: String) -> [String] {
        let url = AppPaths.session(sessionID).appendingPathComponent("transcript.json")
        guard let t = try? JSONIO.read(Transcript.self, from: url), !t.turns.isEmpty else { return fallback.map(\.rendered) }
        return t.turns.map { turn in
            AssistLine(start: turn.start, speaker: t.label(for: turn.speaker), text: turn.text).rendered
        }
    }

    // MARK: Recap

    /// Fields Claude fills in: writable content fields except those the app sets itself.
    public static func recapFields(_ schema: AyaListSchema, config: AyaConfig) -> [AyaField] {
        var appSet: Set<String> = [config.meetingDateField, config.meetingExternalIdField, config.meetingTypeField]
        // A dedicated field for the template's tables is filled by the app, not drafted.
        if let t = config.tablesField(in: schema), !t.hasPrefix("summary") { appSet.insert(t) }
        return schema.writableContent.filter { f in
            !appSet.contains(f.key) && f.kind != .datetime && f.kind != .date && !(f.kind == .select && f.options.isEmpty)
        }
    }

    static func propertySchema(_ f: AyaField) -> JSONValue {
        let desc = JSONValue.string(f.label + (f.kind == .markdown ? " (markdown)" : ""))
        switch f.kind {
        case .number: return .object(["type": .array([.string("number"), .string("null")]), "description": desc])
        case .boolean: return .object(["type": .array([.string("boolean"), .string("null")]), "description": desc])
        case .select: return .object(["enum": .array(f.options.map { .string($0) } + [.null]), "description": desc])
        default: return .object(["type": .array([.string("string"), .string("null")]), "description": desc])
        }
    }

    public static func objectSchema(_ fields: [AyaField]) -> JSONValue {
        var props: [String: JSONValue] = [:]
        for f in fields { props[f.key] = propertySchema(f) }
        return .object(["type": .string("object"), "properties": .object(props),
                        "required": .array(fields.map { .string($0.key) }), "additionalProperties": .bool(false)])
    }

    /// Asks Claude for the recap fields. Returns one proposed value per field. With `existing`
    /// (the Aya meeting the recap goes to), its current values are shown and fields that already
    /// have content are not overwritten unless the user ticks them.
    public static func draftRecap(binary: URL, model: String, record: AssistRecord, sessionID: String,
                                  schema: AyaListSchema, config: AyaConfig, existing: AyaRecord? = nil) async throws -> [ProposedValue] {
        let fields = recapFields(schema, config: config)
        guard !fields.isEmpty else { return [] }
        let transcript = transcriptLines(sessionID: sessionID, fallback: record.lines, myName: record.context.myName)
        var prompt = contextText(record) + "\n\nTRANSCRIPT:\n" + transcript.joined(separator: "\n")
        prompt += "\n\nFill in the meeting recap. Fields:\n"
        for f in fields {
            prompt += "- \(f.key): \(f.label)\(f.kind == .markdown ? " — markdown" : "")\(f.kind == .select ? " — one of: \(f.options.joined(separator: ", "))" : "")\n"
        }
        prompt += "The title should name the client or project and the topic. Attendee fields: names and roles of the external participants."
        if let t = record.context.playbookTitle, !record.context.playbookItems.isEmpty {
            prompt += " The meeting followed the template \"\(t)\"; where it fits, structure the summary along its agenda."
        }
        let r = try await ClaudeCLI.oneShot(binary: binary, system: AssistPrompts.recapSystem, prompt: prompt,
                                            model: model, effort: "medium", schema: objectSchema(fields))
        guard let out = r.structured?.object else { throw ClaudeCLI.CLIError.failed("no structured recap returned") }
        let spoken = transcript.joined(separator: "\n")
        let drafted = fields.map { f in
            let v = out[f.key] ?? .null
            var value = v.isNull ? "" : v.description
            var note: String?
            // Deadlines nobody said are taken out again, and the user is told.
            if f.kind == .markdown || f.kind == .text {
                let checked = DeadlineCheck.filter(value, transcript: spoken)
                if !checked.removed.isEmpty {
                    value = checked.text
                    note = "Removed deadlines not said in the meeting: " + checked.removed.map { "“\($0)”" }.joined(separator: ", ")
                }
            }
            return ProposedValue(key: f.key, label: f.label, kind: f.kind, options: f.options, old: "",
                                 value: value, reason: note, include: !v.isEmpty)
        }
        return target(drafted, existing: existing)
    }

    /// Shows the target meeting's current values and decides what is written by default: a field
    /// is ticked when the draft has something and the meeting has nothing there yet.
    public static func target(_ fields: [ProposedValue], existing: AyaRecord?) -> [ProposedValue] {
        fields.map { p in
            var p = p
            p.old = existing?[p.key].map { $0.isNull ? "" : $0.description } ?? ""
            p.include = !p.value.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && p.old.isEmpty
            return p
        }
    }

    /// Record values for saving the recap: the reviewed fields plus what the app fills in. For an
    /// existing meeting, links, date and type are only filled where the meeting has none yet, and
    /// team members are added to its participants.
    public static func recapValues(_ fields: [ProposedValue], record: AssistRecord, sessionID: String, startedAt: Date,
                                   schema: AyaListSchema, config: AyaConfig, existing: AyaRecord? = nil,
                                   memberIDs: [String] = [], tables: [TemplateTable] = []) -> [String: JSONValue] {
        var values: [String: JSONValue] = [:]
        for p in fields where p.include {
            if let v = schema.coerce(.string(p.value), for: p.key) { values[p.key] = v }
        }
        // Filled tables go under a heading named after the template, replacing that block if it
        // is already there (saving again does not duplicate them).
        if let key = config.tablesField(in: schema),
           let block = TemplateParser.block(templateTitle: record.context.playbookTitle ?? "Template", tables: tables) {
            let base = values[key]?.string ?? existing?[key]?.string ?? ""
            values[key] = .string(TemplateParser.merge(existing: base, block: block))
        }
        func isEmpty(_ key: String) -> Bool { existing?[key].map { $0.isEmpty } ?? true }
        func fill(_ key: String, _ v: JSONValue) {
            if schema.field(key) != nil, values[key] == nil, isEmpty(key) { values[key] = v }
        }
        let c = record.context
        fill(config.meetingDateField, .string(AyaText.iso(startedAt)))
        fill(config.meetingExternalIdField, .string("meetingassistant:\(sessionID)"))
        fill(config.meetingTitleField, .string(c.title))
        if let t = c.meetingType, schema.coerce(.string(t), for: config.meetingTypeField) != nil { fill(config.meetingTypeField, .string(t)) }
        func link(_ key: String?, _ id: String?) {
            guard let key, let id, schema.field(key) != nil else { return }
            fill(key, schema.linkValue(key, id: id))
        }
        link(config.meetingClientField, c.clientID)
        link(config.meetingProjectField, c.projectID)
        link(config.meetingTemplateField, c.playbookID)
        if let key = config.meetingParticipantsField, let f = schema.field(key), !memberIDs.isEmpty {
            let current = existing?.linkedIDs(key) ?? []
            let all = current + memberIDs.filter { !current.contains($0) }
            if all != current { values[key] = f.kind == .relations ? .array(all.map { .string($0) }) : .string(all[0]) }
        }
        return values
    }

    /// Completes the template's tables from the final transcript. Values the user set are kept;
    /// returns the tables with Claude's values and quotes.
    public static func completeTables(binary: URL, model: String, record: AssistRecord, sessionID: String,
                                      tables: [TemplateTable]) async throws -> [TemplateTable] {
        guard !tables.isEmpty else { return tables }
        let transcript = transcriptLines(sessionID: sessionID, fallback: record.lines, myName: record.context.myName)
        var prompt = contextText(record)
        if let notes = record.context.templateNotes, !notes.isEmpty { prompt += "\n\nTEMPLATE NOTES:\n" + notes.map { "- \($0)" }.joined(separator: "\n") }
        prompt += "\n\n" + tables.map { AssistPrompts.tableText($0, filledOnly: false) }.joined(separator: "\n")
        prompt += "\n\nTRANSCRIPT:\n" + transcript.joined(separator: "\n")
        prompt += """
        \n\nComplete the tables from the whole transcript. Return one entry per cell that the meeting answered: \
        fill empty cells, correct values from the live notes that turned out wrong, leave cells nobody talked about out. \
        Keep values short, in the meeting's language. For a row named "…:" (e.g. "Eigener Vorschlag:") put the proposal \
        into the name column. Values marked "(set by user)" are final; don't return them.
        """
        let columnNames = Array(Set(tables.flatMap(\.columns))).sorted()
        let rowIDs = tables.flatMap { $0.rows.map(\.id) }
        let schema = JSONValue.object([
            "type": .string("object"), "additionalProperties": .bool(false), "required": .array([.string("cells")]),
            "properties": .object(["cells": .object(["type": .string("array"), "items": .object([
                "type": .string("object"), "additionalProperties": .bool(false),
                "required": .array([.string("row"), .string("column"), .string("value"), .string("evidence")]),
                "properties": .object([
                    "row": .object(["enum": .array(rowIDs.map { .string($0) })]),
                    "column": .object(["enum": .array(columnNames.map { .string($0) })]),
                    "value": .object(["type": .string("string")]),
                    "evidence": .object(["type": .string("string"), "description": .string("short quote from the transcript")]),
                ])])])])])
        let r = try await ClaudeCLI.oneShot(binary: binary, system: AssistPrompts.tablesSystem, prompt: prompt,
                                            model: model, effort: "medium", schema: schema)
        guard let cells = r.structured?["cells"]?.array else { throw ClaudeCLI.CLIError.failed("no table values returned") }
        var out = tables
        for c in cells {
            guard let row = c["row"]?.string, let col = c["column"]?.string, let value = c["value"]?.string else { continue }
            let tid = String(row.split(separator: ".").first ?? "")
            guard let t = out.firstIndex(where: { $0.id == tid }) else { continue }
            out[t].set(row: row, column: col, value: value, evidence: c["evidence"]?.string)
        }
        return out
    }

    /// Team members among the attendees (and the user), matched by name.
    public static func memberIDs(attendees: [String], members: [AyaRecord]) -> [String] {
        func norm(_ s: String) -> String {
            s.folding(options: [.caseInsensitive, .diacriticInsensitive], locale: nil)
                .components(separatedBy: .whitespaces).filter { !$0.isEmpty }.joined(separator: " ")
        }
        var out: [String] = []
        for a in attendees.map(norm) where !a.isEmpty {
            // Full name, or a first name that matches exactly one member.
            let full = members.filter { norm($0.title) == a }
            let first = members.filter { norm($0.title).hasPrefix(a + " ") }
            if let m = full.first ?? (first.count == 1 ? first[0] : nil), !out.contains(m.id) { out.append(m.id) }
        }
        return out
    }

    // MARK: CRM

    /// Fields of a CRM record Claude may change: plain text, numbers, booleans and selects,
    /// minus technical fields (the same `TechnicalFields` that are hidden from the live context).
    static func editableFields(_ schema: AyaListSchema) -> [AyaField] {
        schema.writableContent.filter { f in
            f.kind != .datetime && f.kind != .date && !(f.kind == .select && f.options.isEmpty)
                && !TechnicalFields.contains(f.key)
        }
    }

    /// Values for creating a contact from a proposal: the ticked fields plus the link to the client,
    /// written as an id or a list of ids depending on the link field.
    public static func newContactValues(_ c: ContactProposal, clientID: String?, link: String?, schema: AyaListSchema) -> [String: JSONValue] {
        var values: [String: JSONValue] = [:]
        for ch in c.changes where ch.include { if let v = schema.coerce(.string(ch.value), for: ch.key) { values[ch.key] = v } }
        guard !values.isEmpty else { return values }
        if let link, let clientID { values[link] = schema.linkValue(link, id: clientID) }
        return values
    }

    static func newContactFields(_ schema: AyaListSchema) -> [AyaField] {
        let wanted = ["first_name", "last_name", "vorname", "nachname", "name__", "position", "rolle", "role", "email", "telefon", "phone", "title"]
        return editableFields(schema).filter { f in f.kind != .boolean && f.kind != .select && wanted.contains { f.key.hasPrefix($0) || f.key.contains($0) } }
    }

    /// Asks Claude which CRM fields to update, and which attendees to add as contacts.
    public static func proposeCRM(binary: URL, model: String, record: AssistRecord, sessionID: String,
                                  client: AyaRecord, contacts: [AyaRecord],
                                  clientSchema: AyaListSchema, contactSchema: AyaListSchema?) async throws -> CRMProposal {
        let cFields = editableFields(clientSchema)
        let pFields = contactSchema.map(editableFields) ?? []
        let newFields = contactSchema.map(newContactFields) ?? []
        let transcript = transcriptLines(sessionID: sessionID, fallback: record.lines, myName: record.context.myName)

        func current(_ r: AyaRecord, _ fields: [AyaField]) -> String {
            fields.map { f in "  \(f.key) (\(f.label)\(f.kind == .select ? "; one of: " + f.options.joined(separator: ", ") : "")): \(AssistPrompts.clip(r[f.key]?.description ?? "", 1500))" }
                .joined(separator: "\n")
        }
        var prompt = "CLIENT RECORD \(client.title) — current values:\n" + current(client, cFields)
        if !contacts.isEmpty {
            prompt += "\n\nKNOWN CONTACTS — current values:\n"
            for c in contacts { prompt += "contact_id \(c.id) — \(c.title):\n" + current(c, pFields) + "\n" }
        }
        prompt += "\n\nATTENDEES SEEN BY THE ASSISTANT:\n" + record.state.attendees.map {
            "- \($0.name)\($0.role.map { ", \($0)" } ?? "")\($0.company.map { ", \($0)" } ?? "")\($0.contactID.map { " [contact_id \($0)]" } ?? " [not in CRM]")\($0.notes.map { ": \($0)" } ?? "")"
        }.joined(separator: "\n")
        prompt += "\n\nMEETING SUMMARY:\n" + record.state.summary.map { "- \($0)" }.joined(separator: "\n")
        prompt += "\n\nTRANSCRIPT:\n" + transcript.joined(separator: "\n")
        prompt += """
        \n\nPropose updates: client_changes for the client record, contact_changes for known contacts, \
        new_contacts for external attendees who are not in the CRM yet (only real people from the client side, not the user). \
        Use empty arrays when nothing should change.
        """

        func changeSchema(_ fields: [AyaField], withContact: Bool, ids: [String]) -> JSONValue {
            var props: [String: JSONValue] = [
                "field": .object(["enum": .array(fields.map { .string($0.key) })]),
                "new_value": .object(["type": .string("string")]),
                "reason": .object(["type": .string("string")]),
            ]
            var req: [JSONValue] = [.string("field"), .string("new_value"), .string("reason")]
            if withContact { props["contact_id"] = .object(["enum": .array(ids.map { .string($0) })]); req.append(.string("contact_id")) }
            return .object(["type": .string("array"), "items": .object(["type": .string("object"), "properties": .object(props),
                                                                         "required": .array(req), "additionalProperties": .bool(false)])])
        }
        var top: [String: JSONValue] = ["client_changes": cFields.isEmpty ? .object(["type": .string("array"), "maxItems": .number(0)]) : changeSchema(cFields, withContact: false, ids: [])]
        if !contacts.isEmpty && !pFields.isEmpty { top["contact_changes"] = changeSchema(pFields, withContact: true, ids: contacts.map(\.id)) }
        if !newFields.isEmpty {
            var nprops: [String: JSONValue] = [:]
            for f in newFields { nprops[f.key] = .object(["type": .array([.string("string"), .string("null")]), "description": .string(f.label)]) }
            top["new_contacts"] = .object(["type": .string("array"), "items": .object([
                "type": .string("object"), "properties": .object(nprops),
                "required": .array(newFields.map { .string($0.key) }), "additionalProperties": .bool(false)])])
        }
        let schema = JSONValue.object(["type": .string("object"), "properties": .object(top),
                                       "required": .array(top.keys.sorted().map { .string($0) }), "additionalProperties": .bool(false)])

        let r = try await ClaudeCLI.oneShot(binary: binary, system: AssistPrompts.crmSystem, prompt: prompt,
                                            model: model, effort: "medium", schema: schema)
        guard let out = r.structured else { throw ClaudeCLI.CLIError.failed("no structured CRM proposal returned") }

        func proposed(_ item: JSONValue, schema: AyaListSchema, record: AyaRecord) -> ProposedValue? {
            guard let key = item["field"]?.string, let f = schema.field(key) else { return nil }
            let new = item["new_value"]?.description ?? ""
            let old = record[key]?.description ?? ""
            guard new != old, schema.coerce(.string(new), for: key) != nil else { return nil }
            return ProposedValue(key: key, label: f.label, kind: f.kind, options: f.options, old: old, value: new,
                                 reason: item["reason"]?.string, include: false)
        }
        let clientChanges = (out["client_changes"]?.array ?? []).compactMap { proposed($0, schema: clientSchema, record: client) }
        var contactProps: [ContactProposal] = []
        if let cs = contactSchema {
            var byContact: [String: [ProposedValue]] = [:]
            for item in out["contact_changes"]?.array ?? [] {
                guard let cid = item["contact_id"]?.string, let c = contacts.first(where: { $0.id == cid }),
                      let p = proposed(item, schema: cs, record: c) else { continue }
                byContact[cid, default: []].append(p)
            }
            for c in contacts { if let ch = byContact[c.id] { contactProps.append(ContactProposal(contactID: c.id, name: c.title, changes: ch, include: false)) } }
            for item in out["new_contacts"]?.array ?? [] {
                let changes = newFields.compactMap { f -> ProposedValue? in
                    let v = item[f.key]?.description ?? ""
                    guard !v.isEmpty else { return nil }
                    return ProposedValue(key: f.key, label: f.label, kind: f.kind, options: [], old: "", value: v, reason: nil, include: true)
                }
                guard !changes.isEmpty else { continue }
                let name = changes.filter { $0.key.contains("name") }.map(\.value).joined(separator: " ")
                contactProps.append(ContactProposal(contactID: nil, name: name.isEmpty ? changes[0].value : name, changes: changes, include: false))
            }
        }
        return CRMProposal(client: clientChanges, contacts: contactProps)
    }

    static func contextText(_ r: AssistRecord) -> String {
        var s = ""
        if r.context.clientID != nil { s += "CLIENT: \(r.context.clientTitle)\n" }
        if let p = r.context.projectTitle { s += "PROJECT: \(p)\n" }
        if let m = r.context.meetingTitle { s += "MEETING: \(m)\n" }
        if let p = r.context.playbookTitle { s += "TEMPLATE: \(p)\n" }
        if !r.context.goal.isEmpty { s += "GOAL: \(r.context.goal)\n" }
        s += "USER (\"Me\" in the transcript): \(r.context.myName)\n"
        s += "\nATTENDEES:\n" + r.state.attendees.map { "- \($0.name)\($0.role.map { ", \($0)" } ?? "")\($0.company.map { ", \($0)" } ?? "")" }.joined(separator: "\n")
        s += "\n\nLIVE SUMMARY:\n" + r.state.summary.map { "- \($0)" }.joined(separator: "\n")
        s += "\n\nOPEN POINTS:\n" + r.state.openPoints.map { "- [\($0.status.rawValue)] \($0.text)" }.joined(separator: "\n")
        s += "\n\nQUESTIONS ASKED:\n" + r.state.askedQuestions.map { "- \($0.text)" }.joined(separator: "\n")
        return s
    }
}
