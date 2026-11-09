import AVFoundation
import MeetingAssistantCore
import SwiftUI

/// Plays the session's tracks together (mic + call audio), kept in sync.
@MainActor
final class SessionPlayer: ObservableObject {
    @Published var isPlaying = false
    @Published var currentTime: Double = 0
    private var players: [AVAudioPlayer] = []
    private var timer: Timer?
    var duration: Double { players.map(\.duration).max() ?? 0 }

    init(sessionID: String, tracks: [Track]) {
        players = tracks.compactMap { try? AVAudioPlayer(contentsOf: AppPaths.track(sessionID, $0)) }
        players.forEach { $0.prepareToPlay() }
    }

    func toggle() { isPlaying ? pause() : play(from: currentTime) }

    func play(from t: Double) {
        guard let first = players.first else { return }
        let start = first.deviceCurrentTime + 0.05
        for p in players {
            p.currentTime = min(max(0, t), p.duration)
            p.play(atTime: start)
        }
        isPlaying = true
        timer?.invalidate()
        timer = Timer.scheduledTimer(withTimeInterval: 0.2, repeats: true) { [weak self] _ in
            Task { @MainActor in
                guard let self, let p = self.players.first else { return }
                self.currentTime = p.currentTime
                if !self.players.contains(where: \.isPlaying) { self.isPlaying = false; self.timer?.invalidate() }
            }
        }
    }

    func pause() {
        players.forEach { $0.pause() }
        isPlaying = false
        timer?.invalidate()
    }

    func seek(_ t: Double) {
        currentTime = t
        if isPlaying { play(from: t) }
    }
}
