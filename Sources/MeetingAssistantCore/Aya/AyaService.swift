import Foundation

/// On-disk cache for Aya data: list schemas and the records shown in the pickers.
/// Every Aya call costs API credits, so pickers read this cache and refresh on demand.
public enum AyaCache {
    static var dir: URL { AppPaths.root.appendingPathComponent("Aya", isDirectory: true) }
    static var schemaFile: URL { dir.appendingPathComponent("schemas.json") }
    static func listFile(_ slot: AyaSlot) -> URL { dir.appendingPathComponent("list-\(slot.rawValue).json") }

    public struct CachedList: Codable, Sendable {
        public var ref: AyaListRef
        public var records: [AyaRecord]
        public var fetchedAt: Date
    }

    public static func schemas() -> [String: AyaListSchema] {
        (try? JSONIO.read([String: AyaListSchema].self, from: schemaFile)) ?? [:]
    }
    public static func schema(_ slot: AyaSlot) -> AyaListSchema? { schemas()[slot.rawValue] }
    public static func saveSchemas(_ s: [String: AyaListSchema]) throws {
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        try JSONIO.write(s, to: schemaFile)
    }

    public static func list(_ slot: AyaSlot) -> CachedList? { try? JSONIO.read(CachedList.self, from: listFile(slot)) }
    public static func saveList(_ slot: AyaSlot, _ list: CachedList) throws {
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        try JSONIO.write(list, to: listFile(slot))
    }

    public static func clear() { try? FileManager.default.removeItem(at: dir) }
}

/// Everything about one meeting's context that comes from Aya. Every part is optional: a
/// meeting can be about a client, a project, both, or neither (an internal meeting).
public struct AyaMeetingContext: Codable, Sendable {
    public var client: AyaRecord?
    public var contacts: [AyaRecord]
    public var project: AyaRecord?
    public var template: AyaRecord?
    /// The planned meeting in Aya this meeting belongs to.
    public var meeting: AyaRecord?
    /// Earlier meetings with the client or in the project, newest first.
    public var recaps: [AyaRecord]
    public var clientSchema: AyaListSchema?
    public var contactSchema: AyaListSchema?
    public var projectSchema: AyaListSchema?
    public var recapSchema: AyaListSchema?

    public init(client: AyaRecord? = nil, contacts: [AyaRecord] = [], project: AyaRecord? = nil, template: AyaRecord? = nil,
                meeting: AyaRecord? = nil, recaps: [AyaRecord] = [], clientSchema: AyaListSchema? = nil,
                contactSchema: AyaListSchema? = nil, projectSchema: AyaListSchema? = nil, recapSchema: AyaListSchema? = nil) {
        self.client = client; self.contacts = contacts; self.project = project; self.template = template
        self.meeting = meeting; self.recaps = recaps; self.clientSchema = clientSchema; self.contactSchema = contactSchema
        self.projectSchema = projectSchema; self.recapSchema = recapSchema
    }
}

/// Higher-level Aya operations built on `AyaClient` and the stored `AyaConfig`.
public struct AyaService: Sendable {
    public let config: AyaConfig
    public let client: AyaClient

    public init(config: AyaConfig, client: AyaClient) { self.config = config; self.client = client }

    public init?(config: AyaConfig = .load()) {
        guard let c = config.client() else { return nil }
        self.init(config: config, client: c)
    }

    /// Picks the default list for every slot that is still empty, and moves lists whose record
    /// type now lives in another workspace. A record type's home workspace (`workspaceId` on the
    /// spec) is used, because that is where its fields are defined.
    public func autoSelectLists() async throws -> AyaConfig {
        var cfg = config
        let workspaces = try await client.workspaces()
        guard let first = workspaces.first else {
            throw AyaError.notFound("No workspaces found for this organization id. Check the id: it is part of the Aya URL, app.hey-aya.ai/org/<id>/…")
        }
        let specs = try await client.recordSpecs(workspace: first.id)
        for slot in AyaSlot.allCases {
            let tech = cfg.list(slot)?.techName ?? slot.defaultTechName
            guard let spec = specs.first(where: { $0.techName == tech }) else { continue }
            let ws = workspaces.first { $0.id == spec.workspaceId } ?? first
            if cfg.list(slot)?.workspaceId != ws.id || cfg.list(slot) == nil {
                cfg.setList(slot, AyaListRef(workspaceId: ws.id, workspaceLabel: ws.label, techName: spec.techName, label: spec.label))
            }
        }
        return cfg
    }

