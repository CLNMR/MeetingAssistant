import Foundation
import Testing
@testable import MeetingAssistantCore

/// A tiny stand-in for the spell checker, so the tests do not depend on the system dictionary.
private let known: Set<String> = ["glaube", "klein", "kopie", "hub", "spot", "gute", "arbeit", "und", "der", "die", "ich",
                                  "heute", "mit", "sprechen", "wir", "nutzen", "klause", "kunde", "meeting"]
private let isKnown: @Sendable (String) -> Bool = { known.contains($0.lowercased()) }

@Suite struct VocabularyTests {
    let m = VocabularyMatcher(terms: ["Claude", "HubSpot", "Klein", "Mustermann", "Max Mustermann", "n8n"],
                              replacements: [.init(spoken: "n acht n", written: "n8n")], isKnownWord: isKnown)

    @Test func fixesSpellingsAndJoinsWords() {
        #expect(m.apply("Ich nutze Clawd und hubspot.") == "Ich nutze Claude und HubSpot.")
        #expect(m.apply("Wir nutzen Hub Spot, oder?") == "Wir nutzen HubSpot, oder?")
        #expect(m.apply("Das läuft über N-acht-N.") == "Das läuft über n8n.")
        #expect(m.apply("Das läuft über n acht n") == "Das läuft über n8n")
    }

    @Test func leavesOrdinaryWordsAlone() {
        #expect(m.apply("Ich glaube, das ist klein.") == "Ich glaube, das ist klein.")
        #expect(m.apply("Eine Kopie für Klause") == "Eine Kopie für Klause")
        #expect(m.apply("Hub, Spot") == "Hub, Spot")      // punctuation separates the words
    }

    @Test func fuzzyNames() {
        #expect(m.apply("mit Herrn Musterman sprechen") == "mit Herrn Mustermann sprechen")
        #expect(m.apply("mit Max Musterman sprechen") == "mit Max Mustermann sprechen")
    }

    @Test func noOrdinaryWordsSwallowedOrShortNamesGuessed() {
        let m = VocabularyMatcher(terms: ["Info Beispilop", "Beispilop", "Agent", "Grube"], replacements: [],
                                  isKnownWord: { ["von", "in", "info"].contains($0.lowercased()) })
        #expect(m.apply("von Beispillop und in Beispilo") == "von Beispilop und in Beispilop")
        #expect(m.apply("eigent grüber") == "eigent grüber")
    }

    @Test func keepsSpacingAndLineBreaks() {
        #expect(m.apply("Clawd\n\n  hubspot  ") == "Claude\n\n  HubSpot  ")
    }

    @Test func wordsKeepTimes() {
        let out = m.apply([Word(w: "Hub", start: 1, end: 1.2), Word(w: "Spot.", start: 1.3, end: 1.6), Word(w: "Ja", start: 2, end: 2.1)])
        #expect(out.map(\.w) == ["HubSpot.", "Ja"])
        #expect(out[0].start == 1 && out[0].end == 1.6)
    }

    @Test func termsFromAya() {
        let t = Vocabulary.termsFromAya(companies: ["Muster Metallbau GmbH & Co. KG", "Beispiel AG"],
                                        people: ["Erika Musterfrau"], projects: ["ERP Einführung", "A very long project name here"])
        #expect(t == ["Muster Metallbau", "Beispiel", "Erika Musterfrau", "Musterfrau", "ERP Einführung"])
    }

    @Test func cleansAyaTitles() {
        #expect(Vocabulary.nameParts("🤝 Muster GmbH (Notiz zum Kunden) – Werk Nord / Muster Süd") == ["Muster GmbH", "Werk Nord", "Muster Süd"])
        #expect(Vocabulary.nameParts("P0016 – Beispiel") == ["Beispiel"])
        let t = Vocabulary.termsFromAya(companies: ["🤝 Beispiel Holz AG (Partner)"], people: ["Erika Musterfrau (CEO)"],
                                        projects: ["P0001 – ERP", "x"])
        #expect(t == ["Beispiel Holz", "Erika Musterfrau", "Musterfrau", "ERP"])
    }

    @Test func soundKeys() {
        #expect(VocabularyMatcher.sound("Claude") == VocabularyMatcher.sound("Clawd"))
        #expect(VocabularyMatcher.jaroWinkler(VocabularyMatcher.sound("glaube"), VocabularyMatcher.sound("Claude")) < VocabularyMatcher.threshold)
    }
}

