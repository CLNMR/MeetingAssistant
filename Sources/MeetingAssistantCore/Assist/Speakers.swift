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

    /// Replaces a generic label ("Speaker 2") with a name in text, as a whole word only. Any capitalization
    /// matches, and "Speaker N" also matches "Sprecher N" / "Sprecherin N", which Claude writes in German recaps.
    public static func replacing(_ old: String, with new: String, in text: String) -> String {
        guard !old.isEmpty, old != new else { return text }
        var core = NSRegularExpression.escapedPattern(for: old)
        if old.hasPrefix("Speaker "), let n = Int(old.dropFirst(8)) { core = "(?:Speaker|Sprecher(?:in)?) \(n)" }
        let pattern = "(?<![\\p{L}\\p{N}])" + core + "(?![\\p{L}\\p{N}])"
        guard let re = try? NSRegularExpression(pattern: pattern, options: [.caseInsensitive]) else { return text }
        return re.stringByReplacingMatches(in: text, range: NSRange(text.startIndex..., in: text),
                                           withTemplate: NSRegularExpression.escapedTemplate(for: new))
    }

    /// Speakers that got a name between two versions of a transcript: a generic label renamed
    /// ("Speaker 2" → "Erika"), or a generic speaker merged into another (the label of the speaker whose
    /// turns now cover most of its speaking time wins).
    public static func renames(from before: Transcript, to after: Transcript) -> [(old: String, new: String)] {
        var out: [(old: String, new: String)] = []
        for s in before.speakers where isGeneric(s.label) {
            var new: String?
            if let a = after.speakers.first(where: { $0.id == s.id }) {
                new = a.label
            } else {
                // Merging renumbers turns and joins neighbours, so follow its turns by time, not by id.
                var overlap: [String: Double] = [:]
                for t in before.turns where t.speaker == s.id {
                    for a in after.turns {
                        let o = min(t.end, a.end) - max(t.start, a.start)
                        if o > 0 { overlap[after.label(for: a.speaker), default: 0] += o }
                    }
                }
                new = overlap.max { $0.value < $1.value }?.key
            }
            let n = new?.trimmingCharacters(in: .whitespaces) ?? ""
            if !n.isEmpty, n != s.label { out.append((s.label, n)) }
        }
        return out
    }

    /// The recap fields with the renames applied (see `replacing`).
    public static func applying(_ renames: [(old: String, new: String)], to fields: [ProposedValue]) -> [ProposedValue] {
        guard !renames.isEmpty else { return fields }
        return fields.map { f in
            var f = f
            for r in renames { f.value = replacing(r.old, with: r.new, in: f.value) }
            return f
        }
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
