import AVFoundation
import Foundation
import Testing
@testable import MeetingAssistantCore

private func w(_ s: String, _ a: Double, _ b: Double) -> Word { Word(w: s, start: a, end: b) }

@Suite struct ChunkingTests {
    @Test func joinsCloseRegionsAndSplitsLongOnes() {
        let regions = [TimeSpan(0, 2), TimeSpan(2.5, 4), TimeSpan(10, 40)]
        let c = Chunking.chunks(from: regions, maxLength: 14, maxGap: 1.2, padding: 0, totalDuration: 50)
        #expect(c.first == TimeSpan(0, 4))               // first two merged
        #expect(c.dropFirst().allSatisfy { $0.duration <= 14.0001 })
        #expect(abs(c.dropFirst().reduce(0) { $0 + $1.duration } - 30) < 0.001)
    }
}

@Suite struct WordAlignmentTests {
    @Test func keepsPunctuationAndUsesAlignerTimes() {
        let words = WordAlignment.attach(text: "Hallo, Welt! Wie geht's?",
                                         aligned: [("hallo", 0.1, 0.4), ("welt", 0.5, 0.8), ("wie", 1.0, 1.1), ("geht", 1.2, 1.3), ("s", 1.3, 1.4)],
                                         offset: 10, chunkEnd: 12)
        #expect(words.map(\.w) == ["Hallo,", "Welt!", "Wie", "geht's?"])
        #expect(abs(words[0].start - 10.1) < 1e-9)
        #expect(abs(words[3].end - 11.4) < 1e-9)          // split token consumed
    }

    @Test func interpolatesUnmatchedWords() {
        let words = WordAlignment.attach(text: "eins zwei drei", aligned: [("eins", 0, 0.5), ("drei", 2, 2.5)], offset: 0, chunkEnd: 3)
        #expect(words.count == 3)
        #expect(words[1].start >= 0.5 && words[1].end <= 2)
    }
}

@Suite struct MergerTests {
    @Test func assignsByOverlapAndNearest() {
        let r = TrackResult(track: .system, words: [w("a", 0, 0.5), w("b", 1, 1.5), w("c", 5.2, 5.5), w("d", 20, 21)],
                            segments: [("s1", TimeSpan(0, 2)), ("s2", TimeSpan(5, 6))], fixedSpeaker: nil)
        let tagged = Merger.assignSpeakers(r).map(\.1)
        #expect(tagged == ["s1", "s1", "s2", "s2"])        // "d" far from all -> previous speaker
    }

    @Test func fixedSpeakerWhenNoSegments() {
        let r = TrackResult(track: .mic, words: [w("x", 0, 1)], segments: nil, fixedSpeaker: "me")
        #expect(Merger.assignSpeakers(r).map(\.1) == ["me"])
    }

    @Test func smoothsSingleWordFlips() {
        let r = TrackResult(track: .mic, words: [w("a", 0, 0.3), w("b", 0.35, 0.6), w("c", 0.65, 0.9)],
                            segments: [("A", TimeSpan(0, 0.32)), ("B", TimeSpan(0.33, 0.62)), ("A", TimeSpan(0.63, 1))], fixedSpeaker: nil)
        #expect(Merger.assignSpeakers(r).map(\.1) == ["A", "A", "A"])
    }

    @Test func removesEchoedRunsButKeepsOwnSpeech() {
        let system = [w("wir", 10, 10.2), w("sind", 10.2, 10.4), w("im", 10.4, 10.5), w("plan", 10.5, 10.8)]
        let mic: [(Word, String)] = [(w("Wir", 10.1, 10.3), "me"), (w("sind", 10.3, 10.5), "me"), (w("im", 10.5, 10.6), "me"),
                                     (w("Plan.", 10.6, 10.9), "me"), (w("Danke", 15, 15.4), "me"), (w("schön", 15.4, 15.8), "me")]
        let kept = Merger.removeEcho(mic: mic, system: system).map(\.0.w)
        #expect(kept == ["Danke", "schön"])
    }

    @Test func turnsGroupPerSpeakerAndSortByStart() {
        let tagged: [(Word, String, Track)] = [(w("Hallo", 0, 0.5), "me", .mic), (w("zusammen", 0.6, 1), "me", .mic),
                                               (w("Hi", 0.8, 1.0), "s1", .system), (w("Ja", 5, 5.2), "me", .mic)]
        let t = Merger.turns(tagged)
        #expect(t.map(\.text) == ["Hallo zusammen", "Hi", "Ja"])
        #expect(t.map(\.id) == [0, 1, 2])
    }
}

@Suite struct SpeakerMathTests {
    @Test func mergesToTargetCount() {
        let c: [String: [Float]] = ["a": [1, 0, 0], "b": [0.9, 0.1, 0], "c": [0, 0, 1]]
        let m = SpeakerMath.mergeClusters(c, weights: ["a": 10, "b": 2, "c": 5], target: 2, autoMergeAbove: 2)
        #expect(m["b"] == "a" && m["a"] == "a" && m["c"] == "c")
    }

