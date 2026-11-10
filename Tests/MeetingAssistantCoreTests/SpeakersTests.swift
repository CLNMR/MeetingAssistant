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
