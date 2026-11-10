import AVFoundation
import Foundation

/// Dictations whose text did not arrive. The audio is saved before it is transcribed and removed
/// once the text is delivered, so an error or a crash does not lose what was said. A failed one
/// stays until it is transcribed again, deleted, or for 24 hours at most. The folder is kept
/// out of backups: it is plain audio of the user's voice.
public enum DictationRecovery {
    public static let keptFor: TimeInterval = 24 * 60 * 60

    public struct Item: Identifiable, Hashable, Sendable {
        public var url: URL
        public var recordedAt: Date
        public var seconds: Double
        /// Recorded as a Scribe+ dictation (a retry runs Scribe+ again).
        public var plus: Bool = false
        public var id: String { url.lastPathComponent }
    }

    public static var folder: URL {
        AppPaths.root.appendingPathComponent("Dictations", isDirectory: true)
    }

    /// Saves 16 kHz mono samples as `dictation-<ms>.caf` (`dictation-<ms>-plus.caf` for a Scribe+
    /// dictation); returns the file, or nil when that failed.
    public static func save(_ samples: [Float], plus: Bool = false, at date: Date = Date(), in dir: URL = folder) -> URL? {
        let fm = FileManager.default
        do {
            try fm.createDirectory(at: dir, withIntermediateDirectories: true)
            var d = dir
            var values = URLResourceValues()
            values.isExcludedFromBackup = true
            try? d.setResourceValues(values)
            let url = dir.appendingPathComponent("dictation-\(Int(date.timeIntervalSince1970 * 1000))\(plus ? "-plus" : "").caf")
            try AudioIO.writeCAF(samples, to: url)
            return url
        } catch {
            return nil
        }
    }

    public static func load(_ item: Item) throws -> [Float] { try AudioIO.load16k(item.url) }

    public static func delete(_ url: URL) { try? FileManager.default.removeItem(at: url) }

    /// Newest first; removes those older than `keptFor`.
    public static func current(now: Date = Date(), in dir: URL = folder) -> [Item] {
        let fm = FileManager.default
        guard let files = try? fm.contentsOfDirectory(at: dir, includingPropertiesForKeys: nil) else { return [] }
        var out: [Item] = []
        for f in files where f.pathExtension == "caf" {
            let parts = f.deletingPathExtension().lastPathComponent.replacingOccurrences(of: "dictation-", with: "")
                .split(separator: "-")
            let ms = parts.first.flatMap { Double($0) } ?? 0
            let date = Date(timeIntervalSince1970: ms / 1000)
            if now.timeIntervalSince(date) > keptFor { delete(f); continue }
            let secs = (try? AVAudioFile(forReading: f)).map { Double($0.length) / $0.processingFormat.sampleRate } ?? 0
            out.append(Item(url: f, recordedAt: date, seconds: secs, plus: parts.dropFirst().contains("plus")))
        }
        return out.sorted { $0.recordedAt > $1.recordedAt }
    }
}
