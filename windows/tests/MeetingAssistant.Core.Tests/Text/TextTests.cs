using MeetingAssistant.Core.Model;
using MeetingAssistant.Core.Text;
using Xunit;

namespace MeetingAssistant.Core.Tests.Text;

// Ported from Tests/MeetingAssistantCoreTests/TextTests.swift (same inputs and expected outputs),
// plus tests for each numeric rule of spec 04 §2–§5.

static class Known
{
    /// A tiny stand-in for the spell checker, so the tests do not depend on a system dictionary.
    static readonly HashSet<string> Words = new()
    {
        "glaube", "klein", "kopie", "hub", "spot", "gute", "arbeit", "und", "der", "die", "ich",
        "heute", "mit", "sprechen", "wir", "nutzen", "klause", "kunde", "meeting",
    };
    public static bool IsKnown(string w) => Words.Contains(w.ToLowerInvariant());
}

public class VocabularyTests
{
    readonly VocabularyMatcher m = new(new[] { "Claude", "HubSpot", "Klein", "Mustermann", "Max Mustermann", "n8n" },
        new[] { new Vocabulary.Replacement("n acht n", "n8n") }, isKnownWord: Known.IsKnown);

    [Fact]
    public void FixesSpellingsAndJoinsWords()
    {
        Assert.Equal("Ich nutze Claude und HubSpot.", m.Apply("Ich nutze Clawd und hubspot."));
        Assert.Equal("Wir nutzen HubSpot, oder?", m.Apply("Wir nutzen Hub Spot, oder?"));
        Assert.Equal("Das läuft über n8n.", m.Apply("Das läuft über N-acht-N."));
        Assert.Equal("Das läuft über n8n", m.Apply("Das läuft über n acht n"));
    }

    [Fact]
    public void LeavesOrdinaryWordsAlone()
    {
        Assert.Equal("Ich glaube, das ist klein.", m.Apply("Ich glaube, das ist klein."));
        Assert.Equal("Eine Kopie für Klause", m.Apply("Eine Kopie für Klause"));
        Assert.Equal("Hub, Spot", m.Apply("Hub, Spot"));      // punctuation separates the words
    }

    [Fact]
    public void FuzzyNames()
    {
        Assert.Equal("mit Herrn Mustermann sprechen", m.Apply("mit Herrn Musterman sprechen"));
        Assert.Equal("mit Max Mustermann sprechen", m.Apply("mit Max Musterman sprechen"));
    }

    [Fact]
    public void NoOrdinaryWordsSwallowedOrShortNamesGuessed()
    {
        var m2 = new VocabularyMatcher(new[] { "Info Beispilop", "Beispilop", "Agent", "Grube" }, Array.Empty<Vocabulary.Replacement>(),
            isKnownWord: w => new[] { "von", "in", "info" }.Contains(w.ToLowerInvariant()));
        Assert.Equal("von Beispilop und in Beispilop", m2.Apply("von Beispillop und in Beispilo"));
        Assert.Equal("eigent grüber", m2.Apply("eigent grüber"));
    }

    [Fact]
    public void KeepsSpacingAndLineBreaks() => Assert.Equal("Claude\n\n  HubSpot  ", m.Apply("Clawd\n\n  hubspot  "));

    [Fact]
    public void WordsKeepTimes()
    {
        var output = m.Apply(new[] { new Word("Hub", 1, 1.2), new Word("Spot.", 1.3, 1.6), new Word("Ja", 2, 2.1) });
        Assert.Equal(new[] { "HubSpot.", "Ja" }, output.Select(w => w.W));
        Assert.Equal(1, output[0].Start);
        Assert.Equal(1.6, output[0].End);
    }

    [Fact]
    public void TermsFromAya()
    {
        var t = Vocabulary.TermsFromAya(new[] { "Muster Metallbau GmbH & Co. KG", "Beispiel AG" },
            new[] { "Erika Musterfrau" }, new[] { "ERP Einführung", "A very long project name here" });
        Assert.Equal(new[] { "Muster Metallbau", "Beispiel", "Erika Musterfrau", "Musterfrau", "ERP Einführung" }, t);
    }

