import Foundation

// Client for the Aya REST API (https://api.hey-aya.ai).
// Auth: `Authorization: Bearer <org API key>`. An org key belongs to one organization, whose id
// must be configured next to it: it can't be derived from the key.

public struct AyaWorkspace: Codable, Identifiable, Hashable, Sendable {
    public var id: String
    public var slug: String?
    public var localizedLabel: [String: String]?
    public var isPrivate: Bool?
    public var label: String { AyaText.localized(localizedLabel) ?? slug ?? id }
}

public struct AyaRecordSpec: Codable, Identifiable, Hashable, Sendable {
    public var id: String
    public var techName: String
    public var workspaceId: String?
    public var localizedLabel: [String: String]?
    public var isSystem: Bool?
    public var label: String { AyaText.localized(localizedLabel) ?? techName }
}

/// One record: a flat object of `id`, `computedTitle`, `<field>__value`, `<rel>__id(s)`, … and system columns.
public struct AyaRecord: Codable, Identifiable, Hashable, Sendable {
    public var values: [String: JSONValue]
    public init(values: [String: JSONValue]) { self.values = values }
    public init(from decoder: Decoder) throws { values = try [String: JSONValue](from: decoder) }
    public func encode(to encoder: Encoder) throws { try values.encode(to: encoder) }

    public var id: String { values["id"]?.string ?? "" }
    public var title: String {
        let t = values["computedTitle"]?.string?.trimmingCharacters(in: .whitespaces) ?? ""
        return t.isEmpty ? id : t
    }
    public subscript(key: String) -> JSONValue? { values[key] }
    public var updatedAt: Date? { values["updatedAt"]?.string.flatMap(AyaText.parseDate) }
    public var createdAt: Date? { values["createdAt"]?.string.flatMap(AyaText.parseDate) }
}

public enum AyaText {
    /// The label in the system language, then English, then any.
    public static func localized(_ map: [String: String]?) -> String? {
        guard let map, !map.isEmpty else { return nil }
        let lang = Locale.current.language.languageCode?.identifier ?? "en"
        return map[lang] ?? map["en"] ?? map["de"] ?? map.values.sorted().first
    }

    public static func parseDate(_ s: String) -> Date? {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        if let d = f.date(from: s) { return d }
        f.formatOptions = [.withInternetDateTime]
        return f.date(from: s)
    }

    public static func iso(_ d: Date) -> String {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime]
        return f.string(from: d)
    }
}

public enum AyaError: LocalizedError, Equatable {
    case notConfigured
    case unauthorized
    case forbidden(String)
    case notFound(String)
    case creditsExhausted
    case badRequest(String)
    case server(Int, String)
    case network(String)
    case decoding(String)

    public var errorDescription: String? {
        switch self {
        case .notConfigured: return "Aya is not connected yet. Enter the organization id and API key in Settings › Assist."
        case .unauthorized: return "Aya rejected the API key. Check it in Settings › Assist."
        case .forbidden(let m): return "No permission in Aya for this list or field. \(m)"
        case .notFound(let m): return "Not found in Aya (workspace, list or record). \(m)"
        case .creditsExhausted: return "The Aya organization has used up its API credits."
        case .badRequest(let m): return "Aya rejected the request: \(m)"
        case .server(let code, let m): return "Aya server error \(code). \(m)"
        case .network(let m): return "Could not reach Aya: \(m)"
        case .decoding(let m): return "Unexpected answer from Aya: \(m)"
        }
    }
}

/// A list query: `{"filter":…, "sorting":[…], "pagination":{limit, offset}}`.
/// `brickTechName` is the camelCase field name (`firma`, `meetingDate`), not the record key.
public struct AyaQuery: Sendable {
    public struct Condition: Sendable {
        public var field: String, valueKey: String, comparator: String, value: String
        public init(field: String, valueKey: String = "value", comparator: String = "equal", value: String) {
            self.field = field; self.valueKey = valueKey; self.comparator = comparator; self.value = value
        }
    }
    public var condition: Condition?
    public var sortField: String?          // camelCase field or "system"
    public var sortValueKey = "value"
    public var descending = false
    public var limit = 50
    public var offset = 0

