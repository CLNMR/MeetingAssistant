using MeetingAssistant.Core.Json;
using MeetingAssistant.Core.Model;
using Xunit;

namespace MeetingAssistant.Core.Tests.Model;

public class JsonFormatTests
{
    [Fact]
    public void SessionInfoMatchesMacFormat()
    {
        var s = new SessionInfo
        {
            Id = "2026-11-10_14-37-28", Title = "MUSTER SOLAR – Erstgespräch Testlauf",
            StartedAt = new DateTime(2026, 11, 10, 13, 37, 28, DateTimeKind.Utc), DurationSec = 12.5,
            Mode = MeetingMode.InPerson, Tracks = { Track.Mic }, Status = SessionStatus.Done,
        };
        var expected = "{\n  \"durationSec\" : 12.5,\n  \"id\" : \"2026-11-10_14-37-28\",\n  \"mode\" : \"inPerson\",\n  \"startedAt\" : \"2026-11-10T13:37:28Z\",\n  \"status\" : \"done\",\n  \"title\" : \"MUSTER SOLAR – Erstgespräch Testlauf\",\n  \"tracks\" : [\n    \"mic\"\n  ]\n}";
        Assert.Equal(expected, JsonIO.Serialize(s));
        var back = JsonIO.Deserialize<SessionInfo>(expected);
        Assert.Equal(MeetingMode.InPerson, back.Mode);
        Assert.Null(back.ExpectedSpeakers);
        Assert.Equal(DateTimeKind.Utc, back.StartedAt.Kind);
    }

    [Fact]
    public void EmptyArraysAndExponentsLikeSwift()
    {
        var t = new Transcript { Engine = "parakeet-ultra", CreatedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc) };
        Assert.Contains("\"turns\" : [\n\n  ]", JsonIO.Serialize(t));
        Assert.Equal("-4.335016e-05", JsonIO.FormatNumber(-4.335016e-05));
        Assert.Equal("3", JsonIO.FormatNumber(3.0));
    }

    [Fact]
    public void ReadsFractionalSecondsLeniently()
    {
        var d = IsoDateConverter.Parse("2026-11-10T13:03:22.123Z");
        Assert.Equal("2026-11-10T13:03:22Z", IsoDateConverter.ToText(d));
    }

    [Fact]
    public void MarkMeRenamesPreviousMe()
    {
        var t = new Transcript
        {
            Speakers =
            {
                new Speaker { Id = "mic-0", Label = "Erika Musterfrau", IsMe = true },
                new Speaker { Id = "mic-1", Label = "Speaker 1" },
            },
        };
        t.MarkMe("mic-1", "Erika Musterfrau");
        Assert.Equal("Speaker 2", t.Speakers[0].Label);
        Assert.True(t.Speakers[1].IsMe);
    }
}