    @Test func autoMergesOnlyVerySimilar() {
        let c: [String: [Float]] = ["a": [1, 0], "b": [0.99, 0.05], "c": [0, 1]]
        let m = SpeakerMath.mergeClusters(c, weights: [:], target: nil, autoMergeAbove: 0.9)
        #expect(Set(m.values).count == 2)
    }

    @Test func matchNeedsThresholdAndMargin() {
        let me: [Float] = [1, 0, 0]
        let clear = [("a", [Float]([0.9, 0.3, 0])), ("b", [Float]([0.2, 1, 0]))]
        #expect(SpeakerMatch.best(for: me, among: clear, threshold: 0.5, margin: 0.08, reference: []) == "a")
        let ambiguous = [("a", [Float]([0.9, 0.3, 0])), ("b", [Float]([0.88, 0.32, 0]))]
        #expect(SpeakerMatch.best(for: me, among: ambiguous, threshold: 0.5, margin: 0.08, reference: []) == nil)
        let weak = [("a", [Float]([0.3, 1, 0]))]
        #expect(SpeakerMatch.best(for: me, among: weak, threshold: 0.5, margin: 0.08, reference: []) == nil)
    }

    @Test func libraryLearnsRunningAverage() {
        var lib = SpeakerLibrary()
        lib.learn(name: "Anna", embedding: [1, 0])
        lib.learn(name: "anna", embedding: [0, 1])
        #expect(lib.entries.count == 1 && lib.entries[0].count == 2)
        #expect(abs(SpeakerMath.cosine(lib.entries[0].embedding, [1, 1]) - 1) < 1e-5)
    }
}

@Suite struct ExporterTests {
    @Test func formatsTimes() {
        #expect(Exporter.clock(65) == "01:05")
        #expect(Exporter.clock(3725) == "1:02:05")
        #expect(Exporter.srtTime(3725.5) == "01:02:05,500")
    }

    @Test func rendersMarkdownWithLabels() throws {
        let t = Transcript(turns: [Turn(id: 0, speaker: "me", start: 0, end: 1, text: "Hallo", track: .mic, words: [w("Hallo", 0, 1)])],
                           speakers: [Speaker(id: "me", label: "Erika", track: .mic, embedding: nil, isMe: true)],
                           engine: "x", createdAt: Date(), processingSeconds: 1)
        let info = SessionInfo(id: "x", title: "Weekly", startedAt: Date(), durationSec: 60, mode: .call, tracks: [.mic],
                               expectedSpeakers: nil, language: nil, status: .done)
        let md = try Exporter.render(t, info: info, as: .markdown)
        #expect(md.contains("# Weekly") && md.contains("**Erika** [00:00]: Hallo"))
    }
}

@Suite struct AppPathsMigrationTests {
    private func tempBase() -> URL {
        let u = FileManager.default.temporaryDirectory.appendingPathComponent("ma-paths-\(UUID().uuidString)", isDirectory: true)
        try? FileManager.default.createDirectory(at: u, withIntermediateDirectories: true)
        return u
    }

    @Test func movesOldFolderOnce() throws {
        let base = tempBase(), fm = FileManager.default
        let old = base.appendingPathComponent("MeetingScribe/Sessions/abc", isDirectory: true)
        try fm.createDirectory(at: old, withIntermediateDirectories: true)
        let root = AppPaths.migratedRoot(base: base)
        #expect(root.lastPathComponent == "MeetingAssistant")
        #expect(fm.fileExists(atPath: root.appendingPathComponent("Sessions/abc").path))
        #expect(!fm.fileExists(atPath: base.appendingPathComponent("MeetingScribe").path))
        #expect(AppPaths.migratedRoot(base: base) == root)          // idempotent
    }

    @Test func freshInstallUsesNewName() {
        #expect(AppPaths.migratedRoot(base: tempBase()).lastPathComponent == "MeetingAssistant")
    }

    @Test func emptyNewFolderDoesNotHideOldData() throws {
        let base = tempBase(), fm = FileManager.default
        try fm.createDirectory(at: base.appendingPathComponent("MeetingScribe/Sessions"), withIntermediateDirectories: true)
        try fm.createDirectory(at: base.appendingPathComponent("MeetingAssistant"), withIntermediateDirectories: true)
        #expect(AppPaths.migratedRoot(base: base).lastPathComponent == "MeetingScribe")
    }
}

@Suite struct PauseTests {
    private func turn(_ text: String, _ a: Double, _ b: Double) -> Turn {
        let parts = text.split(separator: " ").map(String.init)
        let step = (b - a) / Double(parts.count)
        let words = parts.enumerated().map { Word(w: $1, start: a + Double($0) * step, end: a + Double($0 + 1) * step) }
        return Turn(id: 0, speaker: "s1", start: a, end: b, text: text, track: .mic, words: words)
    }
    private func transcript(_ turns: [Turn]) -> Transcript {
        Transcript(turns: turns, speakers: [], engine: "test", createdAt: Date(), processingSeconds: 0)
    }

