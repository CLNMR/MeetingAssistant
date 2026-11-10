import Foundation

/// Fits dictated text into the text around the cursor: a space where words would touch, a
/// capital at the start of a sentence, a lowercase function word in the middle of one, and no
/// final period when the sentence goes on after the cursor. Only the edges change.
public enum InsertionFit {
    public static func fit(_ text: String, before: String, after: String) -> String {
        var t = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !t.isEmpty else { return text }
        let b = before.replacingOccurrences(of: "\u{00A0}", with: " ")
        let trimmedBefore = b.trimmingCharacters(in: CharacterSet(charactersIn: " \t"))

        // Capitalization at the start.
        if startsSentence(trimmedBefore) {
            t = uppercasingFirst(t)
        } else if continuesSentence(trimmedBefore) {
            t = lowercasingFunctionWord(t)
        }

        // Final period when the sentence goes on after the cursor.
        if sentenceContinues(after), t.hasSuffix("."), !t.hasSuffix(".."), endsWithWord(String(t.dropLast())) {
            t.removeLast()
        }

        // Spaces.
        if let last = b.last, !last.isWhitespace, !"([{„“‚‘«\"'/-".contains(last),
           let first = t.first, !",.;:!?)]}…".contains(first) {
            t = " " + t
        }
        if let first = after.first, first.isLetter || first.isNumber || "„(\"".contains(first), let last = t.last, !last.isWhitespace, !"(/-".contains(last) {
            t += " "
        }
        return t
    }

    static func startsSentence(_ before: String) -> Bool {
        guard let last = before.last else { return true }
        return ".!?\n\r•".contains(last) || before.hasSuffix(". ") || before.hasSuffix("\n")
    }

    static func continuesSentence(_ before: String) -> Bool {
        guard let last = before.last else { return false }
        return last.isLetter || last.isNumber || ",;–-(".contains(last)
    }

    /// Lowercase word on the same line right after the cursor.
    static func sentenceContinues(_ after: String) -> Bool {
        let line = after.prefix { $0 != "\n" }.trimmingCharacters(in: .whitespaces)
        guard let first = line.first else { return false }
        return first.isLetter && first.isLowercase
    }

    /// The last word has four letters or more, so "usw." and "z. B." keep their period.
    static func endsWithWord(_ text: String) -> Bool {
        let word = text.split(separator: " ").last ?? ""
        return word.count >= 4 && word.allSatisfy(\.isLetter)
    }

    static func uppercasingFirst(_ t: String) -> String {
        guard let i = t.firstIndex(where: \.isLetter), t[i].isLowercase else { return t }
        return t.replacingCharacters(in: i...i, with: t[i].uppercased())
    }

    /// "Und dann" → "und dann" mid-sentence. Nouns keep their capital, and so do "Sie", "Ihr", "I".
    static func lowercasingFunctionWord(_ t: String) -> String {
        let word = t.prefix { $0.isLetter }
        guard let first = word.first, first.isUppercase, functionWords.contains(word.lowercased()),
              !word.dropFirst().contains(where: \.isUppercase) else { return t }
        return word.lowercased() + t.dropFirst(word.count)
    }

    static let functionWords: Set<String> = [
        // German
        "und", "oder", "aber", "denn", "doch", "sondern", "dass", "weil", "wenn", "als", "ob", "damit", "obwohl", "dann",
        "also", "auch", "noch", "schon", "nur", "sogar", "eben", "halt", "ja", "nein", "nicht", "kein", "keine", "der", "die",
        "das", "den", "dem", "des", "ein", "eine", "einen", "einem", "einer", "ich", "du", "er", "es", "wir", "mir", "mich",
        "dir", "dich", "uns", "euch", "ihm", "ihn", "in", "im", "an", "am", "auf", "aus", "bei", "mit", "nach", "von", "vom",
        "zu", "zum", "zur", "für", "über", "unter", "vor", "hinter", "neben", "zwischen", "bis", "wie", "was", "wer", "wo",
        "warum", "welche", "welcher", "welches", "so", "da", "hier", "dort", "jetzt", "heute", "morgen", "bitte", "danke",
        "mal", "man", "sich", "sein", "ist", "sind", "war", "hat", "habe", "haben", "wird", "werden", "kann", "können",
        // English
        "and", "or", "but", "so", "because", "if", "when", "that", "which", "who", "then", "also", "the", "a", "an", "to",
        "of", "in", "on", "at", "for", "with", "from", "by", "as", "is", "are", "was", "it", "we", "you", "he", "she", "they",
        "this", "these", "those", "there", "here", "not", "please", "thanks",
    ]
}
