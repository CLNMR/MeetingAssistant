import Foundation

/// One field of an Aya record type, as far as the OpenAPI document describes it.
/// Aya's OpenAPI has no field labels and no select options: labels are derived from the key,
/// options are learned from existing records.
public struct AyaField: Codable, Hashable, Identifiable, Sendable {
    public enum Kind: String, Codable, Sendable {
        case text, markdown, select, number, boolean, datetime, date, email, phone, url
        case relation, relations, embedded, files, other
    }
    public var key: String            // record key, e.g. "firmenname__value", "firma__id"
    public var kind: Kind
    public var writable: Bool
    public var options: [String]      // select only: values seen in existing records
    public var id: String { key }

    public init(key: String, kind: Kind, writable: Bool, options: [String] = []) {
        self.key = key; self.kind = kind; self.writable = writable; self.options = options
    }

    /// The field name without suffix: "firmenname__value" → "firmenname".
    public var name: String { key.components(separatedBy: "__").first ?? key }

    /// "next_steps__value" → "Next steps", "maZahl" → "Ma zahl".
    public var label: String { AyaField.humanize(name) }

    /// camelCase name used by filters and sorting ("kunde_praefix" → "kundePraefix").
    public var brickTechName: String {
        let parts = name.split(separator: "_")
        guard let first = parts.first else { return name }
        return String(first) + parts.dropFirst().map { $0.prefix(1).uppercased() + $0.dropFirst() }.joined()
    }

    /// Plain content a person or Claude can write.
    public var isContent: Bool { [.text, .markdown, .select, .number, .boolean, .datetime, .date, .email, .phone, .url].contains(kind) }

    public static func humanize(_ s: String) -> String {
        var out = ""
        for (i, ch) in s.enumerated() {
            if ch == "_" { out += " "; continue }
            if ch.isUppercase, i > 0 { out += " " }
            out.append(ch)
        }
        out = out.lowercased().trimmingCharacters(in: .whitespaces)
        return out.prefix(1).uppercased() + out.dropFirst()
    }
}

/// The schema of one list (record type) in one workspace.
public struct AyaListSchema: Codable, Hashable, Sendable {
    public var workspaceId: String
    public var techName: String
    public var fields: [AyaField]
    public var fetchedAt: Date

    public init(workspaceId: String, techName: String, fields: [AyaField], fetchedAt: Date = Date()) {
        self.workspaceId = workspaceId; self.techName = techName; self.fields = fields; self.fetchedAt = fetchedAt
    }

    public func field(_ key: String) -> AyaField? { fields.first { $0.key == key } }

    /// The value that links a record to `id` through the relation field `key`: a list of ids for a
    /// relations field (`…__ids`), a single id otherwise. Unknown fields are judged by their key.
    public func linkValue(_ key: String, id: String) -> JSONValue {
        let many = field(key).map { $0.kind == .relations } ?? key.hasSuffix("__ids")
        return many ? .array([.string(id)]) : .string(id)
    }
    public var writableContent: [AyaField] { fields.filter { $0.writable && $0.isContent } }
    public var relations: [AyaField] { fields.filter { $0.kind == .relation || $0.kind == .relations } }