    /// Reads the field schema of every configured list from the workspaces' OpenAPI documents
    /// and learns select options from up to 200 existing records per list.
    @discardableResult
    public func refreshSchemas() async throws -> [String: AyaListSchema] {
        var out: [String: AyaListSchema] = [:]
        var docs: [String: Data] = [:]
        for slot in AyaSlot.allCases {
            guard let ref = config.list(slot) else { continue }
            if docs[ref.workspaceId] == nil {
                do { docs[ref.workspaceId] = try await client.openAPI(workspace: ref.workspaceId) }
                catch AyaError.notFound {
                    throw AyaError.notFound("API documentation is switched off. Turn it on in the Aya organization settings.")
                }
            }
            var schema = try AyaListSchema.parse(openAPI: docs[ref.workspaceId]!, workspaceId: ref.workspaceId, techName: ref.techName)
            let sample = (try? await client.records(workspace: ref.workspaceId, list: ref.techName,
                                                     query: AyaQuery(sortField: "system", sortValueKey: "updatedAt",
                                                                     descending: true, limit: 200))) ?? []
            schema.learnOptions(from: sample)
            out[slot.rawValue] = schema
        }
        try AyaCache.saveSchemas(out)
        return out
    }

    /// Reloads one list for the pickers (clients, projects, templates, members).
    @discardableResult
    public func refreshList(_ slot: AyaSlot) async throws -> [AyaRecord] {
        guard let ref = config.list(slot) else { throw AyaError.notConfigured }
        var records = try await client.allRecords(workspace: ref.workspaceId, list: ref.techName, max: 5000)
        records.sort { $0.title.localizedCaseInsensitiveCompare($1.title) == .orderedAscending }
        // Templates are few and used whole; of the rest keep only what the pickers show.
        let slim = slot == .templates ? records : records.map { r in
            AyaRecord(values: r.values.filter { ["id", "computedTitle", "updatedAt"].contains($0.key) || isPreviewField($0.key) })
        }
        try AyaCache.saveList(slot, .init(ref: ref, records: slim, fetchedAt: Date()))
        return slim
    }

    private func isPreviewField(_ key: String) -> Bool {
        ["branche__value", "domain__value", "region__value", "area__value", "status__value", "goal__value"].contains(key)
    }

    public func search(_ slot: AyaSlot, text: String) async throws -> [AyaRecord] {
        guard let ref = config.list(slot) else { throw AyaError.notConfigured }
        return try await client.search(workspace: ref.workspaceId, list: ref.techName, text: text, limit: 20)
    }

    private func brick(_ key: String, _ kind: AyaField.Kind, _ slot: AyaSlot) -> String {
        AyaCache.schema(slot)?.field(key)?.brickTechName ?? AyaField(key: key, kind: kind, writable: true).brickTechName
    }

    /// Meetings in Aya dated from `from` to `to`, earliest first.
    public func meetings(from: Date, to: Date, limit: Int = 100) async throws -> [AyaRecord] {
        guard let ref = config.list(.meetings) else { throw AyaError.notConfigured }
        let dateKey = config.meetingDateField
        let field = brick(dateKey, .datetime, .meetings)
        let found = try await client.records(workspace: ref.workspaceId, list: ref.techName,
                                             query: AyaQuery(condition: .init(field: field, valueKey: "value", comparator: "greaterThanEqual",
                                                                              value: AyaText.iso(from)),
                                                             sortField: field, descending: false, limit: limit))
        return found.filter { r in r.date(dateKey).map { $0 < to } ?? false }
    }

    /// Planned meetings: from the start of today through the next `days` days.
    public func upcomingMeetings(days: Int = 14, now: Date = Date()) async throws -> [AyaRecord] {
        let cal = Calendar.current
        let start = cal.startOfDay(for: now)
        return try await meetings(from: start, to: cal.date(byAdding: .day, value: days + 1, to: start)!)
    }

    /// Meetings on the day of `date` (for attaching a recording to a meeting created elsewhere).
    public func meetings(onDayOf date: Date) async throws -> [AyaRecord] {
        let cal = Calendar.current
        let start = cal.startOfDay(for: date)
        return try await meetings(from: start, to: cal.date(byAdding: .day, value: 1, to: start)!)
    }

    public func record(_ slot: AyaSlot, id: String) async throws -> AyaRecord {
        guard let ref = config.list(slot) else { throw AyaError.notConfigured }
        return try await client.record(workspace: ref.workspaceId, list: ref.techName, id: id)
    }