    public init(condition: Condition? = nil, sortField: String? = nil, sortValueKey: String = "value",
                descending: Bool = false, limit: Int = 50, offset: Int = 0) {
        self.condition = condition; self.sortField = sortField; self.sortValueKey = sortValueKey
        self.descending = descending; self.limit = limit; self.offset = offset
    }

    public var json: JSONValue {
        var filter: JSONValue = .null
        if let c = condition {
            filter = .object(["nodeType": .string("condition"), "brickTechName": .string(c.field),
                              "brickValueKey": .string(c.valueKey), "comparator": .string(c.comparator),
                              "valueType": .string("constant"), "value": .string(c.value)])
        }
        var sorting: [JSONValue] = []
        if let s = sortField {
            sorting = [.object(["brickTechName": .string(s), "brickValueKey": .string(sortValueKey),
                                "descending": .bool(descending)])]
        }
        return .object(["filter": filter, "sorting": .array(sorting),
                        "pagination": .object(["limit": .number(Double(limit)), "offset": .number(Double(offset))])])
    }
}

public actor AyaClient {
    public let orgId: String
    private let apiKey: String
    private let base: URL
    private let session: URLSession

    public init(orgId: String, apiKey: String, base: URL = URL(string: "https://api.hey-aya.ai")!) {
        self.orgId = orgId.trimmingCharacters(in: .whitespacesAndNewlines)
        self.apiKey = apiKey.trimmingCharacters(in: .whitespacesAndNewlines)
        self.base = base
        let cfg = URLSessionConfiguration.ephemeral
        cfg.timeoutIntervalForRequest = 30
        session = URLSession(configuration: cfg)
    }

    // MARK: Reads

    /// Cheap key check. Returns the key owner's user id.
    public func me() async throws -> String {
        let v: JSONValue = try await get("/api/auth/me")
        return v["userId"]?.string ?? ""
    }

    public func workspaces() async throws -> [AyaWorkspace] {
        try await get("/api/workspaces", query: [URLQueryItem(name: "orgId", value: orgId)])
    }

    public func recordSpecs(workspace: String) async throws -> [AyaRecordSpec] {
        try await get("/api/workspaces/\(workspace)/record-specs")
    }

    public func records(workspace: String, list: String, query: AyaQuery = AyaQuery()) async throws -> [AyaRecord] {
        try await get("/api/workspaces/\(workspace)/records/\(list)",
                      query: [URLQueryItem(name: "query", value: query.json.compactJSON)])
    }

    /// All records, paging through `limit` at a time (stops at `max`).
    public func allRecords(workspace: String, list: String, sortField: String? = nil, max: Int = 2000) async throws -> [AyaRecord] {
        var out: [AyaRecord] = []
        var q = AyaQuery(sortField: sortField, limit: 200)
        while out.count < max {
            let page = try await records(workspace: workspace, list: list, query: q)
            out += page
            if page.count < q.limit { break }
            q.offset += q.limit
        }
        return out
    }

    public func search(workspace: String, list: String, text: String, limit: Int = 20) async throws -> [AyaRecord] {
        try await get("/api/workspaces/\(workspace)/records/\(list)/search",
                      query: [URLQueryItem(name: "q", value: text), URLQueryItem(name: "limit", value: String(limit))])
    }

    public func record(workspace: String, list: String, id: String) async throws -> AyaRecord {
        try await get("/api/workspaces/\(workspace)/records/\(list)/\(id)")
    }

    /// The workspace's OpenAPI document (needs API documentation switched on in the organization).
    public func openAPI(workspace: String) async throws -> Data {
        try await send("GET", "/api/workspaces/\(workspace)/openapi.json", auth: false)
    }

    // MARK: Writes

    /// Creates a record. `values` uses record keys (`title__value`, `firma__id`, …).
    public func create(workspace: String, list: String, values: [String: JSONValue]) async throws -> AyaRecord {
        let body = try JSONEncoder().encode(JSONValue.object(["values": .object(values)]))
        return try decode(await send("POST", "/api/workspaces/\(workspace)/records/\(list)", body: body))
    }

    /// Partial update: only the given keys change.
    public func update(workspace: String, list: String, id: String, values: [String: JSONValue]) async throws -> AyaRecord {
        let body = try JSONEncoder().encode(JSONValue.object(["values": .object(values)]))
        return try decode(await send("PUT", "/api/workspaces/\(workspace)/records/\(list)/\(id)", body: body))
    }

    public func delete(workspace: String, list: String, id: String) async throws {
        _ = try await send("DELETE", "/api/workspaces/\(workspace)/records/\(list)/\(id)")
    }

    // MARK: Transport

    private func get<T: Decodable>(_ path: String, query: [URLQueryItem] = []) async throws -> T {
        try decode(await send("GET", path, query: query))
    }

    private func decode<T: Decodable>(_ data: Data) throws -> T {
        do { return try JSONDecoder().decode(T.self, from: data) }
        catch { throw AyaError.decoding(String(describing: error).prefix(300).description) }
    }

    private func send(_ method: String, _ path: String, query: [URLQueryItem] = [], body: Data? = nil,
                      auth: Bool = true) async throws -> Data {
        guard !orgId.isEmpty, !apiKey.isEmpty else { throw AyaError.notConfigured }
        var comps = URLComponents(url: base.appendingPathComponent(path), resolvingAgainstBaseURL: false)!
        if !query.isEmpty { comps.queryItems = query }
        // URLComponents leaves "+" alone, which servers read as a space.
        comps.percentEncodedQuery = comps.percentEncodedQuery?.replacingOccurrences(of: "+", with: "%2B")
        var req = URLRequest(url: comps.url!)
        req.httpMethod = method
        if auth { req.setValue("Bearer \(apiKey)", forHTTPHeaderField: "Authorization") }
        req.setValue("application/json", forHTTPHeaderField: "Accept")
        if let body { req.httpBody = body; req.setValue("application/json", forHTTPHeaderField: "Content-Type") }

        var attempt = 0
        while true {
            attempt += 1
            let data: Data, resp: URLResponse
            do { (data, resp) = try await session.data(for: req) }
            catch {
                if attempt < 3 { try await Task.sleep(nanoseconds: UInt64(attempt) * 1_000_000_000); continue }
                throw AyaError.network(error.localizedDescription)
            }
            let http = resp as! HTTPURLResponse
            if (200..<300).contains(http.statusCode) { return data }
            let err = (try? JSONValue.parse(data)) ?? .null
            let code = err["code"]?.string ?? ""
            let message = err["error"]?.string ?? String(data: data.prefix(300), encoding: .utf8) ?? ""
            switch http.statusCode {
            case 402: throw AyaError.creditsExhausted
            case 429:
                if code == "limit_exceeded" { throw AyaError.creditsExhausted }
                if attempt < 4 {
                    let wait = Double(http.value(forHTTPHeaderField: "Retry-After") ?? "") ?? Double(attempt * 2)
                    try await Task.sleep(nanoseconds: UInt64(min(wait, 30) * 1e9)); continue
                }
                throw AyaError.server(429, message)
            case 503, 502, 504:
                if attempt < 4 { try await Task.sleep(nanoseconds: UInt64(attempt) * 1_000_000_000); continue }
                throw AyaError.server(http.statusCode, message)
            case 401: throw AyaError.unauthorized
            case 403: throw AyaError.forbidden(message)
            case 404: throw AyaError.notFound(message)
            case 400, 422: throw AyaError.badRequest(message.isEmpty ? (String(data: data.prefix(300), encoding: .utf8) ?? "") : message)
            default: throw AyaError.server(http.statusCode, message)
            }
        }
    }
}
