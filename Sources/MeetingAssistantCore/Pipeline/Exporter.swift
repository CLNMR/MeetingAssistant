import Foundation

public enum ExportFormat: String, CaseIterable, Identifiable, Sendable {
    case markdown = "md", text = "txt", srt, json
    public var id: String { rawValue }
    public var title: String {
        switch self {
        case .markdown: return "Markdown"
        case .text: return "Plain text"
        case .srt: return "Subtitles (SRT)"
        case .json: return "JSON"
        }
    }
}

public enum Exporter {
    public static func clock(_ t: Double) -> String {
        let s = Int(t)
        return s >= 3600 ? String(format: "%d:%02d:%02d", s / 3600, s / 60 % 60, s % 60)
                         : String(format: "%02d:%02d", s / 60, s % 60)
    }

    static func srtTime(_ t: Double) -> String {
        let ms = Int((t * 1000).rounded())
        return String(format: "%02d:%02d:%02d,%03d", ms / 3_600_000, ms / 60_000 % 60, ms / 1000 % 60, ms % 1000)
    }

    public static func render(_ t: Transcript, info: SessionInfo, as format: ExportFormat) throws -> String {
        switch format {
        case .markdown:
            let df = DateFormatter(); df.dateStyle = .full; df.timeStyle = .short
            var out = "# \(info.title)\n\n"
            out += "*\(df.string(from: info.startedAt)) · \(clock(info.durationSec)) · \(info.mode.title)*\n\n"
            let names = t.speakers.map(\.label)
            if !names.isEmpty { out += "**Participants:** \(names.joined(separator: ", "))\n\n" }
            for turn in t.turns {
                out += "**\(t.label(for: turn.speaker))** [\(clock(turn.start))]: \(turn.text)\n\n"
            }
            return out
        case .text:
            return t.turns.map { "[\(clock($0.start))] \(t.label(for: $0.speaker)): \($0.text)" }.joined(separator: "\n") + "\n"
        case .srt:
            var out = ""
            var n = 1
            for turn in t.turns {
                // Keep subtitles readable: split long turns into ~12-word cues.
                for part in stride(from: 0, to: turn.words.count, by: 12) {
                    let ws = Array(turn.words[part..<min(turn.words.count, part + 12)])
                    guard let f = ws.first, let l = ws.last else { continue }
                    out += "\(n)\n\(srtTime(f.start)) --> \(srtTime(max(l.end, f.start + 0.5)))\n"
                    out += "\(t.label(for: turn.speaker)): \(ws.map(\.w).joined(separator: " "))\n\n"
                    n += 1
                }
            }
            return out
        case .json:
            return String(decoding: try JSONIO.encoder.encode(t), as: UTF8.self)
        }
    }
}