    /// The full context for a meeting. A planned meeting's own client, project and template are
    /// used unless others are given.
    public func meetingContext(meetingID: String? = nil, clientID: String? = nil, projectID: String? = nil,
                               templateID: String? = nil, recapLimit: Int = 3) async throws -> AyaMeetingContext {
        let schemas = AyaCache.schemas()
        var ctx = AyaMeetingContext(clientSchema: schemas[AyaSlot.clients.rawValue], contactSchema: schemas[AyaSlot.contacts.rawValue],
                                    projectSchema: schemas[AyaSlot.projects.rawValue], recapSchema: schemas[AyaSlot.meetings.rawValue])
        var clientID = clientID, projectID = projectID, templateID = templateID
        if let meetingID {
            let m = try await record(.meetings, id: meetingID)
            ctx.meeting = m
            clientID = clientID ?? config.meetingClientField.flatMap { m.linkedID($0) }
            projectID = projectID ?? config.meetingProjectField.flatMap { m.linkedID($0) }
            templateID = templateID ?? config.meetingTemplateField.flatMap { m.linkedID($0) }
        }
        if let clientID { ctx.client = try await record(.clients, id: clientID) }
        if let projectID, config.list(.projects) != nil { ctx.project = try? await record(.projects, id: projectID) }
        if let templateID { ctx.template = try await record(.templates, id: templateID) }

        if let clientID, let ref = config.list(.contacts), let link = config.contactsClientField {
            let field = schemas[AyaSlot.contacts.rawValue]?.field(link) ?? AyaField(key: link, kind: .relation, writable: true)
            ctx.contacts = (try? await client.records(workspace: ref.workspaceId, list: ref.techName,
                                                      query: AyaQuery(condition: .init(field: field.brickTechName, valueKey: "id", value: clientID),
                                                                      limit: 50))) ?? []
        }
        let earlier = (try? await earlierMeetings(client: ctx.client, projectID: projectID, limit: recapLimit + 1)) ?? []
        ctx.recaps = Array(earlier.filter { $0.id != meetingID }.prefix(recapLimit))
        return ctx
    }

    /// Earlier meetings with the client (by the meeting → client link, else by the client's name),
    /// or in the project, newest first. Only meetings dated before now.
    public func earlierMeetings(client company: AyaRecord?, projectID: String?, limit: Int) async throws -> [AyaRecord] {
        guard let ref = config.list(.meetings) else { return [] }
        let dateKey = config.meetingDateField
        let dateSort = brick(dateKey, .datetime, .meetings)
        let now = Date()
        func past(_ rs: [AyaRecord]) -> [AyaRecord] { rs.filter { ($0.date(dateKey) ?? .distantPast) <= now } }
        func linked(_ link: String, _ id: String) async throws -> [AyaRecord] {
            let field = brick(link, link.hasSuffix("__ids") ? .relations : .relation, .meetings)
            return past(try await client.records(workspace: ref.workspaceId, list: ref.techName,
                                                 query: AyaQuery(condition: .init(field: field, valueKey: "id",
                                                                                  comparator: link.hasSuffix("__ids") ? "containsAny" : "equal", value: id),
                                                                 sortField: dateSort, descending: true, limit: limit + 3)))
        }
        var out: [AyaRecord] = []
        if let company, let link = config.meetingClientField { out = try await linked(link, company.id) }
        if out.isEmpty, let projectID, let link = config.meetingProjectField { out = try await linked(link, projectID) }
        if out.isEmpty, let company {
            // Meetings from before the client link existed: find them by the client's name.
            let name = AyaService.searchName(company.title)
            if name.count >= 3 {
                let found = try await self.client.search(workspace: ref.workspaceId, list: ref.techName, text: name, limit: 20)
                out = past(found).sorted { ($0.date(dateKey) ?? .distantPast) > ($1.date(dateKey) ?? .distantPast) }
            }
        }
        return Array(out.prefix(limit))
    }
    /// "Müller Maschinenbau GmbH & Co. KG" → "Müller Maschinenbau".
    public static func searchName(_ title: String) -> String {
        let suffixes = ["gmbh & co. kg", "gmbh & co kg", "gmbh", "ag", "kg", "se", "ug", "e.v.", "ltd", "inc", "llc", "co. kg", "mbh"]
        var t = title.trimmingCharacters(in: .whitespaces)
        var changed = true
        while changed {
            changed = false
            for s in suffixes where t.lowercased().hasSuffix(" " + s) {
                t = String(t.dropLast(s.count + 1)).trimmingCharacters(in: .whitespaces); changed = true
            }
        }
        return t
    }
}
