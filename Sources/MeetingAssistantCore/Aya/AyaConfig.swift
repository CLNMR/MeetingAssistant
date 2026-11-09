import Foundation
import Security

/// The four Aya lists MeetingAssistant works with.
public enum AyaSlot: String, Codable, CaseIterable, Identifiable, Sendable {
    case clients, contacts, playbooks, recaps
    public var id: String { rawValue }
    public var title: String {
        switch self {
        case .clients: return "Clients"
        case .contacts: return "Contacts"
        case .playbooks: return "Playbooks"
        case .recaps: return "Meeting recaps"
        }
    }
    public var explanation: String {
        switch self {
        case .clients: return "Companies you meet with (your CRM)."
        case .contacts: return "People at those companies; used to recognise attendees."
        case .playbooks: return "How a kind of meeting should go; becomes the checklist."
        case .recaps: return "Where a recap is created after each meeting."
        }
    }
    /// Default list (techName), used when connecting for the first time.
    public var defaultTechName: String {
        switch self {
        case .clients: return "firma"
        case .contacts: return "kontakt"
        case .playbooks: return "wikiPlaybooks"
        case .recaps: return "pmMeetings"
        }
    }
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
public struct AyaConfig: Codable, Equatable, Sendable {
    public var orgId = ""
    public var lists: [String: AyaListRef] = [:]          // AyaSlot.rawValue → list

    // Link fields (record keys of relation fields).
    public var contactsClientField: String? = "firma__id"
    public var recapClientField: String? = nil
    public var recapContactsField: String? = nil

    // Field roles. Only used if the field exists in the list.
    public var recapTitleField = "title__value"
    public var recapDateField = "meeting_date__value"
    public var recapAttendeesField = "external_participants__value"
    public var recapExternalIdField = "external_meeting_id__value"
    public var playbookGoalField = "goal__value"
    public var playbookStepsField = "steps__value"

    public init() {}

    public func list(_ slot: AyaSlot) -> AyaListRef? { lists[slot.rawValue] }
    public mutating func setList(_ slot: AyaSlot, _ ref: AyaListRef?) { lists[slot.rawValue] = ref }
    public var isComplete: Bool { !orgId.isEmpty && AyaSlot.allCases.allSatisfy { list($0) != nil } }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        let d = AyaConfig()
        orgId = (try? c.decode(String.self, forKey: .orgId)) ?? d.orgId
        lists = (try? c.decode([String: AyaListRef].self, forKey: .lists)) ?? d.lists
        contactsClientField = c.contains(.contactsClientField) ? (try? c.decodeIfPresent(String.self, forKey: .contactsClientField)) : d.contactsClientField
        recapClientField = try? c.decodeIfPresent(String.self, forKey: .recapClientField)
        recapContactsField = try? c.decodeIfPresent(String.self, forKey: .recapContactsField)
        recapTitleField = (try? c.decode(String.self, forKey: .recapTitleField)) ?? d.recapTitleField
        recapDateField = (try? c.decode(String.self, forKey: .recapDateField)) ?? d.recapDateField
        recapAttendeesField = (try? c.decode(String.self, forKey: .recapAttendeesField)) ?? d.recapAttendeesField
        recapExternalIdField = (try? c.decode(String.self, forKey: .recapExternalIdField)) ?? d.recapExternalIdField
        playbookGoalField = (try? c.decode(String.self, forKey: .playbookGoalField)) ?? d.playbookGoalField
        playbookStepsField = (try? c.decode(String.self, forKey: .playbookStepsField)) ?? d.playbookStepsField
    }

    public func encode(to encoder: Encoder) throws {
        var c = encoder.container(keyedBy: CodingKeys.self)
        try c.encode(orgId, forKey: .orgId)
        try c.encode(lists, forKey: .lists)
        try c.encode(contactsClientField, forKey: .contactsClientField)   // explicit null = no link
        try c.encode(recapClientField, forKey: .recapClientField)
        try c.encode(recapContactsField, forKey: .recapContactsField)
        try c.encode(recapTitleField, forKey: .recapTitleField)
        try c.encode(recapDateField, forKey: .recapDateField)
        try c.encode(recapAttendeesField, forKey: .recapAttendeesField)
        try c.encode(recapExternalIdField, forKey: .recapExternalIdField)
        try c.encode(playbookGoalField, forKey: .playbookGoalField)
        try c.encode(playbookStepsField, forKey: .playbookStepsField)
    }

    enum CodingKeys: String, CodingKey {
        case orgId, lists, contactsClientField, recapClientField, recapContactsField
        case recapTitleField, recapDateField, recapAttendeesField, recapExternalIdField
        case playbookGoalField, playbookStepsField
    }

    // MARK: Persistence (shared with msctl through the app's defaults domain)

    static let defaultsKey = "aya.config.v1"

    public static func load() -> AyaConfig {
        guard let s = AppDefaults.shared.string(forKey: defaultsKey),
              let c = try? JSONDecoder().decode(AyaConfig.self, from: Data(s.utf8)) else { return AyaConfig() }
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
