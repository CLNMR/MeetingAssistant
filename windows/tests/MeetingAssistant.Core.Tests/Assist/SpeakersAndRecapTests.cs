using System.Text.Json.Nodes;
using MeetingAssistant.Core.Assist;
using MeetingAssistant.Core.Aya;
using MeetingAssistant.Core.Claude;
using MeetingAssistant.Core.Model;
using Xunit;
using static MeetingAssistant.Core.Tests.Assist.AssistTestData;

namespace MeetingAssistant.Core.Tests.Assist;

// Port of SpeakersTests.swift and DeadlineCheckTests (TextTests.swift), plus recap/CRM result reading.

public class SpeakersTests
{
    static Turn T(int id, string sp, double s, double e, string text) => new() { Id = id, Speaker = sp, Start = s, End = e, Text = text, Track = Track.System };
    static Speaker Sp(string id, string label) => new() { Id = id, Label = label, Track = Track.System };

    [Fact]
    public void Generic()
    {
        Assert.True(SpeakerNames.IsGeneric("Speaker 3"));
        Assert.True(SpeakerNames.IsGeneric("Them (?)"));
        Assert.True(SpeakerNames.IsGeneric(" ? "));
        Assert.True(SpeakerNames.IsGeneric("…"));
        Assert.False(SpeakerNames.IsGeneric("Speaker Muster"));
        Assert.False(SpeakerNames.IsGeneric("Erika Musterfrau"));
    }

    [Fact]
    public void CarriesLiveNamesToFinalSpeakers()
    {
        var t = new Transcript { Turns = { T(0, "a", 0, 10, "Guten Tag"), T(1, "b", 10, 20, "Hallo"), T(2, "a", 20, 30, "Weiter geht es") }, Speakers = { Sp("a", "Speaker 1"), Sp("b", "Speaker 2") } };
        var lines = new[]
        {
            new AssistLine(1, "Erika Musterfrau", "Guten Tag zusammen wie geht es"), new AssistLine(11, "Speaker 2", "Hallo hallo ich bin da"),
            new AssistLine(21, "Erika Musterfrau", "Weiter geht es mit Punkt zwei"),
        };
        // Speaker 2 was matched to an attendee by Claude.
        var att = new List<Attendee> { new() { Name = "Max Beispiel", Speaker = "Speaker 2", Present = true } };
        var names = SpeakerNames.CarryOver(t, lines, att);
        Assert.Equal(new Dictionary<string, string> { ["a"] = "Erika Musterfrau", ["b"] = "Max Beispiel" }, names);
    }

    [Fact]
    public void NoNameWhenUnclear()
    {
        var t = new Transcript { Turns = { T(0, "a", 0, 20, "x") }, Speakers = { Sp("a", "Speaker 1") } };
        var lines = new[] { new AssistLine(1, "Erika", "eins zwei drei vier"), new AssistLine(10, "Max", "eins zwei drei vier") };
        Assert.Empty(SpeakerNames.CarryOver(t, lines, new List<Attendee>()));
    }

    [Fact]
    public void NeedsFourVotesAndLeavesNamedSpeakers()
    {
        var t = new Transcript { Turns = { T(0, "a", 0, 5, "x"), T(1, "b", 10, 15, "y") }, Speakers = { Sp("a", "Speaker 1"), Sp("b", "Lena") } };
        Assert.Empty(SpeakerNames.CarryOver(t, new[] { new AssistLine(1, "Erika", "eins zwei drei") }, new List<Attendee>()));
        Assert.Equal("Erika", SpeakerNames.CarryOver(t, new[] { new AssistLine(5.5, "Erika", "eins zwei drei vier"), new AssistLine(11, "Max", "a b c d e") }, new List<Attendee>())["a"]);
        Assert.Single(SpeakerNames.CarryOver(t, new[] { new AssistLine(5.5, "Erika", "eins zwei drei vier"), new AssistLine(11, "Max", "a b c d e") }, new List<Attendee>()));
    }

    [Fact]
    public void ReplacesWholeLabelsOnly() =>
        Assert.Equal("Erika und Speaker 12 sprachen.", SpeakerNames.Replacing("Speaker 1", "Erika", "Speaker 1 und Speaker 12 sprachen."));

    [Fact]
    public void ImportantPersonFields()
    {
        var d = new List<LabeledValue>
        {
            new("utm", "Kampagne", "x"), new("email__value", "E-Mail", "erika@example.com"),
            new("position__value", "Position", "Geschäftsführerin"), new("telefon__value", "Telefon", "+49 1"),
        };
        Assert.Equal(new[] { "Position", "E-Mail", "Telefon" }, PersonInfo.Important(d).Select(x => x.Label));
        Assert.Equal(300, PersonInfo.Important(new[] { new LabeledValue("notiz__value", "Notiz", new string('a', 400)) })[0].Value.Length);
    }

    [Fact]
    public void NameMatching()
    {
        var people = new[] { "Erika Musterfrau", "Max Beispiel", "Max Muster" };
        Assert.Equal("Erika Musterfrau", PersonMatch.Match("erika musterfrau", people, x => x));
        Assert.Equal("Erika Musterfrau", PersonMatch.Match("Érika", people, x => x));
        Assert.Null(PersonMatch.Match("Max", people, x => x));                // two Maxes
        Assert.Equal("Max Beispiel", PersonMatch.Match("Max Bei", people, x => x));
        Assert.Null(PersonMatch.Match("Speaker 2", people, x => x));
    }
}

