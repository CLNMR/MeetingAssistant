import Foundation
import MeetingAssistantCore

/// Speaker names, people from Aya, past meetings and imported recordings.
extension AssistController {

    // MARK: - Recap draft

    /// Saves the edited recap shortly after the last change.
    func saveDraftSoon() {
        guard let id = recordID else { return }
        draftSave?.cancel()
        draftSave = Task { [weak self] in
            try? await Task.sleep(nanoseconds: 600_000_000)
            guard !Task.isCancelled else { return }
            self?.flushDraft(only: id)
        }
    }

    func flushDraft(only: String? = nil) {
        draftSave?.cancel(); draftSave = nil
        guard let id = recordID, only == nil || only == id, var rec = record else { return }
        let draft = recapFields.isEmpty ? nil : recapFields
        guard rec.recapDraft != draft else { return }
        rec.recapDraft = draft
        try? rec.save(id)
        record = rec
    }

    // MARK: - Speakers during the meeting

    /// The name shown for a live line, or nil while its speaker is not known yet.
    func liveName(_ l: LiveLine) -> String? {
        guard let raw = l.speaker else { return nil }
        let me = app.prefs.myName.isEmpty ? "Me" : app.prefs.myName
        if let n = state.speakerNames?[raw] { return SpeakerNames.resolved(n, attendees: state.attendees) }
        return raw == "me" ? me : nil
    }

    /// The live label ("system-1") an attendee speaks under, when known.
    func rawSpeaker(for a: AssistState.Attendee) -> String? {
        guard let s = a.speaker else { return nil }
        return state.speakerNames?.first { $0.value == s }?.key
    }

    func renameSpeaker(raw: String, to name: String) {
        Task { await engine?.renameSpeaker(raw: raw, to: name) }
    }

    /// Names to choose from: attendees, the client's contacts, then the team.
    var nameSuggestions: [String] {
        var out: [String] = []
        let me = app.prefs.myName
        let names = state.attendees.map(\.name) + (context?.contacts.map(\.name) ?? []) + members.map(\.title)
        for n in names where !SpeakerNames.isGeneric(n) && n != me && !out.contains(n) { out.append(n) }
        return out
    }

    // MARK: - People from Aya

    struct PersonCard: Equatable {
        var name: String
        var source: String
        var details: [AssistContext.LabeledValue]
    }

    /// What Aya knows about a person: a contact of this meeting's client, or a team member.
    func personCard(name: String, contactID: String? = nil) -> PersonCard? {
        let contacts = context?.contacts ?? []
        let client = context?.clientTitle ?? ""
        if let c = contacts.first(where: { $0.id == contactID }) ?? Self.match(name, in: contacts, \.name) {
            return PersonCard(name: c.name, source: client.isEmpty ? "Contact in Aya" : "Contact at \(client)",
                              details: PersonInfo.important(c.details))
        }
        if let m = Self.match(name, in: members, \.title) {
            return PersonCard(name: m.title, source: "Team member in Aya",
                              details: PersonInfo.important(AssistContext.labeledValues(m, schema: schemas[AyaSlot.members.rawValue])))
        }
        return nil
    }

    /// A contact that is not part of the meeting context, loaded from Aya by id.
    func loadContact(_ id: String) async -> PersonCard? {
        guard let svc = service, let r = try? await svc.record(.contacts, id: id) else { return nil }
        return PersonCard(name: r.title, source: "Contact in Aya",
                          details: PersonInfo.important(AssistContext.labeledValues(r, schema: schemas[AyaSlot.contacts.rawValue])))
    }

    /// Same name ignoring case and accents, else the only entry whose name starts with every word given
    /// ("Anna" → "Anna Muster", but not when there are two Annas).
    static func match<T>(_ name: String, in items: [T], _ key: (T) -> String) -> T? {
        let n = name.trimmingCharacters(in: .whitespaces)
        guard !n.isEmpty, !SpeakerNames.isGeneric(n) else { return nil }
        let opts: String.CompareOptions = [.caseInsensitive, .diacriticInsensitive]
        if let hit = items.first(where: { key($0).compare(n, options: opts) == .orderedSame }) { return hit }
        let words = n.split(separator: " ").map(String.init)
        let hits = items.filter { item in
            let parts = key(item).split(separator: " ").map(String.init)
            return words.allSatisfy { w in parts.contains { $0.range(of: w, options: opts.union(.anchored)) != nil } }
        }
        return hits.count == 1 ? hits[0] : nil
    }

