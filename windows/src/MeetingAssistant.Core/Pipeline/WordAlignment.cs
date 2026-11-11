using System.Text;
using MeetingAssistant.Core.Model;

namespace MeetingAssistant.Core.Pipeline;

/// Word timing helpers (spec 01-recording, "ASR engines": spread fallback and the aligner attach rule).
public static class WordAlignment
{
    /// Lowercase, letters and digits only (used to compare words).
    public static string Norm(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var rune in s.ToLowerInvariant().EnumerateRunes())
            if (Rune.IsLetterOrDigit(rune)) sb.Append(rune.ToString());
        return sb.ToString();
    }

    static string[] Tokens(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// Evenly spread the words of `text` over start…end (fallback when the engine gives no timings).
    public static List<Word> Spread(string text, double start, double end)
    {
        var tokens = Tokens(text);
        if (tokens.Length == 0) return new();
        double step = Math.Max(0.01, (end - start) / tokens.Length);
        return tokens.Select((t, i) => new Word(t, start + i * step, start + (i + 1) * step)).ToList();
    }

    /// Map bare aligner words (no punctuation, sometimes split differently) onto the punctuated
    /// ASR tokens in order. Unmatched tokens get interpolated times; start times are made monotonic.
    public static List<Word> Attach(string text, IReadOnlyList<(string Text, double Start, double End)> aligned,
                                    double offset, double chunkEnd)
    {
        var tokens = Tokens(text);
        if (tokens.Length == 0) return new();
        var times = new (double Start, double End)?[tokens.Length];
        int j = 0;
        for (int i = 0; i < tokens.Length; i++)
        {
            var target = Norm(tokens[i]);
            if (target.Length == 0) continue;
            int? found = null;
            for (int k = j; k < aligned.Count && k < j + 4; k++)
            {
                var a = Norm(aligned[k].Text);
                if (a == target || (a.Length > 0 && (target.StartsWith(a, StringComparison.Ordinal) || a.StartsWith(target, StringComparison.Ordinal))))
                { found = k; break; }
            }
            if (found is not int f) continue;
            double end = aligned[f].End;
            var consumed = Norm(aligned[f].Text);
            int n = f + 1;
            while (consumed.Length < target.Length && n < aligned.Count
                   && target.StartsWith(consumed + Norm(aligned[n].Text), StringComparison.Ordinal))
            {
                consumed += Norm(aligned[n].Text);
                end = aligned[n].End;
                n++;
            }
            times[i] = (aligned[f].Start + offset, end + offset);
            j = n;
        }

        var result = new List<Word>(tokens.Length);
        for (int i = 0; i < tokens.Length; i++)
        {
            if (times[i] is { } t) { result.Add(new Word(tokens[i], t.Start, Math.Max(t.End, t.Start))); continue; }
            double prevEnd = result.Count > 0 ? result[^1].End : offset;
            double nextStart = chunkEnd;
            for (int n = i + 1; n < tokens.Length; n++) if (times[n] is { } tn) { nextStart = tn.Start; break; }
            int firstKnown = tokens.Length;
            for (int n = i; n < tokens.Length; n++) if (times[n] is not null) { firstKnown = n; break; }
            int missingRun = firstKnown - i;
            double step = Math.Max(0.01, (nextStart - prevEnd) / (missingRun + 1));
            result.Add(new Word(tokens[i], prevEnd, prevEnd + step));
        }
        for (int i = 1; i < result.Count; i++)
        {
            if (result[i].Start < result[i - 1].Start)
            {
                var s = result[i - 1].Start;
                result[i] = result[i] with { Start = s, End = Math.Max(result[i].End, s) };
            }
        }
        return result;
    }
}