    [Fact]
    public void CleansAyaTitles()
    {
        Assert.Equal(new[] { "Muster GmbH", "Werk Nord", "Muster Süd" },
            Vocabulary.NameParts("🤝 Muster GmbH (Notiz zum Kunden) – Werk Nord / Muster Süd"));
        Assert.Equal(new[] { "Beispiel" }, Vocabulary.NameParts("P0016 – Beispiel"));
        var t = Vocabulary.TermsFromAya(new[] { "🤝 Beispiel Holz AG (Partner)" }, new[] { "Erika Musterfrau (CEO)" },
            new[] { "P0001 – ERP", "x" });
        Assert.Equal(new[] { "Beispiel Holz", "Erika Musterfrau", "Musterfrau", "ERP" }, t);
    }

    [Fact]
    public void SoundKeys()
    {
        Assert.Equal(VocabularyMatcher.Sound("Claude"), VocabularyMatcher.Sound("Clawd"));
        Assert.True(VocabularyMatcher.JaroWinkler(VocabularyMatcher.Sound("glaube"), VocabularyMatcher.Sound("Claude")) < VocabularyMatcher.Threshold);
    }

    // --- numeric rules (spec 04 §5) ---

    [Fact]
    public void FuzzyNeedsSixLetterTermsAndFiveLetterWindows()
    {
        Assert.Equal(6, VocabularyMatcher.FuzzyMinLetters);
        Assert.Equal(0.9, VocabularyMatcher.Threshold);
        // "Grube" has 5 letters: exact only.
        var m5 = new VocabularyMatcher(new[] { "Grube" }, Array.Empty<Vocabulary.Replacement>(), isKnownWord: _ => false);
        Assert.Equal("Grub", m5.Apply("Grub"));
        Assert.Equal("Grube", m5.Apply("grube"));
        // A window of 4 letters is never fuzzy-matched to a 6-letter term.
        var m6 = new VocabularyMatcher(new[] { "Claude" }, Array.Empty<Vocabulary.Replacement>(), isKnownWord: _ => false);
        Assert.Equal("Clod", m6.Apply("Clod"));
        Assert.Equal("Claude", m6.Apply("Clawd"));
    }

    [Fact]
    public void FuzzySkipsDigitsAndKnownWords()
    {
        var mm = new VocabularyMatcher(new[] { "Beispilop" }, Array.Empty<Vocabulary.Replacement>(), isKnownWord: w => w == "Beispillop");
        Assert.Equal("Beispillop", mm.Apply("Beispillop"));      // a known word is never corrected
        var md = new VocabularyMatcher(new[] { "Beispilop" }, Array.Empty<Vocabulary.Replacement>(), isKnownWord: _ => false);
        Assert.Equal("Beispil0p", md.Apply("Beispil0p"));        // digits mean something else
        Assert.Equal("Beispilop", md.Apply("Beispillop"));
    }

    [Fact]
    public void FuzzyLengthDifferenceLimit()
    {
        // |len(term) − len(window)| ≤ max(1, len(term)/5): 9 letters → 1 allowed.
        var mm = new VocabularyMatcher(new[] { "Beispilop" }, Array.Empty<Vocabulary.Replacement>(), isKnownWord: _ => false);
        Assert.Equal("Beispilop", mm.Apply("Beispilopp"));
        Assert.Equal("Beispiloppp", mm.Apply("Beispiloppp"));
    }

    [Fact]
    public void ReplacementsAlwaysApplyAndKeepOuterPunctuation()
    {
        var mm = new VocabularyMatcher(Array.Empty<string>(), new[] { new Vocabulary.Replacement("n acht n", "n8n") }, isKnownWord: _ => true);
        Assert.Equal("„n8n,“ sagt er", mm.Apply("„N acht N,“ sagt er"));
    }

    [Fact]
    public void VocabularyJsonDefaultsAndRoundTrip()
    {
        var v = Json.JsonIO.Deserialize<Vocabulary>("{}");
        Assert.Empty(v.Terms);
        Assert.True(v.UseAya);
        Assert.True(v.Fuzzy);
        Assert.Equal(0, v.AyaTermsVersion);
        Assert.Null(v.AyaFetchedAt);
        var text = "{\n  \"ayaFetchedAt\" : \"2026-11-10T08:00:00Z\",\n  \"ayaTerms\" : [\n    \"MUSTER SOLAR\"\n  ],\n  \"ayaTermsVersion\" : 2,\n  \"fuzzy\" : true,\n  \"replacements\" : [\n    {\n      \"spoken\" : \"n acht n\",\n      \"written\" : \"n8n\"\n    }\n  ],\n  \"terms\" : [\n    \"HubSpot\"\n  ],\n  \"useAya\" : true\n}";
        Assert.Equal(text, Json.JsonIO.Serialize(Json.JsonIO.Deserialize<Vocabulary>(text)));
    }

