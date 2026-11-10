import Foundation

/// Text speech models write without anyone saying it: subtitle credits from their training data
/// ("Untertitel im Auftrag des ZDF"), and short phrases ("Thank you.", "Okay", "Moment.") on a
/// recording that holds no real speech, such as a dictation shortcut pressed by accident.
public enum InventedText {
    /// Credits and sign-offs nobody dictates or says in a meeting. Normalized, without years.
    static let credits: Set<String> = [
        "untertitel im auftrag des zdf", "untertitel im auftrag des zdf für funk", "untertitelung im auftrag des zdf",
        "untertitel des zdf", "untertitelung des zdf", "untertitel der amara org community", "untertitel von amara org",
        "untertitel von der amara org community", "copyright wdr", "copyright swr", "copyright zdf",
        "vielen dank fürs zuschauen", "danke fürs zuschauen", "vielen dank für ihre aufmerksamkeit und bis zum nächsten mal",
        "abonniert den kanal", "bitte abonnieren", "abonniere meinen kanal",
        "subtitles by the amara org community", "subtitles by amara org", "thanks for watching", "thank you for watching",
        "thank you so much for watching", "please subscribe", "like and subscribe", "please like and subscribe",
    ]

    /// Phrases people do say, invented only when the recording has hardly any speech.
    static let doubtful: Set<String> = [
        "danke", "vielen dank", "danke schön", "dankeschön", "tschüss", "tschüs", "bis bald", "bis dann", "ende",
        "thank you", "thanks", "thank you very much", "you", "bye", "bye bye", "okay", "ok", "ja", "nein", "hmm", "hm",
        "mhm", "oh", "moment", "musik", "music", "applaus", "applause", "lachen", "so", "ne", "äh", "ähm",
    ]

    /// Shorter than this, the longest run of voice is no word: a click or bump is one or two frames.
    public static let minimumVoicedSeconds = 0.15
    /// Below this longest run, a doubtful phrase counts as invented.
    public static let doubtfulBelowSeconds = 0.35
    /// Dictations shorter than this are checked for speech before the model runs.
    public static let accidentalSeconds = 3.0

    /// Lowercase words only, no punctuation or years: "Untertitel im Auftrag des ZDF, 2021." → "untertitel im auftrag des zdf".
    static func key(_ text: String) -> String {
        let cleaned = text.lowercased().map { $0.isLetter || $0.isWhitespace ? $0 : " " }
        return String(cleaned).split(separator: " ").joined(separator: " ")
    }

    public static func isCredit(_ text: String) -> Bool { credits.contains(key(text)) }

    /// The whole text is a credit, or a doubtful phrase on a recording with hardly any voice.
    public static func isInvented(_ text: String, longestVoicedRun: Double) -> Bool {
        let k = key(text)
        guard !k.isEmpty else { return false }
        if credits.contains(k) { return true }
        return longestVoicedRun < doubtfulBelowSeconds && doubtful.contains(k)
    }

    /// Longest stretch (seconds) in which the 16 kHz signal is clearly above its own noise floor.
    /// 30 ms frames; one quiet frame inside a run does not end it.
    public static func longestVoicedRun(_ samples: [Float], sampleRate: Double = 16_000) -> Double {
        let frame = Int(sampleRate * 0.03)
        guard samples.count >= frame else { return 0 }
        var rms: [Float] = []
        rms.reserveCapacity(samples.count / frame)
        var i = 0
        while i + frame <= samples.count {
            var sum: Float = 0
            for j in i..<(i + frame) { sum += samples[j] * samples[j] }
            rms.append((sum / Float(frame)).squareRoot())
            i += frame
        }
        let sorted = rms.sorted()
        let floor = sorted[sorted.count / 5]
        let threshold = max(0.008, floor * 4)
        var best = 0, run = 0, gap = 0
        for v in rms {
            if v >= threshold { run += 1 + gap; gap = 0; best = max(best, run) }
            else if run > 0, gap == 0 { gap = 1 }
            else { run = 0; gap = 0 }
        }
        return Double(best) * 0.03
    }
}
