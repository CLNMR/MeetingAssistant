using MeetingAssistant.Core.Model;

namespace MeetingAssistant.Speech;

/// Builds words from Parakeet's SentencePiece tokens (the Mac's `buildWordTimings`): a token
/// starting with a space or "▁" begins a new word; other tokens (word pieces, punctuation) are
/// appended to the current word. A word starts at its first token's time and ends where the next
/// word starts (or at its last token's time + duration, for the last word, capped at `clipEnd`).
public static class TokenWords
{
    public static List<Word> Build(IReadOnlyList<string> tokens, IReadOnlyList<float> starts, IReadOnlyList<float>? durations, double clipEnd)
    {
        var words = new List<(string Text, double Start, double LastEnd)>();
        int n = Math.Min(tokens.Count, starts.Count);
        for (int i = 0; i < n; i++)
        {
            var tok = tokens[i];
            if (tok.Length == 0) continue;
            bool startsWord = tok[0] == ' ' || tok[0] == '▁';
            var piece = tok.Replace('▁', ' ');
            double tokEnd = durations is { } d && i < d.Count && d[i] > 0
                ? starts[i] + d[i]
                : (i + 1 < n ? starts[i + 1] : Math.Min(clipEnd, starts[i] + 0.08));
            if (startsWord || words.Count == 0)
            {
                var text = piece.Trim();
                if (text.Length == 0) continue;
                words.Add((text, starts[i], tokEnd));
            }
            else
            {
                var last = words[^1];
                words[^1] = (last.Text + piece.Trim(), last.Start, Math.Max(last.LastEnd, tokEnd));
            }
        }
        var result = new List<Word>(words.Count);
        for (int i = 0; i < words.Count; i++)
        {
            double end = words[i].LastEnd;
            end = Math.Min(Math.Max(end, words[i].Start + 0.01), clipEnd);
            result.Add(new Word(words[i].Text, words[i].Start, Math.Max(end, words[i].Start)));
        }
        return result;
    }
}