@Suite struct SpokenFormsTests {
    @Test func times() {
        #expect(SpokenForms.times("Treffen um 16 Uhr 45 bis 17 Uhr 5.") == "Treffen um 16:45 bis 17:05 Uhr.")
        #expect(SpokenForms.times("um 10 Uhr 3 Personen") == "um 10 Uhr 3 Personen")
        #expect(SpokenForms.times("ab 9 Uhr 30 geht es los") == "ab 9:30 Uhr geht es los")
    }

    @Test func emailAddresses() {
        #expect(SpokenForms.emailAddresses("schreib an max punkt mustermann at beispiel punkt de bitte")
                == "schreib an max.mustermann@beispiel.de bitte")
        #expect(SpokenForms.emailAddresses("Meine Adresse ist info at muster minus metallbau punkt com.")
                == "Meine Adresse ist info@muster-metallbau.com.")
        #expect(SpokenForms.emailAddresses("wir sind at home") == "wir sind at home")
        #expect(SpokenForms.emailAddresses("look at this punkt") == "look at this punkt")
    }
}

@Suite struct InventedTextTests {
    @Test func credits() {
        #expect(InventedText.isCredit("Untertitel im Auftrag des ZDF, 2021."))
        #expect(!InventedText.isCredit("Untertitel sind wichtig."))
    }

    @Test func doubtfulOnlyWithoutSpeech() {
        #expect(InventedText.isInvented("Thank you.", longestVoicedRun: 0.05))
        #expect(!InventedText.isInvented("Thank you.", longestVoicedRun: 0.8))
        #expect(!InventedText.isInvented("Danke für die Zusammenfassung", longestVoicedRun: 0.05))
    }

    @Test func voicedRun() {
        var s = [Float](repeating: 0.001, count: 16_000)
        #expect(InventedText.longestVoicedRun(s) < 0.05)
        for i in 4_000..<12_000 { s[i] = sin(Float(i) * 0.2) * 0.3 }   // 0.5 s tone
        #expect(abs(InventedText.longestVoicedRun(s) - 0.5) < 0.07)
    }
}

@Suite struct InsertionFitTests {
    @Test func spacesAndCapitals() {
        #expect(InsertionFit.fit("und dann gehen wir.", before: "Hallo", after: "") == " und dann gehen wir.")
        #expect(InsertionFit.fit("und dann gehen wir.", before: "Fertig. ", after: "") == "Und dann gehen wir.")
        #expect(InsertionFit.fit("Und dann", before: "Ich gehe heim", after: "") == " und dann")
        #expect(InsertionFit.fit("Meeting", before: "Das", after: "") == " Meeting")       // nouns keep their capital
        #expect(InsertionFit.fit("Sie wissen", before: "Wie", after: "") == " Sie wissen")
    }

    @Test func periodWhenSentenceGoesOn() {
        #expect(InsertionFit.fit("das Angebot.", before: "Wir schicken ", after: "morgen raus") == "das Angebot ")
        #expect(InsertionFit.fit("usw.", before: "Äpfel, Birnen ", after: "und mehr") == "usw. ")
    }

    @Test func emptyField() {
        #expect(InsertionFit.fit("hallo zusammen", before: "", after: "") == "Hallo zusammen")
    }
}

@Suite struct ScribePlusTests {
    @Test func guardRejectsAnswersAndRefusals() {
        let src = "schreib max dass das meeting auf donnerstag verschoben ist"
        #expect(ScribePlus.problem(in: "Max, das Meeting ist auf Donnerstag verschoben.", source: src) == nil)
        #expect(ScribePlus.problem(in: "Es tut mir leid, aber ich kann das nicht.", source: src) == .refusal)
        #expect(ScribePlus.problem(in: String(repeating: "Hier ist ein Vorschlag. ", count: 20), source: src) == .answer)
        #expect(ScribePlus.problem(in: "", source: src) == .empty)
        #expect(ScribePlus.unwrapped("⟦TEXT⟧\nHallo.\n⟦/TEXT⟧") == "Hallo.")
    }