    [Fact]
    public void AyaRefreshRules()
    {
        var now = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
        var v = new Vocabulary { AyaFetchedAt = now.AddHours(-11), AyaTermsVersion = 2 };
        Assert.False(v.NeedsAyaRefresh(now));
        v.AyaFetchedAt = now.AddHours(-12.1);
        Assert.True(v.NeedsAyaRefresh(now));
        v.UseAya = false;
        Assert.False(v.NeedsAyaRefresh(now));
        v.AyaTermsVersion = 1;                     // an old version is rebuilt (as on the Mac, even with names off)
        Assert.True(v.NeedsAyaRefresh(now));
        Assert.False(v.SetAyaTerms(Array.Empty<string>(), now));   // empty result does not overwrite
        Assert.True(v.SetAyaTerms(new[] { "MUSTER SOLAR" }, now));
        Assert.Equal(2, v.AyaTermsVersion);
        Assert.Equal(new[] { "MUSTER SOLAR" }, v.AyaTerms);
    }

    [Fact]
    public void ActiveTermsFollowUseAya()
    {
        var v = new Vocabulary { Terms = { "Claude" }, AyaTerms = { "MUSTER SOLAR" } };
        Assert.Equal(new[] { "Claude", "MUSTER SOLAR" }, v.ActiveTerms);
        v.UseAya = false;
        Assert.Equal(new[] { "Claude" }, v.ActiveTerms);
        Assert.False(v.AddTerm(" Claude "));
        Assert.False(v.AddTerm("  "));
        Assert.True(v.AddTerm("HubSpot"));
    }

    [Fact]
    public void StripsLegalForms()
    {
        Assert.Equal("MUSTER SOLAR", Vocabulary.StripLegalForm("MUSTER SOLAR GmbH"));
        Assert.Equal("Beispilop", Vocabulary.StripLegalForm("Beispilop UG (haftungsbeschränkt)"));
        Assert.Equal("Muster", Vocabulary.StripLegalForm("Muster GmbH & Co. KG"));
        Assert.Equal("", Vocabulary.StripLegalForm("GmbH"));
    }

    [Fact]
    public void AyaTermFilters()
    {
        // letter key ≥ 3, ≤ 4 words, no "@", people's last name only with ≥ 4 characters, projects ≤ 3 words.
        var t = Vocabulary.TermsFromAya(new[] { "AB" }, new[] { "Max Bo", "max@beispiel.de" }, new[] { "Eins Zwei Drei Vier" });
        Assert.Equal(new[] { "Max Bo" }, t);
    }
}

public class SpokenFormsTests
{
    [Fact]
    public void Times()
    {
        Assert.Equal("Treffen um 16:45 bis 17:05 Uhr.", SpokenForms.Times("Treffen um 16 Uhr 45 bis 17 Uhr 5."));
        Assert.Equal("um 10 Uhr 3 Personen", SpokenForms.Times("um 10 Uhr 3 Personen"));
        Assert.Equal("ab 9:30 Uhr geht es los", SpokenForms.Times("ab 9 Uhr 30 geht es los"));
    }

    [Fact]
    public void TimeLimits()
    {
        Assert.Equal("um 24 Uhr 30.", SpokenForms.Times("um 24 Uhr 30."));     // hours 0–23
        Assert.Equal("um 23 Uhr 60.", SpokenForms.Times("um 23 Uhr 60."));     // minutes 0–59
        Assert.Equal("um 0:00 Uhr", SpokenForms.Times("um 0 Uhr 0"));
    }

