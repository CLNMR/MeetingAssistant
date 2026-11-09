import Foundation

/// A stretch of a recording in which nobody said anything substantial.
public struct Pause: Hashable, Sendable, Identifiable {
    public enum Kind: Sendable { case leading, inner, trailing }
    public var start: Double
    public var end: Double
    public var kind: Kind
    public var id: Double { start }
    public var duration: Double { end - start }

    /// Long enough that two separate meetings probably share the recording.
    public var suggestsSplit: Bool { kind == .inner && duration >= Pauses.meetingBreak }

    /// Where to split: shortly before speech resumes, so part 2 starts with a little lead-in.
    public var splitPoint: Double { max(start, end - Pauses.margin) }
    /// Where part 1 should end when the pause itself is removed.
    public var keepUntil: Double { min(end, start + Pauses.margin) }
}

/// Finds long pauses from the transcript's speech timings.
///
/// Speech recognition sometimes produces a word or two in silence ("ne", "Так", "Thank you") and a
/// stray remark during a break does not end it either, so turns of at most `minorWords` words are
/// ignored. Otherwise one long break shows up as several shorter pauses.
public enum Pauses {
    /// Shortest pause that is marked.
    public static let minimum: Double = 60
    /// Pauses from this length on are offered as a split between two meetings.
    public static let meetingBreak: Double = 180
    /// Seconds of the pause kept on each side when cutting.
    public static let margin: Double = 2
    public static let minorWords = 3

    public static func find(in transcript: Transcript, duration: Double, minimum: Double = Pauses.minimum) -> [Pause] {
        let speech = transcript.turns
            .filter { !isMinor($0) }
            .map { (start: $0.start, end: $0.end) }
            .sorted { $0.start < $1.start }
        guard !speech.isEmpty else {
            return duration >= max(minimum, meetingBreak) ? [Pause(start: 0, end: duration, kind: .leading)] : []
        }
        var out: [Pause] = []
        // A quiet start is usually greetings and waiting for people, so only a long one is marked.
        if speech[0].start >= max(minimum, meetingBreak) { out.append(Pause(start: 0, end: speech[0].start, kind: .leading)) }
        var lastEnd = speech[0].end
        for s in speech.dropFirst() {
            if s.start - lastEnd >= minimum { out.append(Pause(start: lastEnd, end: s.start, kind: .inner)) }
            lastEnd = max(lastEnd, s.end)
        }
        if duration - lastEnd >= minimum { out.append(Pause(start: lastEnd, end: duration, kind: .trailing)) }
        return out
    }

    static func isMinor(_ t: Turn) -> Bool {
        let words = t.words.isEmpty ? t.text.split(whereSeparator: \.isWhitespace).count : t.words.count
        return words <= minorWords
    }
}

extension SessionEdit {
    /// Split at a pause: part 2 starts just before speech resumes. With `removePause`, part 1 is
    /// also cut right after the last speech, so the pause disappears from both parts.
    @discardableResult
    public static func split(_ id: String, at pause: Pause, removePause: Bool) throws -> SessionInfo {
        let second = try split(id, at: pause.splitPoint)
        if removePause, pause.splitPoint - pause.keepUntil >= 1 {
            try trim(id, start: 0, end: pause.keepUntil)
        }
        return second
    }
}
