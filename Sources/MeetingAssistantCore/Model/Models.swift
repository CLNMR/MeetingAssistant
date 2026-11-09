import Foundation

public enum MeetingMode: String, Codable, CaseIterable, Identifiable, Sendable {
    case call, inPerson, hybrid
    public var id: String { rawValue }
    public var title: String {
        switch self {
        case .call: return "Call"
        case .inPerson: return "In person"
        case .hybrid: return "Hybrid"
        }
    }
    public var explanation: String {
        switch self {
        case .call: return "Online meeting. Records your microphone and the call audio separately. Your mic is labelled “Me”."
        case .inPerson: return "Everyone is in the room. Records the microphone only and separates speakers from it."
        case .hybrid: return "Call with several people in the room. Records both and separates speakers on both tracks."
        }
    }
    public var recordsSystemAudio: Bool { self != .inPerson }
}

public enum Track: String, Codable, Sendable { case mic, system }

public struct SessionInfo: Codable, Identifiable, Hashable, Sendable {
    public var id: String
    public var title: String
    public var startedAt: Date
    public var durationSec: Double
    public var mode: MeetingMode
    public var tracks: [Track]
    public var expectedSpeakers: Int?
    public var language: String?   // nil = auto
    public var status: Status

    public enum Status: String, Codable, Sendable { case recording, recorded, queued, transcribing, done, failed }

    public init(id: String, title: String, startedAt: Date, durationSec: Double, mode: MeetingMode,
                tracks: [Track], expectedSpeakers: Int?, language: String?, status: Status) {
        self.id = id; self.title = title; self.startedAt = startedAt; self.durationSec = durationSec
        self.mode = mode; self.tracks = tracks; self.expectedSpeakers = expectedSpeakers
        self.language = language; self.status = status
    }
}

public struct Word: Codable, Hashable, Sendable {
    public var w: String
    public var start: Double
    public var end: Double
    public init(w: String, start: Double, end: Double) { self.w = w; self.start = start; self.end = end }
}

public struct Turn: Codable, Hashable, Identifiable, Sendable {
    public var id: Int
    public var speaker: String     // speaker id, see Speaker
    public var start: Double
    public var end: Double
    public var text: String
    public var track: Track
    public var words: [Word]
    public init(id: Int, speaker: String, start: Double, end: Double, text: String, track: Track, words: [Word]) {
        self.id = id; self.speaker = speaker; self.start = start; self.end = end
        self.text = text; self.track = track; self.words = words
    }
}

public struct Speaker: Codable, Hashable, Identifiable, Sendable {
    public var id: String
    public var label: String
    public var track: Track
    public var embedding: [Float]?
    public var isMe: Bool
    public init(id: String, label: String, track: Track, embedding: [Float]?, isMe: Bool) {
        self.id = id; self.label = label; self.track = track; self.embedding = embedding; self.isMe = isMe
    }
}

public struct Transcript: Codable, Sendable {
    public var turns: [Turn]
    public var speakers: [Speaker]
    public var engine: String
    public var createdAt: Date
    public var processingSeconds: Double

    public func label(for speakerID: String) -> String {
        speakers.first { $0.id == speakerID }?.label ?? speakerID
    }

    /// Move one turn to another speaker.
    public mutating func reassign(turn id: Int, to speaker: String) {
        guard let i = turns.firstIndex(where: { $0.id == id }) else { return }
        turns[i].speaker = speaker
        normalize()
    }

    /// Merge speaker `from` into `into` (fixes a voice split over two labels).
    public mutating func merge(speaker from: String, into: String) {
        guard from != into else { return }
        for i in turns.indices where turns[i].speaker == from { turns[i].speaker = into }
        if let a = speakers.firstIndex(where: { $0.id == from }), let b = speakers.firstIndex(where: { $0.id == into }),
           let ea = speakers[a].embedding, let eb = speakers[b].embedding {
            speakers[b].embedding = SpeakerMath.mean([ea, eb])
        }
        speakers.removeAll { $0.id == from }
        normalize()
    }

    /// Join consecutive turns of the same speaker and renumber.
    public mutating func normalize(maxGap: Double = 1.5) {
        var out: [Turn] = []
        for t in turns.sorted(by: { $0.start < $1.start }) {
            if var last = out.last, last.speaker == t.speaker, t.start - last.end < maxGap {
                last.text += " " + t.text; last.words += t.words; last.end = max(last.end, t.end)
                out[out.count - 1] = last
            } else { out.append(t) }
        }
        for i in out.indices { out[i].id = i }
        turns = out
        let used = Set(turns.map(\.speaker))
        speakers.removeAll { !used.contains($0.id) }
    }
}

/// On-disk layout: ~/Library/Application Support/MeetingAssistant/
/// (called MeetingScribe before the rename; the old folder is moved on first use).
public enum AppPaths {
    public static let root: URL = {
        let env = ProcessInfo.processInfo.environment
        if let override = env["MEETINGASSISTANT_HOME"] ?? env["MEETINGSCRIBE_HOME"] {
            return URL(fileURLWithPath: override, isDirectory: true)
        }
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        return migratedRoot(base: base)
    }()

    /// Moves `MeetingScribe/` to `MeetingAssistant/` once. If the move is impossible, keeps using the old folder,
    /// so existing recordings are never lost or hidden.
    static func migratedRoot(base: URL) -> URL {
        let fm = FileManager.default
        let new = base.appendingPathComponent("MeetingAssistant", isDirectory: true)
        let old = base.appendingPathComponent("MeetingScribe", isDirectory: true)
        guard fm.fileExists(atPath: old.path) else { return new }
        if !fm.fileExists(atPath: new.path) {
            if (try? fm.moveItem(at: old, to: new)) != nil { return new }
            return old
        }
        // Both exist (e.g. an old build ran after the move): keep using the new one unless it is empty.
        let newItems = (try? fm.contentsOfDirectory(atPath: new.path)) ?? []
        return newItems.isEmpty ? old : new
    }
    public static var sessions: URL { root.appendingPathComponent("Sessions", isDirectory: true) }
    public static var models: URL { root.appendingPathComponent("Models", isDirectory: true) }
    public static var voiceprint: URL { root.appendingPathComponent("me-voiceprint.json") }
    public static var speakerLibrary: URL { root.appendingPathComponent("speaker-library.json") }

    public static func session(_ id: String) -> URL { sessions.appendingPathComponent(id, isDirectory: true) }
    public static func track(_ id: String, _ t: Track) -> URL { session(id).appendingPathComponent("\(t.rawValue).caf") }
    public static func info(_ id: String) -> URL { session(id).appendingPathComponent("session.json") }
    public static func transcript(_ id: String) -> URL { session(id).appendingPathComponent("transcript.json") }

    public static func ensure() throws {
        for dir in [root, sessions, models] {
            try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        }
    }
}

public enum JSONIO {
    public static let encoder: JSONEncoder = {
        let e = JSONEncoder(); e.outputFormatting = [.prettyPrinted, .sortedKeys]; e.dateEncodingStrategy = .iso8601; return e
    }()
    public static let decoder: JSONDecoder = {
        let d = JSONDecoder(); d.dateDecodingStrategy = .iso8601; return d
    }()
    public static func write<T: Encodable>(_ value: T, to url: URL) throws {
        try encoder.encode(value).write(to: url, options: .atomic)
    }
    public static func read<T: Decodable>(_ type: T.Type, from url: URL) throws -> T {
        try decoder.decode(type, from: Data(contentsOf: url))
    }
}