    [Fact]
    public void EmailAddresses()
    {
        Assert.Equal("schreib an max.mustermann@beispiel.de bitte",
            SpokenForms.EmailAddresses("schreib an max punkt mustermann at beispiel punkt de bitte"));
        Assert.Equal("Meine Adresse ist info@muster-metallbau.com.",
            SpokenForms.EmailAddresses("Meine Adresse ist info at muster minus metallbau punkt com."));
        Assert.Equal("wir sind at home", SpokenForms.EmailAddresses("wir sind at home"));
        Assert.Equal("look at this punkt", SpokenForms.EmailAddresses("look at this punkt"));
    }

    [Fact]
    public void Hesitations()
    {
        Assert.Equal("Also, wir gehen.", SpokenForms.WithoutHesitations("M, also, wir gehen."));
        Assert.Equal("Wir gehen heim.", SpokenForms.WithoutHesitations("Wir, ähm, gehen äh heim."));
        Assert.Equal("Das M ist gut.", SpokenForms.WithoutHesitations("Das M ist gut."));
    }

    [Fact]
    public void ApplyRunsAllInOrder() =>
        Assert.Equal("Mail an max@beispiel.de um 9:30 Uhr.", SpokenForms.Apply("Ähm, mail an max at beispiel punkt de um 9 Uhr 30."));
}

public class InventedTextTests
{
    [Fact]
    public void Credits()
    {
        Assert.True(InventedText.IsCredit("Untertitel im Auftrag des ZDF, 2021."));
        Assert.False(InventedText.IsCredit("Untertitel sind wichtig."));
        Assert.True(InventedText.IsCredit(new[] { new Word("Thanks", 0, 1), new Word("for", 1, 2), new Word("watching!", 2, 3) }));
    }

    [Fact]
    public void DoubtfulOnlyWithoutSpeech()
    {
        Assert.True(InventedText.IsInvented("Thank you.", 0.05));
        Assert.False(InventedText.IsInvented("Thank you.", 0.8));
        Assert.False(InventedText.IsInvented("Danke für die Zusammenfassung", 0.05));
    }

    [Fact]
    public void DoubtfulThreshold()
    {
        Assert.True(InventedText.IsInvented("Okay.", 0.34));
        Assert.False(InventedText.IsInvented("Okay.", 0.35));
        Assert.False(InventedText.IsInvented("...", 0));
        Assert.Equal(0.15, InventedText.MinimumVoicedSeconds);
        Assert.Equal(3.0, InventedText.AccidentalSeconds);
    }

    [Fact]
    public void VoicedRun()
    {
        var s = Enumerable.Repeat(0.001f, 16_000).ToArray();
        Assert.True(InventedText.LongestVoicedRun(s) < 0.05);
        for (int i = 4_000; i < 12_000; i++) s[i] = MathF.Sin(i * 0.2f) * 0.3f;   // 0.5 s tone
        Assert.True(Math.Abs(InventedText.LongestVoicedRun(s) - 0.5) < 0.07);
    }

    [Fact]
    public void VoicedRunBridgesOneQuietFrame()
    {
        var s = new float[16_000];
        void Tone(int a, int b) { for (int i = a; i < b; i++) s[i] = MathF.Sin(i * 0.2f) * 0.3f; }
        Tone(0, 480 * 5);                 // 5 frames
        Tone(480 * 6, 480 * 10);          // one quiet frame, then 4 more
        Assert.Equal(0.30, InventedText.LongestVoicedRun(s), 3);   // 5 + 1 bridged + 4 = 10 frames
        Array.Clear(s);
        Tone(0, 480 * 5);
        Tone(480 * 7, 480 * 10);          // two quiet frames end the run
        Assert.Equal(0.15, InventedText.LongestVoicedRun(s), 3);
    }

    [Fact]
    public void ShortClipHasNoVoice() => Assert.Equal(0, InventedText.LongestVoicedRun(new float[479]));

    [Fact]
    public void TranscriptCleanupDropsCreditsAndAppliesVocabulary()
    {
        var turns = new List<Turn>
        {
            new() { Id = 0, Speaker = "mic-0", Text = "Untertitel im Auftrag des ZDF", Words = { new Word("Untertitel", 0, 1) } },
            new() { Id = 1, Speaker = "mic-0", Text = "wir nutzen hubspot", Words = { new Word("wir", 1, 2), new Word("nutzen", 2, 3), new Word("hubspot", 3, 4) } },
        };
        var v = new Vocabulary { Terms = { "HubSpot" } };
        var output = TranscriptCleanup.Cleaned(turns, v, Known.IsKnown);
        Assert.Single(output);
        Assert.Equal(0, output[0].Id);
        Assert.Equal("wir nutzen HubSpot", output[0].Text);
    }
}

