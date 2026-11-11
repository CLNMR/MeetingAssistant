using MeetingAssistant.Core.Model;

namespace MeetingAssistant.Core.Text;

/// <summary>
/// Clean-up of final meeting turns (spec 01 step 9, spec 04 §4): drop turns whose whole text is a
/// subtitle credit, apply the vocabulary to the timed words (text rebuilt from the words when
/// something changed), renumber ids. Mac: `TranscriptionPipeline.cleaned`.
/// </summary>
public static class TranscriptCleanup
{
    /// Cleaned copies of the turns.
    public static List<Turn> Cleaned(IEnumerable<Turn> turns, Vocabulary vocabulary, Func<string, bool>? isKnownWord = null) =>
        Cleaned(turns, vocabulary.IsEmpty ? null : vocabulary.Matcher(isKnownWord));

    /// Cleaned copies of the turns with a prepared matcher (null = no vocabulary).
    public static List<Turn> Cleaned(IEnumerable<Turn> turns, VocabularyMatcher? matcher)
    {
        var output = new List<Turn>();
        foreach (var turn in turns)
        {
            if (InventedText.IsCredit(turn.Text)) continue;
            var t = turn.Clone();
            if (matcher is not null)
            {
                var words = matcher.Apply(t.Words);
                if (!words.SequenceEqual(t.Words))
                {
                    t.Words = words;
                    t.Text = string.Join(" ", words.Select(w => w.W));
                }
            }
            t.Id = output.Count;
            output.Add(t);
        }
        return output;
    }
}
