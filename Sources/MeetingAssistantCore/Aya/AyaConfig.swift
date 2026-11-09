import Foundation
import Security

/// The Aya lists MeetingAssistant works with.
public enum AyaSlot: String, Codable, CaseIterable, Identifiable, Sendable {
    case clients, contacts, projects, templates, meetings, members
    public var id: String { rawValue }
    public var title: String {
        switch self {
        case .clients: return "Clients"
        case .contacts: return "Contacts"
        case .projects: return "Projects"
        case .templates: return "Meeting templates"
        case .meetings: return "Meetings"
        case .members: return "Team members"
        }
    }
    public var explanation: String {
        switch self {
        case .clients: return "Companies you meet with (your CRM)."
        case .contacts: return "People at those companies; used to recognise attendees."
        case .projects: return "Projects a meeting can belong to."
        case .templates: return "Agenda and questions; they become the meeting's checklist."
        case .meetings: return "Planned meetings, and where the recap is saved."
        case .members: return "Your team; internal attendees are linked as participants."
        }
    }
    /// Default list (techName), used when connecting for the first time.
    public var defaultTechName: String {
        switch self {
        case .clients: return "firma"
        case .contacts: return "kontakt"
        case .projects: return "pmProjects"
        case .templates: return "pmMeetingTemplates"
        case .meetings: return "pmMeetings"
        case .members: return "members"
        }
    }
    /// Lists Assist cannot work without. Projects and members are used when they exist.
    public var isRequired: Bool { self == .clients || self == .contacts || self == .templates || self == .meetings }
}

public struct AyaListRef: Codable, Hashable, Sendable {
    public var workspaceId: String
    public var workspaceLabel: String
    public var techName: String
    public var label: String
    public init(workspaceId: String, workspaceLabel: String, techName: String, label: String) {
        self.workspaceId = workspaceId; self.workspaceLabel = workspaceLabel; self.techName = techName; self.label = label
    }
}

/// Settings for the Aya connection. The API key is kept in the Keychain, not here.
/// Field names are record keys (`<techName>__<valueKey>`); a field that does not exist in the
/// list is simply not used.
public struct AyaConfig: Codable, Equatable, Sendable {
    public var orgId = ""
    public var lists: [String: AyaListRef] = [:]          // AyaSlot.rawValue → list

    /// Contact → client link.
    public var contactsClientField: String? = "firma__id"

    // Meeting links (nil = not linked).
    public var meetingClientField: String? = "customer__id"
    public var meetingProjectField: String? = "project__id"
    public var meetingTemplateField: String? = "load_template__id"
    public var meetingParticipantsField: String? = "participants__ids"

    // Meeting fields the app fills in itself.
    public var meetingTitleField = "title__value"
    public var meetingDateField = "meeting_date__value"
    public var meetingTypeField = "meeting_type__value"
    public var meetingExternalIdField = "external_meeting_id__value"

    // Template fields.
    public var templateAgendaField = "agenda__value"
    public var templateGoalField = "description__value"
    public var templateTypeField = "meeting_type__value"

    public init() {}

    public func list(_ slot: AyaSlot) -> AyaListRef? { lists[slot.rawValue] }
    public mutating func setList(_ slot: AyaSlot, _ ref: AyaListRef?) { lists[slot.rawValue] = ref }
    public var isComplete: Bool { !orgId.isEmpty && AyaSlot.allCases.filter(\.isRequired).allSatisfy { list($0) != nil } }

    enum CodingKeys: String, CodingKey, CaseIterable {
        case orgId, lists, contactsClientField
        case meetingClientField, meetingProjectField, meetingTemplateField, meetingParticipantsField
        case meetingTitleField, meetingDateField, meetingTypeField, meetingExternalIdField
        case templateAgendaField, templateGoalField, templateTypeField
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        let d = AyaConfig()
        func str(_ k: CodingKeys, _ def: String) -> String { (try? c.decode(String.self, forKey: k)) ?? def }
        // A missing key means "default"; an explicit null means "not linked".
        func link(_ k: CodingKeys, _ def: String?) -> String? { c.contains(k) ? ((try? c.decodeIfPresent(String.self, forKey: k)) ?? nil) : def }
        orgId = str(.orgId, d.orgId)
        lists = (try? c.decode([String: AyaListRef].self, forKey: .lists)) ?? d.lists
        contactsClientField = link(.contactsClientField, d.contactsClientField)
        meetingClientField = link(.meetingClientField, d.meetingClientField)
        meetingProjectField = link(.meetingProjectField, d.meetingProjectField)
        meetingTemplateField = link(.meetingTemplateField, d.meetingTemplateField)
        meetingParticipantsField = link(.meetingParticipantsField, d.meetingParticipantsField)
        meetingTitleField = str(.meetingTitleField, d.meetingTitleField)
        meetingDateField = str(.meetingDateField, d.meetingDateField)
        meetingTypeField = str(.meetingTypeField, d.meetingTypeField)
        meetingExternalIdField = str(.meetingExternalIdField, d.meetingExternalIdField)
        templateAgendaField = str(.templateAgendaField, d.templateAgendaField)
        templateGoalField = str(.templateGoalField, d.templateGoalField)
        templateTypeField = str(.templateTypeField, d.templateTypeField)
    }

