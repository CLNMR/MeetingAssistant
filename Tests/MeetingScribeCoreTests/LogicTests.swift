import Foundation
import Testing
@testable import MeetingScribeCore

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
                           speakers: [Speaker(id: "me", label: "Colin", track: .mic, embedding: nil, isMe: true)],
                           engine: "x", createdAt: Date(), processingSeconds: 1)
        let info = SessionInfo(id: "x", title: "Weekly", startedAt: Date(), durationSec: 60, mode: .call, tracks: [.mic],
                               expectedSpeakers: nil, language: nil, status: .done)
        let md = try Exporter.render(t, info: info, as: .markdown)
        #expect(md.contains("# Weekly") && md.contains("**Colin** [00:00]: Hallo"))
    }
}
