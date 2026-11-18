import Foundation
import ServiceManagement

/// Opens the app when the user logs in. The system login-item list is the source of truth,
/// so turning it off in System Settings › General › Login Items is reflected here.
@MainActor
final class LaunchAtLogin: ObservableObject {
    /// Set once the first-open question was answered (either way).
    static let askedKey = "launchAtLogin.asked"

    @Published private(set) var isEnabled = false
    @Published private(set) var needsApproval = false
    @Published var error: String?

    init() { refresh() }

    /// Only an app bundle can register itself (not `swift run`).
    static var isAvailable: Bool { Bundle.main.bundleURL.pathExtension == "app" }

    /// Ask on the first open of an installed app.
    static var shouldAsk: Bool {
        isAvailable && !UserDefaults.standard.bool(forKey: askedKey)
            && UserDefaults.standard.string(forKey: "markdownSnapshot") == nil
            && UserDefaults.standard.string(forKey: "assistSnapshot") == nil
            && UserDefaults.standard.string(forKey: "dictationTest") == nil
    }

    func refresh() {
        let status = SMAppService.mainApp.status
        isEnabled = status == .enabled
        needsApproval = status == .requiresApproval
    }

    func set(_ on: Bool) {
        error = nil
        do {
            if on { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
        } catch {
            self.error = "Could not \(on ? "turn on" : "turn off") opening at startup: \(error.localizedDescription)"
        }
        refresh()
    }

    /// Answer to the first-open question.
    func answer(_ on: Bool) {
        UserDefaults.standard.set(true, forKey: Self.askedKey)
        if on { set(true) }
    }

    func openLoginItems() {
        SMAppService.openSystemSettingsLoginItems()
    }
}