public class DeadlineCheckTests
{
    [Fact]
    public void RemovesUnsaidDeadlines()
    {
        var md = "- Angebot schicken (bis Freitag)\n- Termin abstimmen (Max)\n- Vertrag prüfen – bis Ende des Monats.\n- Demo (bis 23.11.)";
        var r = DeadlineCheck.Filter(md, "Wir schicken das Angebot. Max stimmt den Termin ab. Vertrag prüfen wir.");
        Assert.Equal("- Angebot schicken\n- Termin abstimmen (Max)\n- Vertrag prüfen.\n- Demo (bis 23.11.)", r.Text);
        Assert.Equal(new[] { "bis Freitag", "bis Ende des Monats" }, r.Removed);
    }

    [Fact]
    public void KeepsSaidDeadlinesAcrossLanguages()
    {
        var md = "- Send the offer (by Friday)\n- Call back tomorrow – by next week";
        var r = DeadlineCheck.Filter(md, "Das Angebot kommt freitags, und nächste Woche rufen wir an.");
        Assert.Empty(r.Removed);
        Assert.Equal(md, r.Text);
    }

    [Fact]
    public void PrefixWordsNeedFiveLetters()
    {
        Assert.Equal("fri", DeadlineCheck.PrefixCanonical("freitagabend"));
        Assert.Equal("weekend", DeadlineCheck.PrefixCanonical("wochenenden"));     // the longest time word wins
        Assert.Null(DeadlineCheck.PrefixCanonical("mai"));
        Assert.Null(DeadlineCheck.PrefixCanonical("maien"));
    }

    [Fact]
    public void RecapDraftReportsRemovedDeadlines()
    {
        var fields = new List<AyaField> { new("next_steps__value", AyaFieldKind.Markdown, true), new("done__value", AyaFieldKind.Boolean, true), new("count__value", AyaFieldKind.Number, true) };
        var structured = JsonNode.Parse("""{"next_steps__value":"- Angebot schicken (bis Freitag)","done__value":true,"count__value":null}""");
        var d = RecapBuilder.ReadRecap(structured, fields, "Wir schicken ein Angebot.", null);
        Assert.Equal("- Angebot schicken", d[0].Value);
        Assert.Equal("Removed deadlines not said in the meeting: “bis Freitag”", d[0].Reason);
        Assert.Equal("yes", d[1].Value);
        Assert.True(d[0].Include && d[1].Include);
        Assert.True(d[2].Value == "" && !d[2].Include);
        Assert.Throws<ClaudeException>(() => RecapBuilder.ReadRecap(null, fields, "", null));
    }
}

public class CrmResultTests
{
    [Fact]
    public void KeepsRealChangesAndGroupsContacts()
    {
        var cs = new AyaListSchema("W", "firma", new[]
        {
            new AyaField("branche__value", AyaFieldKind.Text, true), new AyaField("size__value", AyaFieldKind.Select, true, new[] { "klein", "groß" }),
        });
        var ps = new AyaListSchema("W", "kontakt", new[]
        {
            new AyaField("first_name__value", AyaFieldKind.Text, true), new AyaField("last_name__value", AyaFieldKind.Text, true),
            new AyaField("position__value", AyaFieldKind.Text, true),
        });
        var client = Rec(("id", "C1"), ("computedTitle", "MUSTER SOLAR"), ("branche__value", "Solar"));
        var contact = Rec(("id", "k1"), ("computedTitle", "Erika Musterfrau"), ("position__value", "GF"));
        var answer = JsonNode.Parse("""
            {"client_changes":[{"field":"branche__value","new_value":"Solar","reason":"same"},{"field":"branche__value","new_value":"Solar und Speicher","reason":"said"},
                               {"field":"size__value","new_value":"riesig","reason":"bad option"},{"field":"nope","new_value":"x","reason":""}],
             "contact_changes":[{"contact_id":"k1","field":"position__value","new_value":"Geschäftsführerin","reason":"r"},{"contact_id":"zz","field":"position__value","new_value":"x","reason":"r"}],
             "new_contacts":[{"first_name__value":"Max","last_name__value":"Beispiel","position__value":null},{"first_name__value":null,"last_name__value":"","position__value":null}]}
            """);
        var p = RecapBuilder.ReadCrm(answer, client, new[] { contact }, cs, ps);
        var change = Assert.Single(p.Client);
        Assert.True(change.Value == "Solar und Speicher" && change.Old == "Solar" && !change.Include && change.Reason == "said");
        Assert.Equal(2, p.Contacts.Count);
        Assert.True(p.Contacts[0].ContactID == "k1" && p.Contacts[0].Name == "Erika Musterfrau" && !p.Contacts[0].Include);
        var nc = p.Contacts[1];
        Assert.True(nc.ContactID is null && nc.Name == "Max Beispiel" && nc.Id == "new-Max Beispiel" && !nc.Include);
        Assert.All(nc.Changes, c => Assert.True(c.Include));
        Assert.Equal(new[] { "first_name__value", "last_name__value", "position__value" }, RecapBuilder.NewContactFields(ps).Select(f => f.Key));
    }
}