    // MARK: - Speakers after the meeting

    /// Gives the final transcript's speakers the names they had during the meeting (once).
    func carrySpeakerNames(_ id: String) {
        guard recordID == id else { return }
        reviewTranscript = SessionStore.transcript(id)
        guard var rec = record, rec.speakersCarried != true else { return }
        defer { rec.speakersCarried = true; try? rec.save(id); record = rec }
        guard var t = SessionStore.transcript(id) else { return }
        let names = SpeakerNames.carryOver(transcript: t, lines: rec.lines, attendees: rec.state.attendees)
        guard !names.isEmpty else { reviewTranscript = t; return }
        for i in t.speakers.indices { if let n = names[t.speakers[i].id] { t.speakers[i].label = n } }
        try? SessionStore.saveTranscript(t, id: id)
        reviewTranscript = t
    }

    /// Names a speaker of the final transcript; the recap draft follows.
    func renameFinalSpeaker(_ speakerID: String, to name: String) {
        let n = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let id = recordID, !n.isEmpty, var t = SessionStore.transcript(id),
              let i = t.speakers.firstIndex(where: { $0.id == speakerID }) else { return }
        let old = t.speakers[i].label
        guard old != n else { return }
        t.speakers[i].label = n
        try? SessionStore.saveTranscript(t, id: id)
        reviewTranscript = t
        if let e = t.speakers[i].embedding, !t.speakers[i].isMe {
            var lib = SpeakerLibrary.load()
            lib.learn(name: n, embedding: e)
            try? lib.save()
        }
        if SpeakerNames.isGeneric(old) {
            recapFields = recapFields.map { f in var f = f; f.value = SpeakerNames.replacing(old, with: n, in: f.value); return f }
        }
    }

    // MARK: - Past meetings

    func deleteMeeting(_ id: String) {
        if recordID == id { finishReview() }
        app.delete(id)
    }

    // MARK: - Import

    /// Imports a recording as an Assist meeting: linked to a meeting in Aya, or with client, project and
    /// template for a new one. It is transcribed, then the review drafts the recap.
    func importMeeting(files: [URL], mode: MeetingMode, date: Date, title: String, meetingID: String?, clientID: String?,
                       projectID: String?, templateID: String?) async throws {
        guard let first = files.first else { return }
        var aya = AyaMeetingContext()
        if [meetingID, clientID, projectID, templateID].contains(where: { $0 != nil }) {
            guard let svc = service else { throw AyaError.notConfigured }
            aya = try await svc.meetingContext(meetingID: meetingID, clientID: clientID, projectID: projectID, templateID: templateID)
        }
        let me = app.prefs.myName.trimmingCharacters(in: .whitespaces)
        var ctx = AssistContext(aya: aya, config: config, goal: "", mode: mode, myName: me.isEmpty ? "Me" : me, date: date)
        let name = title.trimmingCharacters(in: .whitespaces)
        if !name.isEmpty { ctx.meetingTitle = name }      // a typed title wins
        let sessionTitle = ctx.title == "Meeting" ? first.deletingPathExtension().lastPathComponent : ctx.title
        let system = files.count > 1 ? files[1] : nil
        let info = try await Task.detached {
            try SessionStore.importFiles(mic: first, system: system, mode: mode, title: sessionTitle, startedAt: date)
        }.value
        var st = AssistState()
        st.playbook = ctx.playbookItems
        st.tables = ctx.templateTables
        try AssistRecord(context: ctx, state: st, lines: []).save(info.id)
        app.reload()
        app.enqueue(info.id)
        openReview(info.id)
    }
}