    /// Parses the fields of `techName` from a workspace OpenAPI document.
    public static func parse(openAPI data: Data, workspaceId: String, techName: String) throws -> AyaListSchema {
        guard let doc = try? JSONValue.parse(data), let paths = doc["paths"]?.object,
              let schemas = doc["components"]?["schemas"]?.object else {
            throw AyaError.decoding("OpenAPI document could not be read")
        }
        // Find the create body via the list path: /api/workspaces/<ws>/records/<techName>.
        guard let path = paths.first(where: { $0.key.hasSuffix("/records/\(techName)") })?.value,
              let ref = path["post"]?["requestBody"]?["content"]?["application/json"]?["schema"]?["$ref"]?.string else {
            throw AyaError.notFound("List “\(techName)” is not in this workspace's API documentation.")
        }
        let createName = String(ref.split(separator: "/").last ?? "")
        let typeName = createName.replacingOccurrences(of: "CreateBody", with: "")
        let writable = schemas[typeName + "UpdateBody"]?["properties"]?["values"]?["properties"]?.object
            ?? schemas[createName]?["properties"]?["values"]?["properties"]?.object ?? [:]
        let readable = schemas[typeName + "Record"]?["properties"]?.object ?? [:]

        var fields: [AyaField] = []
        let system: Set<String> = ["id", "computedTitle", "createdAt", "updatedAt", "createdBy", "modifiedBy",
                                   "createdByTitle", "modifiedByTitle", "locked"]
        for (key, prop) in readable where !system.contains(key) {
            if prop["readOnly"]?.bool == true && writable[key] == nil {
                // Derived columns (computed titles, colors) are not separate fields.
                if key.contains("__computed_title") || key.hasSuffix("_color") { continue }
            }
            fields.append(AyaField(key: key, kind: kind(key: key, prop: writable[key] ?? prop), writable: writable[key] != nil))
        }
        for (key, prop) in writable where readable[key] == nil {
            fields.append(AyaField(key: key, kind: kind(key: key, prop: prop), writable: true))
        }
        fields.removeAll { $0.key.contains("__computed_title") || $0.key.hasSuffix("__record_spec_tech_name") }
        return AyaListSchema(workspaceId: workspaceId, techName: techName, fields: fields.sorted { $0.key < $1.key })
    }

    static func kind(key: String, prop: JSONValue) -> AyaField.Kind {
        let desc = (prop["description"]?.string ?? "").lowercased()
        if key.hasSuffix("__ids") { return .relations }
        if key.hasSuffix("__id") { return desc.contains("embedded") ? .embedded : .relation }
        if key.hasSuffix("__file_count") || key.hasSuffix("__files") { return .files }
        if desc.contains("markdown") { return .markdown }
        if desc.contains("option") { return .select }
        if desc.contains("email") { return .email }
        if desc.contains("phone") { return .phone }
        if desc.contains("url") || desc.contains("link") { return .url }
        if desc.contains("datetime") { return .datetime }
        if desc.contains("date") { return .date }
        if desc.contains("boolean") { return .boolean }
        if desc.contains("numeric") || desc.contains("number") { return .number }
        if desc.contains("text") { return .text }
        let types = (prop["anyOf"]?.array ?? [prop]).compactMap { $0["type"]?.string }
        if types.contains("boolean") { return .boolean }
        if types.contains("number") || types.contains("integer") { return .number }
        if types.contains("string") { return .text }
        return .other
    }

    /// Fills `options` of select fields with the values seen in `records`.
    public mutating func learnOptions(from records: [AyaRecord]) {
        for i in fields.indices where fields[i].kind == .select {
            var seen = Set(fields[i].options)
            for r in records { if let v = r[fields[i].key]?.string, !v.isEmpty { seen.insert(v) } }
            fields[i].options = seen.sorted()
        }
    }

    /// Converts a value (usually text from Claude or a form) to the JSON the field expects.
    /// Returns nil when the value doesn't fit (e.g. an unknown select option).
    public func coerce(_ value: JSONValue, for key: String) -> JSONValue? {
        guard let f = field(key) else { return nil }
        if value.isNull { return f.kind == .boolean ? nil : .null }
        let text = value.description.trimmingCharacters(in: .whitespacesAndNewlines)
        switch f.kind {
        case .boolean:
            if let b = value.bool { return .bool(b) }
            switch text.lowercased() {
            case "true", "yes", "ja", "1": return .bool(true)
            case "false", "no", "nein", "0": return .bool(false)
            default: return nil
            }
        case .number:
            if let n = value.number { return .number(n) }
            return Double(text.replacingOccurrences(of: ",", with: ".")).map { .number($0) }
        case .select:
            if text.isEmpty { return .null }
            return f.options.contains(text) ? .string(text) : nil
        case .datetime, .date:
            if text.isEmpty { return .null }
            return AyaText.parseDate(text) != nil || text.count == 10 ? .string(text) : nil
        case .relation:
            return text.isEmpty ? .null : .string(text)
        case .relations:
            if let a = value.array { return .array(a) }
            return nil
        case .text, .markdown, .email, .phone, .url:
            return .string(text)
        case .embedded, .files, .other:
            return nil
        }
    }
}