public class InsertionFitTests
{
    [Fact]
    public void SpacesAndCapitals()
    {
        Assert.Equal(" und dann gehen wir.", InsertionFit.Fit("und dann gehen wir.", "Hallo", ""));
        Assert.Equal("Und dann gehen wir.", InsertionFit.Fit("und dann gehen wir.", "Fertig. ", ""));
        Assert.Equal(" und dann", InsertionFit.Fit("Und dann", "Ich gehe heim", ""));
        Assert.Equal(" Meeting", InsertionFit.Fit("Meeting", "Das", ""));       // nouns keep their capital
        Assert.Equal(" Sie wissen", InsertionFit.Fit("Sie wissen", "Wie", ""));
    }

    [Fact]
    public void PeriodWhenSentenceGoesOn()
    {
        Assert.Equal("das Angebot ", InsertionFit.Fit("das Angebot.", "Wir schicken ", "morgen raus"));
        Assert.Equal("usw. ", InsertionFit.Fit("usw.", "Äpfel, Birnen ", "und mehr"));
    }

    [Fact]
    public void EmptyField() => Assert.Equal("Hallo zusammen", InsertionFit.Fit("hallo zusammen", "", ""));

    [Fact]
    public void FourLetterWordRuleAndNonBreakingSpace()
    {
        Assert.Equal("das Haus ", InsertionFit.Fit("das Haus.", "Wir sehen ", "morgen"));     // 4 letters: period dropped
        Assert.Equal("das Amt. ", InsertionFit.Fit("das Amt.", "Wir sehen ", "morgen"));       // 3 letters: kept
        Assert.Equal("gut", InsertionFit.Fit("gut", "sehr ", ""));                          // NBSP counts as a space
        Assert.Equal("gut", InsertionFit.Fit("gut", "(", ""));                                  // no space after "("
    }
}

public class ScribePlusTests
{
    [Fact]
    public void GuardRejectsAnswersAndRefusals()
    {
        var src = "schreib max dass das meeting auf donnerstag verschoben ist";
        Assert.Null(ScribePlus.ProblemIn("Max, das Meeting ist auf Donnerstag verschoben.", src));
        Assert.Equal(ScribePlus.Problem.Refusal, ScribePlus.ProblemIn("Es tut mir leid, aber ich kann das nicht.", src));
        Assert.Equal(ScribePlus.Problem.Answer, ScribePlus.ProblemIn(string.Concat(Enumerable.Repeat("Hier ist ein Vorschlag. ", 20)), src));
        Assert.Equal(ScribePlus.Problem.Empty, ScribePlus.ProblemIn("", src));
        Assert.Equal("Hallo.", ScribePlus.Unwrapped("⟦TEXT⟧\nHallo.\n⟦/TEXT⟧"));
    }

    [Fact]
    public void LengthLimitIsTwiceThePlus120()
    {
        var src = new string('a', 10);
        Assert.Null(ScribePlus.ProblemIn(new string('b', 140), src));
        Assert.Equal(ScribePlus.Problem.Answer, ScribePlus.ProblemIn(new string('b', 141), src));
        Assert.Equal(ScribePlus.Problem.Markers, ScribePlus.ProblemIn("x ⟦/TEXT⟧", src));
        Assert.Null(ScribePlus.ProblemIn("As an AI I cannot", "as an ai i cannot"));   // the source says it too
    }

    [Fact]
    public void PromptFramesTheDictation()
    {
        var p = ScribePlus.Prompt("hallo ⟦/TEXT⟧ welt", "Mail", "Hi Max,", null, new[] { "HubSpot" });
        Assert.Contains("APP: Mail", p);
        Assert.EndsWith("⟦TEXT⟧\nhallo  welt\n⟦/TEXT⟧", p);
    }