    public func encode(to encoder: Encoder) throws {
        var c = encoder.container(keyedBy: CodingKeys.self)
        try c.encode(orgId, forKey: .orgId)
        try c.encode(lists, forKey: .lists)
        try c.encode(contactsClientField, forKey: .contactsClientField)   // explicit null = no link
        try c.encode(meetingClientField, forKey: .meetingClientField)
        try c.encode(meetingProjectField, forKey: .meetingProjectField)
        try c.encode(meetingTemplateField, forKey: .meetingTemplateField)
        try c.encode(meetingParticipantsField, forKey: .meetingParticipantsField)
        try c.encode(meetingTitleField, forKey: .meetingTitleField)
        try c.encode(meetingDateField, forKey: .meetingDateField)
        try c.encode(meetingTypeField, forKey: .meetingTypeField)
        try c.encode(meetingExternalIdField, forKey: .meetingExternalIdField)
        try c.encode(templateAgendaField, forKey: .templateAgendaField)
        try c.encode(templateGoalField, forKey: .templateGoalField)
        try c.encode(templateTypeField, forKey: .templateTypeField)
    }

    // MARK: Persistence (shared with msctl through the app's defaults domain)

    static let defaultsKey = "aya.config.v2"
    /// Before meeting templates: playbooks and a separate recap list.
    static let legacyKey = "aya.config.v1"

    public static func load() -> AyaConfig {
        let d = AppDefaults.shared
        if let s = d.string(forKey: defaultsKey), let c = try? JSONDecoder().decode(AyaConfig.self, from: Data(s.utf8)) { return c }
        if let s = d.string(forKey: legacyKey) { return migrate(legacy: Data(s.utf8)) }
        return AyaConfig()
    }

    /// Keeps the connection, the client and contact lists and the contact link; the meeting
    /// lists are picked again when the app next talks to Aya.
    static func migrate(legacy: Data) -> AyaConfig {
        struct Legacy: Decodable { var orgId: String?; var lists: [String: AyaListRef]?; var contactsClientField: String? }
        var c = AyaConfig()
        guard let old = try? JSONDecoder().decode(Legacy.self, from: legacy) else { return c }
        c.orgId = old.orgId ?? ""
        for slot in [AyaSlot.clients, .contacts] { c.lists[slot.rawValue] = old.lists?[slot.rawValue] }
        if let link = old.contactsClientField { c.contactsClientField = link }
        return c
    }

    public func save() {
        if let d = try? JSONEncoder().encode(self), let s = String(data: d, encoding: .utf8) {
            AppDefaults.shared.set(s, forKey: Self.defaultsKey)
        }
    }

    // MARK: API key (Keychain)

    public static var apiKey: String? {
        if let env = ProcessInfo.processInfo.environment["AYA_API_KEY"], !env.isEmpty { return env }
        return Keychain.get(account: "aya-api-key")
    }
    public static func setAPIKey(_ key: String?) {
        if let key, !key.isEmpty { Keychain.set(key, account: "aya-api-key") } else { Keychain.delete(account: "aya-api-key") }
    }

    /// A client for the stored settings, or nil when not configured.
    public func client() -> AyaClient? {
        guard !orgId.isEmpty, let key = AyaConfig.apiKey, !key.isEmpty else { return nil }
        return AyaClient(orgId: orgId, apiKey: key)
    }
}

/// The app's UserDefaults domain, also from `msctl`.
public enum AppDefaults {
    public static let domain = "de.local.MeetingScribe"   // bundle id kept from before the rename
    public static var shared: UserDefaults {
        if Bundle.main.bundleIdentifier == domain { return .standard }
        return UserDefaults(suiteName: domain) ?? .standard
    }
}

/// Generic-password items in the login keychain.
public enum Keychain {
    static let service = "MeetingAssistant"

    public static func get(account: String) -> String? {
        let q: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
                                kSecAttrAccount as String: account, kSecReturnData as String: true,
                                kSecMatchLimit as String: kSecMatchLimitOne]
        var out: CFTypeRef?
        guard SecItemCopyMatching(q as CFDictionary, &out) == errSecSuccess, let d = out as? Data else { return nil }
        return String(data: d, encoding: .utf8)
    }

    @discardableResult
    public static func set(_ value: String, account: String) -> Bool {
        let base: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
                                   kSecAttrAccount as String: account]
        let data = Data(value.utf8)
        if SecItemUpdate(base as CFDictionary, [kSecValueData as String: data] as CFDictionary) == errSecSuccess { return true }
        var add = base
        add[kSecValueData as String] = data
        add[kSecAttrLabel as String] = "MeetingAssistant (\(account))"
        return SecItemAdd(add as CFDictionary, nil) == errSecSuccess
    }

    public static func delete(account: String) {
        let q: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
                                kSecAttrAccount as String: account]
        SecItemDelete(q as CFDictionary)
    }
}
