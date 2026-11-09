import AVFoundation
import Foundation

public enum Permissions {
    public enum State: String, Sendable { case granted, denied, notDetermined }

    public static var microphone: State {
        switch AVCaptureDevice.authorizationStatus(for: .audio) {
        case .authorized: return .granted
        case .notDetermined: return .notDetermined
        default: return .denied
        }
    }

    /// Ask for microphone access. Must be called before touching AVAudioEngine.inputNode,
    /// otherwise the engine blocks until the user answers the system prompt.
    public static func requestMicrophone() async -> Bool {
        if microphone == .granted { return true }
        return await AVCaptureDevice.requestAccess(for: .audio)
    }
}