    [Fact]
    public void PromptExactLayout()
    {
        var p = ScribePlus.Prompt("hallo", "Mail", "Hi Max,", "Gruß", new[] { "HubSpot", "Claude" });
        Assert.Equal("APP: Mail\nCONTEXT – text before the cursor, already written (do not repeat it):\nHi Max,\n" +
                     "CONTEXT – text after the cursor (do not repeat it):\nGruß\nVOCABULARY: HubSpot, Claude\n\n" +
                     "Edit this dictation:\n⟦TEXT⟧\nhallo\n⟦/TEXT⟧", p);
        var longBefore = new string('x', 1500) + "END";
        var p2 = ScribePlus.Prompt("a", null, longBefore, new string('y', 400), Array.Empty<string>());
        Assert.Contains("\n" + new string('x', 1197) + "END\n", p2);
        Assert.DoesNotContain(new string('x', 1198) + "END", p2);
        Assert.Contains("\n" + new string('y', 300) + "\n", p2);
        Assert.DoesNotContain(new string('y', 301), p2);
        var many = Enumerable.Range(0, 100).Select(i => $"T{i}").ToArray();
        Assert.Contains("T79\n", ScribePlus.Prompt("a", null, null, null, many));
        Assert.DoesNotContain("T80", ScribePlus.Prompt("a", null, null, null, many));
    }

    [Fact]
    public void AppNameAndCursorTextOnlyWithContext()
    {
        var off = ScribePlus.Prompt("hallo", "Mail", "Hi Max,", "Gruß", useContext: false, vocabulary: Array.Empty<string>());
        Assert.DoesNotContain("APP:", off);
        Assert.DoesNotContain("Mail", off);
        Assert.DoesNotContain("CONTEXT", off);
        var on = ScribePlus.Prompt("hallo", "Mail", "Hi Max,", "Gruß", useContext: true, vocabulary: Array.Empty<string>());
        Assert.Contains("APP: Mail", on);
        Assert.Contains("Hi Max,", on);
        Assert.Contains("Gruß", on);
    }

    [Fact]
    public void RelevantTerms()
    {
        var t = ScribePlus.RelevantTerms("wir sprechen mit muster", new[] { "Claude" }, new[] { "Muster Metallbau", "Beispiel" });
        Assert.Equal(new[] { "Claude", "Muster Metallbau" }, t);
    }

    [Fact]
    public void EchoedContext()
    {
        Assert.Equal("wir verschieben.", ScribePlus.WithoutEchoedContext("Hallo Anna,\n\nwir verschieben.", "Hallo Anna,\n"));
        Assert.Equal("Wir verschieben.", ScribePlus.WithoutEchoedContext("Wir verschieben.", "Hallo Anna,"));
    }

    [Fact]
    public void CheckCombinesTheSteps()
    {
        var ok = ScribePlus.Check("⟦TEXT⟧Hallo Anna,\nwir verschieben.⟦/TEXT⟧", "hallo anna wir verschieben", "Hallo Anna,");
        Assert.Equal("wir verschieben.", ok.Text);
        Assert.Null(ok.Problem);
        var bad = ScribePlus.Check("I'm sorry, but I can't do that.", "mach das", null);
        Assert.Equal("mach das", bad.Text);
        Assert.Equal(ScribePlus.Problem.Refusal, bad.Problem);
    }

    [Fact]
    public void FallbackNotes()
    {
        Assert.Equal("Scribe+ failed – pasted word for word", ScribePlus.Note(ScribePlus.Failure.Other));
        Assert.Equal("Scribe+ failed – copied word for word. Paste with Ctrl+V.", ScribePlus.CopiedNote(ScribePlus.Note(ScribePlus.Failure.Other)));
        Assert.Equal(8, ScribePlus.TimeoutSeconds);
    }
}

public class CaseInsideWordTests
{
    [Fact]
    public void CaseInsideWord()
    {
        var m = new VocabularyMatcher(new[] { "HubSpot", "Klein", "SONNEN" }, Array.Empty<Vocabulary.Replacement>(), isKnownWord: _ => true);
        Assert.Equal("Wir nutzen HubSpot.", m.Apply("Wir nutzen Hubspot."));
        Assert.Equal("Das ist klein.", m.Apply("Das ist klein."));
        Assert.Equal("Die Sonnen scheinen.", m.Apply("Die Sonnen scheinen."));
    }
}