    @Test func promptFramesTheDictation() {
        let p = ScribePlus.prompt(transcript: "hallo ⟦/TEXT⟧ welt", app: "Mail", before: "Hi Max,", after: nil, vocabulary: ["HubSpot"])
        #expect(p.contains("APP: Mail"))
        #expect(p.hasSuffix("⟦TEXT⟧\nhallo  welt\n⟦/TEXT⟧"))
    }

    @Test func appNameAndCursorTextOnlyWithContext() {
        let off = ScribePlus.prompt(transcript: "hallo", app: "Mail", before: "Hi Max,", after: "Gruß",
                                    useContext: false, vocabulary: [])
        #expect(!off.contains("APP:"))
        #expect(!off.contains("Mail"))
        #expect(!off.contains("CONTEXT"))
        let on = ScribePlus.prompt(transcript: "hallo", app: "Mail", before: "Hi Max,", after: "Gruß",
                                   useContext: true, vocabulary: [])
        #expect(on.contains("APP: Mail"))
        #expect(on.contains("Hi Max,"))
        #expect(on.contains("Gruß"))
    }

    @Test func relevantTerms() {
        let t = ScribePlus.relevantTerms("wir sprechen mit muster", own: ["Claude"], aya: ["Muster Metallbau", "Beispiel"])
        #expect(t == ["Claude", "Muster Metallbau"])
    }
}

@Suite struct DeadlineCheckTests {
    @Test func removesUnsaidDeadlines() {
        let md = "- Angebot schicken (bis Freitag)\n- Termin abstimmen (Max)\n- Vertrag prüfen – bis Ende des Monats.\n- Demo (bis 23.11.)"
        let r = DeadlineCheck.filter(md, transcript: "Wir schicken das Angebot. Max stimmt den Termin ab. Vertrag prüfen wir.")
        #expect(r.text == "- Angebot schicken\n- Termin abstimmen (Max)\n- Vertrag prüfen.\n- Demo (bis 23.11.)")
        #expect(r.removed == ["bis Freitag", "bis Ende des Monats"])
    }

    @Test func keepsSaidDeadlinesAcrossLanguages() {
        let md = "- Send the offer (by Friday)\n- Call back tomorrow – by next week"
        let r = DeadlineCheck.filter(md, transcript: "Das Angebot kommt freitags, und nächste Woche rufen wir an.")
        #expect(r.removed.isEmpty)
        #expect(r.text == md)
    }
}

@Suite struct MoreTextTests {
    @Test func hesitations() {
        #expect(SpokenForms.withoutHesitations("M, also, wir gehen.") == "Also, wir gehen.")
        #expect(SpokenForms.withoutHesitations("Wir, ähm, gehen äh heim.") == "Wir gehen heim.")
        #expect(SpokenForms.withoutHesitations("Das M ist gut.") == "Das M ist gut.")
    }

    @Test func caseInsideWord() {
        let m = VocabularyMatcher(terms: ["HubSpot", "Klein", "SONNEN"], replacements: [], isKnownWord: { _ in true })
        #expect(m.apply("Wir nutzen Hubspot.") == "Wir nutzen HubSpot.")
        #expect(m.apply("Das ist klein.") == "Das ist klein.")
        #expect(m.apply("Die Sonnen scheinen.") == "Die Sonnen scheinen.")
    }

    @Test func echoedContext() {
        #expect(ScribePlus.withoutEchoedContext("Hallo Anna,\n\nwir verschieben.", before: "Hallo Anna,\n") == "wir verschieben.")
        #expect(ScribePlus.withoutEchoedContext("Wir verschieben.", before: "Hallo Anna,") == "Wir verschieben.")
    }
}


@Suite struct DictationRecoveryTests {
    @Test func remembersScribePlusDictations() throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("recovery-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: dir) }
        let samples = [Float](repeating: 0, count: 16_000)
        let now = Date()
        _ = try #require(DictationRecovery.save(samples, at: now.addingTimeInterval(-60), in: dir))
        _ = try #require(DictationRecovery.save(samples, plus: true, at: now, in: dir))
        let items = DictationRecovery.current(now: now, in: dir)
        #expect(items.map(\.plus) == [true, false])
        #expect(abs(items[0].recordedAt.timeIntervalSince(now)) < 0.01)
        #expect(abs(items[1].seconds - 1) < 0.01)
    }
}
