import Foundation
import Testing
@testable import MeetingAssistantCore

@Suite struct SpeakersTests {
    private func turn(_ id: Int, _ sp: String, _ s: Double, _ e: Double, _ text: String) -> Turn {
        Turn(id: id, speaker: sp, start: s, end: e, text: text, track: .system, words: [])
    }

    @Test func generic() {
        #expect(SpeakerNames.isGeneric("Speaker 3"))
        #expect(SpeakerNames.isGeneric("Them (?)"))
        #expect(!SpeakerNames.isGeneric("Speaker Muster"))
        #expect(!SpeakerNames.isGeneric("Erika Musterfrau"))
    }

    @Test func carriesLiveNamesToFinalSpeakers() {
        let t = Transcript(turns: [turn(0, "a", 0, 10, "Guten Tag"), turn(1, "b", 10, 20, "Hallo"), turn(2, "a", 20, 30, "Weiter geht es")],
                           speakers: [Speaker(id: "a", label: "Speaker 1", track: .system, embedding: nil, isMe: false),
                                      Speaker(id: "b", label: "Speaker 2", track: .system, embedding: nil, isMe: false)],
                           engine: "test", createdAt: Date(), processingSeconds: 0)
        let lines = [AssistLine(start: 1, speaker: "Erika Musterfrau", text: "Guten Tag zusammen wie geht es"),
                     AssistLine(start: 11, speaker: "Speaker 2", text: "Hallo hallo ich bin da"),
                     AssistLine(start: 21, speaker: "Erika Musterfrau", text: "Weiter geht es mit Punkt zwei")]
        // Speaker 2 was matched to an attendee by Claude.
        let att = [AssistState.Attendee(name: "Max Beispiel", speaker: "Speaker 2", present: true)]
        let names = SpeakerNames.carryOver(transcript: t, lines: lines, attendees: att)
        #expect(names == ["a": "Erika Musterfrau", "b": "Max Beispiel"])
    }

    @Test func noNameWhenUnclear() {
        let t = Transcript(turns: [turn(0, "a", 0, 20, "x")],
                           speakers: [Speaker(id: "a", label: "Speaker 1", track: .system, embedding: nil, isMe: false)],
                           engine: "test", createdAt: Date(), processingSeconds: 0)
        let lines = [AssistLine(start: 1, speaker: "Erika", text: "eins zwei drei vier"),
                     AssistLine(start: 10, speaker: "Max", text: "eins zwei drei vier")]
        #expect(SpeakerNames.carryOver(transcript: t, lines: lines, attendees: []).isEmpty)
    }

    @Test func replacesWholeLabelsOnly() {
        #expect(SpeakerNames.replacing("Speaker 1", with: "Erika", in: "Speaker 1 und Speaker 12 sprachen.") == "Erika und Speaker 12 sprachen.")
    }

    @Test func replacesGermanAndLowercaseForms() {
        #expect(SpeakerNames.replacing("Speaker 2", with: "Max", in: "Sprecher 2 fragt, Sprecherin 2 nickt, speaker 2 geht; Sprecher 21 bleibt.")
                == "Max fragt, Max nickt, Max geht; Sprecher 21 bleibt.")
        #expect(SpeakerNames.replacing("Them (?)", with: "Max", in: "them (?) sagt") == "Max sagt")
    }

    @Test func renamesBetweenTranscriptVersions() {
        func sp(_ id: String, _ label: String) -> Speaker { Speaker(id: id, label: label, track: .system, embedding: nil, isMe: false) }
        let before = Transcript(turns: [turn(0, "a", 0, 5, "eins zwei"), turn(1, "b", 5, 9, "drei vier fünf"), turn(2, "c", 9, 12, "sechs")],
                                speakers: [sp("a", "Speaker 1"), sp("b", "Speaker 2"), sp("c", "Max Beispiel")],
                                engine: "test", createdAt: Date(), processingSeconds: 0)
        var after = before
        after.speakers[0].label = "Erika Musterfrau"            // renamed
        after.turns[1].speaker = "c"                            // Speaker 2 merged into Max Beispiel
        after.speakers.remove(at: 1)
        let r = SpeakerNames.renames(from: before, to: after)
        #expect(r.map(\.old) == ["Speaker 1", "Speaker 2"])
        #expect(r.map(\.new) == ["Erika Musterfrau", "Max Beispiel"])
        let fields = [ProposedValue(key: "summary__value", label: "Summary", kind: .markdown, options: [], old: "",
                                    value: "Speaker 1 stellt vor, Sprecher 2 fragt.", reason: nil, include: true)]
        #expect(SpeakerNames.applying(r, to: fields)[0].value == "Erika Musterfrau stellt vor, Max Beispiel fragt.")
        #expect(SpeakerNames.renames(from: before, to: before).isEmpty)
    }

    @Test func mergedSpeakerFollowedByTimeWhenTurnsAreJoined() {
        func sp(_ id: String, _ label: String) -> Speaker { Speaker(id: id, label: label, track: .system, embedding: nil, isMe: false) }
        var before = Transcript(turns: [turn(0, "c", 0, 4, "eins zwei"), turn(1, "b", 4.5, 8, "drei vier"), turn(2, "a", 9, 12, "fünf")],
                                speakers: [sp("a", "Speaker 1"), sp("b", "Speaker 2"), sp("c", "Max Beispiel")],
                                engine: "test", createdAt: Date(), processingSeconds: 0)
        before.turns.sort { $0.start < $1.start }
        var after = before
        after.merge(speaker: "b", into: "c")          // joins turns 0 and 1 and renumbers
        let r = SpeakerNames.renames(from: before, to: after)
        #expect(r.count == 1 && r[0].old == "Speaker 2" && r[0].new == "Max Beispiel")
    }

    @Test func importantPersonFields() {
        let d: [AssistContext.LabeledValue] = [
            .init(key: "utm", label: "Kampagne", value: "x"),
            .init(key: "email__value", label: "E-Mail", value: "erika@example.com"),
            .init(key: "position__value", label: "Position", value: "Geschäftsführerin"),
            .init(key: "telefon__value", label: "Telefon", value: "+49 1"),
        ]
        #expect(PersonInfo.important(d).map(\.label) == ["Position", "E-Mail", "Telefon"])
    }
}