public class DictationTextTests
{
    [Fact]
    public void TapsAndAccidentalPresses()
    {
        Assert.True(DictationText.IsTap(0.29));
        Assert.False(DictationText.IsTap(0.3));
        Assert.True(DictationText.IsAccidental(2.9, 0.14));
        Assert.False(DictationText.IsAccidental(3.0, 0.0));
        Assert.False(DictationText.IsAccidental(1, 0.15));
    }

    [Fact]
    public void CleanAppliesVocabularyThenSpokenForms()
    {
        var m = new VocabularyMatcher(new[] { "HubSpot" }, Array.Empty<Vocabulary.Replacement>(), isKnownWord: _ => false);
        Assert.Equal("Wir nutzen HubSpot um 9:30 Uhr.", DictationText.Clean("Ähm, wir nutzen hubspot um 9 Uhr 30.", 1, m));
        Assert.Null(DictationText.Clean("", 1, m));
        Assert.Null(DictationText.Clean("Danke.", 0.1, m));
    }

    [Fact]
    public void PreviewAndTail()
    {
        Assert.Equal("… world again", DictationText.PreviewText("lo world again", truncated: true));
        Assert.Equal("hello", DictationText.PreviewText("hello", truncated: false));
        var tail = DictationText.Tail(string.Join(" ", Enumerable.Repeat("wort", 60)));
        Assert.StartsWith("… wort", tail);
        Assert.True(tail.Length <= 202);
        Assert.Equal(2.5, DictationText.ToastDuration(new string('a', 50)));
        Assert.Equal(4, DictationText.ToastDuration(new string('a', 51)));
    }

    [Fact]
    public void LevelBars()
    {
        Assert.Equal(0, DictationText.LevelFraction(0));
        Assert.Equal(1, DictationText.LevelFraction(1));
        Assert.Equal(0.5, DictationText.LevelFraction((float)Math.Pow(10, -32.5 / 20)), 3);
    }

    [Fact]
    public void LogLine()
    {
        var s = new DictationText.Steps { Mode = "plus", Audio = 3.21, Asr = 0.123, Total = 1.5, Outcome = "ok" };
        Assert.Equal("dictation plus: audio 3.2 s, asr 0.12, cleanup 0.00, field 0.00, claude 0.00, total 1.50 – ok", s.LogLine());
    }
}

public class DictationRecoveryTests
{
    [Fact]
    public void RemembersScribePlusDictations()
    {
        var dir = Path.Combine(Path.GetTempPath(), "recovery-" + Guid.NewGuid().ToString("N"));
        try
        {
            var samples = new float[16_000];
            var now = DateTime.UtcNow;
            Assert.NotNull(DictationRecovery.Save(samples, at: now.AddSeconds(-60), dir: dir));
            Assert.NotNull(DictationRecovery.Save(samples, plus: true, at: now, dir: dir));
            var items = DictationRecovery.Current(now, dir);
            Assert.Equal(new[] { true, false }, items.Select(i => i.Plus));
            Assert.True(Math.Abs((items[0].RecordedAt - now).TotalSeconds) < 0.01);
            Assert.True(Math.Abs(items[1].Seconds - 1) < 0.01);
            Assert.Equal(16_000, DictationRecovery.Load(items[0]).Length);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void PrunesAfter24Hours()
    {
        var dir = Path.Combine(Path.GetTempPath(), "recovery-" + Guid.NewGuid().ToString("N"));
        try
        {
            var now = DateTime.UtcNow;
            var old = DictationRecovery.Save(new float[100], at: now.AddHours(-24.01), dir: dir)!;
            DictationRecovery.Save(new float[100], at: now.AddHours(-23.9), dir: dir);
            Assert.Single(DictationRecovery.Current(now, dir));
            Assert.False(File.Exists(old));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void NamesAreMacCompatible()
    {
        var d = new DateTime(2026, 11, 10, 8, 0, 0, DateTimeKind.Utc);
        Assert.Equal("dictation-1794297600000-plus.caf", DictationRecovery.FileName(d, plus: true));
        Assert.Equal((d, false), DictationRecovery.ParseName("dictation-1794297600000.caf"));
        Assert.Equal((d, true), DictationRecovery.ParseName("dictation-1794297600000-plus.caf"));
    }
}
