import Foundation

/// Speaker names across the live meeting and the final transcript.
public enum SpeakerNames {
    /// Labels the app makes up before anyone is named: "Speaker 2", "Them (?)", "?".
    public static func isGeneric(_ name: String) -> Bool {
        let n = name.trimmingCharacters(in: .whitespaces)
        if n.isEmpty || n == "?" || n == "…" || n.hasPrefix("Them") { return true }
        if n.hasPrefix("Speaker "), Int(n.dropFirst(8)) != nil { return true }
        return false
    }

    /// The name a live label stands for: what the user set, else the attendee Claude matched to it.
    public static func resolved(_ label: String, attendees: [AssistState.Attendee]) -> String {
        guard isGeneric(label) else { return label }
        if let a = attendees.first(where: { $0.speaker == label && !isGeneric($0.name) }) { return a.name }
        return label
    }

    /// Names for the speakers of the final transcript, taken from the live lines said at the same
    /// time. A speaker gets a name when at least 60 % of their matched words carry it. Speakers that
    /// already have a name (or are "me") are left alone.
    public static func carryOver(transcript t: Transcript, lines: [AssistLine],
                                 attendees: [AssistState.Attendee]) -> [String: String] {
        let named = lines.map { (line: $0, name: resolved($0.speaker, attendees: attendees)) }.filter { !isGeneric($0.name) }
        guard !named.isEmpty else { return [:] }
        var votes: [String: [String: Int]] = [:]
        for turn in t.turns {
            guard let sp = t.speakers.first(where: { $0.id == turn.speaker }), !sp.isMe, isGeneric(sp.label) else { continue }
            for (line, name) in named where line.start >= turn.start - 1 && line.start <= turn.end + 0.5 {
                let words = line.text.split(separator: " ").count
                votes[turn.speaker, default: [:]][name, default: 0] += max(1, words)
            }
        }
        var out: [String: String] = [:]
        var taken = Set<String>()
        // Most certain speakers first, so one name goes to the speaker it fits best.
        let ranked = votes.compactMap { id, v -> (String, String, Double)? in
            let total = v.values.reduce(0, +)
            guard let best = v.max(by: { $0.value < $1.value }), total >= 4 else { return nil }
            return (id, best.key, Double(best.value) / Double(total))
        }.sorted { $0.2 > $1.2 }
        for (id, name, share) in ranked where share >= 0.6 && !taken.contains(name) {
            out[id] = name
            taken.insert(name)
        }
        return out
    }

    /// Replaces a generic label ("Speaker 2") with a name in text, as a whole word only.
    public static func replacing(_ old: String, with new: String, in text: String) -> String {
        guard !old.isEmpty, old != new else { return text }
        let pattern = "(?<![\\p{L}\\p{N}])" + NSRegularExpression.escapedPattern(for: old) + "(?![\\p{L}\\p{N}])"
        guard let re = try? NSRegularExpression(pattern: pattern) else { return text }
        return re.stringByReplacingMatches(in: text, range: NSRange(text.startIndex..., in: text),
                                           withTemplate: NSRegularExpression.escapedTemplate(for: new))
    }
}

/// The few facts about a person worth showing at a glance.
public enum PersonInfo {
    /// Field groups in the order they are shown; the first matching field of each group is kept.
    static let groups: [[String]] = [
        ["position", "rolle", "funktion", "job", "titel", "title", "role"],
        ["firma", "unternehmen", "company", "organisation", "kunde"],
        ["abteilung", "department", "bereich", "team"],
        ["e-mail", "email", "mail"],
        ["mobil", "mobile", "handy"],
        ["telefon", "phone", "tel"],
        ["linkedin"],
        ["entscheider", "decision", "einfluss"],
        ["notiz", "notes", "note", "beschreibung", "description", "info", "bemerkung"],
    ]

    public static func important(_ details: [AssistContext.LabeledValue], limit: Int = 8) -> [AssistContext.LabeledValue] {
        var out: [AssistContext.LabeledValue] = []
        var used = Set<String>()
        for g in groups {
            let hit = details.first { d in
                guard !used.contains(d.key) else { return false }
                let l = d.label.lowercased(), k = d.key.lowercased()
                return g.contains { l.contains($0) || k.hasPrefix($0) }
            }
            if let hit, !hit.value.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                out.append(.init(key: hit.key, label: hit.label, value: String(hit.value.prefix(300))))
                used.insert(hit.key)
            }
            if out.count >= limit { break }
        }
        return out
    }
}