    @Test func findsInnerLeadingAndTrailingPauses() {
        let t = transcript([turn("hallo zusammen wie geht es", 200, 210),
                            turn("das war es dann für heute", 110 + 100, 120 + 100),
                            turn("guten morgen und willkommen hier", 500, 510)])
        let p = Pauses.find(in: t, duration: 600)
        #expect(p.map(\.kind) == [.leading, .inner, .trailing])
        #expect(p[1].start == 220 && p[1].end == 500)
        #expect(p[1].suggestsSplit)
        #expect(p[1].splitPoint == 498 && p[1].keepUntil == 222)
        #expect(!p[2].suggestsSplit)
    }

    @Test func strayWordsDoNotBreakAPause() {
        // As seen in real recordings: a word or two recognised in the middle of a break.
        let t = transcript([turn("wir sehen uns nächste woche", 0, 10),
                            turn("ne", 130, 131),
                            turn("okay, one moment.", 260, 262),
                            turn("so jetzt starten wir mal richtig", 500, 510)])
        let p = Pauses.find(in: t, duration: 512)
        #expect(p.count == 1)
        #expect(p[0].start == 10 && p[0].end == 500)
    }

    @Test func shortQuietStartIsNotMarked() {
        let t = transcript([turn("so dann fangen wir mal an", 90, 100)])
        #expect(Pauses.find(in: t, duration: 110).isEmpty)
    }

    @Test func shortGapsAreNotPauses() {
        let t = transcript([turn("eins zwei drei vier", 0, 5), turn("fünf sechs sieben acht", 50, 55)])
        #expect(Pauses.find(in: t, duration: 60).isEmpty)
    }
}

/// Splits a real (silent) recording on disk. The only test that touches AppPaths.root, so it
/// can point the app folder at a temporary directory first.
@Suite struct SplitAtPauseTests {
    @Test func splitsAudioAndTranscriptAndRemovesThePause() throws {
        let home = FileManager.default.temporaryDirectory.appendingPathComponent("ma-split-\(UUID().uuidString)")
        setenv("MEETINGASSISTANT_HOME", home.path, 1)
        defer { try? FileManager.default.removeItem(at: home) }
        #expect(AppPaths.root.path == home.path)

        let id = "2026-01-01_10-00-00"
        try FileManager.default.createDirectory(at: AppPaths.session(id), withIntermediateDirectories: true)
        let fmt = AVAudioFormat(standardFormatWithSampleRate: 8000, channels: 1)!
        do {
            let file = try AVAudioFile(forWriting: AppPaths.track(id, .mic), settings: fmt.settings)
            let buf = AVAudioPCMBuffer(pcmFormat: fmt, frameCapacity: 8000 * 400)!
            buf.frameLength = 8000 * 400
            try file.write(from: buf)
        }

        let start = Date(timeIntervalSince1970: 1_000_000)
        try SessionStore.save(SessionInfo(id: id, title: "Two", startedAt: start, durationSec: 400, mode: .inPerson,
                                          tracks: [.mic], expectedSpeakers: nil, language: nil, status: .done))
        func turn(_ text: String, _ a: Double, _ b: Double) -> Turn {
            Turn(id: 0, speaker: "s1", start: a, end: b, text: text, track: .mic, words: [])
        }
        let tr = Transcript(turns: [turn("erstes meeting endet hier jetzt", 0, 100),
                                    turn("zweites meeting fängt jetzt an", 350, 390)],
                            speakers: [Speaker(id: "s1", label: "A", track: .mic, embedding: nil, isMe: false)],
                            engine: "t", createdAt: start, processingSeconds: 0)
        try SessionStore.saveTranscript(tr, id: id)

        let pause = try #require(Pauses.find(in: tr, duration: 400).first { $0.kind == .inner })
        let second = try SessionEdit.split(id, at: pause, removePause: true)

        let first = try JSONIO.read(SessionInfo.self, from: AppPaths.info(id))
        #expect(abs(first.durationSec - 102) < 0.01)
        #expect(abs(second.durationSec - 52) < 0.01)
        #expect(second.startedAt == start.addingTimeInterval(348))
        #expect(abs(Double(try AVAudioFile(forReading: AppPaths.track(id, .mic)).length) - 102 * 8000) < 2)
        #expect(abs(Double(try AVAudioFile(forReading: AppPaths.track(second.id, .mic)).length) - 52 * 8000) < 2)
        #expect(SessionStore.transcript(id)?.turns.map(\.text) == ["erstes meeting endet hier jetzt"])
        let t2 = try #require(SessionStore.transcript(second.id))
        #expect(t2.turns.map(\.text) == ["zweites meeting fängt jetzt an"])
        #expect(abs(t2.turns[0].start - 2) < 0.01)
    }
}
