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

/// Everything about one meeting's context that comes from Aya.
public struct AyaMeetingContext: Codable, Sendable {
    public var client: AyaRecord
    public var contacts: [AyaRecord]
    public var playbook: AyaRecord?
    public var recaps: [AyaRecord]
    public var clientSchema: AyaListSchema?
    public var contactSchema: AyaListSchema?
    public var recapSchema: AyaListSchema?
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

    /// Picks the default list for every slot that is still empty. A record type's home workspace
    /// (`workspaceId` on the spec) is used, because that is where its fields are defined.
    public func autoSelectLists() async throws -> AyaConfig {
        var cfg = config
        let workspaces = try await client.workspaces()
        guard let first = workspaces.first else {
            throw AyaError.notFound("No workspaces found for this organization id. Check the id: it is part of the Aya URL, app.hey-aya.ai/org/<id>/…")
        }
        let specs = try await client.recordSpecs(workspace: first.id)
        for slot in AyaSlot.allCases where cfg.list(slot) == nil {
            guard let spec = specs.first(where: { $0.techName == slot.defaultTechName }) else { continue }
            let ws = workspaces.first { $0.id == spec.workspaceId } ?? first
            cfg.setList(slot, AyaListRef(workspaceId: ws.id, workspaceLabel: ws.label, techName: spec.techName, label: spec.label))
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

    /// Reloads one list for the pickers (clients and playbooks).
    @discardableResult
    public func refreshList(_ slot: AyaSlot) async throws -> [AyaRecord] {
        guard let ref = config.list(slot) else { throw AyaError.notConfigured }
        var records = try await client.allRecords(workspace: ref.workspaceId, list: ref.techName, max: 5000)
        records.sort { $0.title.localizedCaseInsensitiveCompare($1.title) == .orderedAscending }
        // Keep only what the picker shows; full records are fetched when a meeting starts.
        let slim = records.map { r in
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

    /// The full context for a meeting: client, its contacts, the playbook and the latest recaps.
    public func meetingContext(clientID: String, playbookID: String?, recapLimit: Int = 3) async throws -> AyaMeetingContext {
        guard let cRef = config.list(.clients) else { throw AyaError.notConfigured }
        let schemas = AyaCache.schemas()
        let client = try await self.client.record(workspace: cRef.workspaceId, list: cRef.techName, id: clientID)

        var contacts: [AyaRecord] = []
        if let ref = config.list(.contacts), let link = config.contactsClientField,
           let field = schemas[AyaSlot.contacts.rawValue]?.field(link) ?? Optional(AyaField(key: link, kind: .relation, writable: true)) {
            contacts = (try? await self.client.records(workspace: ref.workspaceId, list: ref.techName,
                                                       query: AyaQuery(condition: .init(field: field.brickTechName, valueKey: "id", value: clientID),
                                                                       limit: 50))) ?? []
        }

        var playbook: AyaRecord?
        if let pid = playbookID, let ref = config.list(.playbooks) {
            playbook = try await self.client.record(workspace: ref.workspaceId, list: ref.techName, id: pid)
        }

        return AyaMeetingContext(client: client, contacts: contacts, playbook: playbook,
                                 recaps: (try? await recentRecaps(client: client, limit: recapLimit)) ?? [],
                                 clientSchema: schemas[AyaSlot.clients.rawValue],
                                 contactSchema: schemas[AyaSlot.contacts.rawValue],
                                 recapSchema: schemas[AyaSlot.recaps.rawValue])
    }

    /// Earlier recaps for a client: by the recap → client link if configured, else by the
    /// client's name appearing in the title or attendees.
    public func recentRecaps(client: AyaRecord, limit: Int) async throws -> [AyaRecord] {
        guard let ref = config.list(.recaps) else { return [] }
        let schema = AyaCache.schema(.recaps)
        let dateSort = schema?.field(config.recapDateField)?.brickTechName
        if let link = config.recapClientField {
            let brick = schema?.field(link)?.brickTechName ?? AyaField(key: link, kind: .relation, writable: true).brickTechName
            return try await self.client.records(workspace: ref.workspaceId, list: ref.techName,
                                                 query: AyaQuery(condition: .init(field: brick, valueKey: "id", value: client.id),
                                                                 sortField: dateSort, descending: true, limit: limit))
        }
        let name = AyaService.searchName(client.title)
        guard name.count >= 3 else { return [] }
        let found = try await self.client.search(workspace: ref.workspaceId, list: ref.techName, text: name, limit: 20)
        let dateKey = config.recapDateField
        return Array(found.sorted {
            ($0[dateKey]?.string ?? $0.values["createdAt"]?.string ?? "") > ($1[dateKey]?.string ?? $1.values["createdAt"]?.string ?? "")
        }.prefix(limit))
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
