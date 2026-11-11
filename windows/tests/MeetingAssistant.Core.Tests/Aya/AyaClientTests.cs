using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using MeetingAssistant.Core.Aya;
using MeetingAssistant.Core.Json;
using MeetingAssistant.Core.Tests.Assist;
using Xunit;

namespace MeetingAssistant.Core.Tests.Aya;

/// A transport that records requests and answers from a script.
sealed class FakeHandler : HttpMessageHandler
{
    public readonly List<(HttpMethod Method, Uri Url, string? Auth, string? Body, string? ContentType)> Requests = new();
    readonly Func<HttpRequestMessage, int, HttpResponseMessage> _answer;
    public FakeHandler(Func<HttpRequestMessage, int, HttpResponseMessage> answer) { _answer = answer; }

    public static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
    {
        var body = r.Content is null ? null : await r.Content.ReadAsStringAsync(ct);
        Requests.Add((r.Method, r.RequestUri!, r.Headers.Authorization?.ToString(), body, r.Content?.Headers.ContentType?.MediaType));
        return _answer(r, Requests.Count);
    }
}

/// Request building, errors and retries of the Aya client (spec 02 §8.1–8.3).
public class AyaClientTests
{
    static (AyaClient, FakeHandler, List<TimeSpan>) Make(Func<HttpRequestMessage, int, HttpResponseMessage> answer, string org = "ORG", string key = "KEY")
    {
        var h = new FakeHandler(answer);
        var sleeps = new List<TimeSpan>();
        return (new AyaClient(org, key, handler: h, delay: t => { sleeps.Add(t); return Task.CompletedTask; }), h, sleeps);
    }

    [Fact]
    public async Task BuildsRequestsWithAuthAndQuery()
    {
        var (c, h, _) = Make((_, _) => FakeHandler.Json("[]"));
        await c.WorkspacesAsync();
        await c.RecordsAsync("WS", "firma", new AyaQuery { Filter = new("firma", "a+b c", "id"), Limit = 50 });
        await c.SearchAsync("WS", "firma", "MUSTER+SOLAR", 20);
        Assert.Equal("https://api.hey-aya.ai/api/workspaces?orgId=ORG", h.Requests[0].Url.AbsoluteUri);
        Assert.Equal("Bearer KEY", h.Requests[0].Auth);
        var q = h.Requests[1].Url.AbsoluteUri;
        Assert.StartsWith("https://api.hey-aya.ai/api/workspaces/WS/records/firma?query=", q);
        Assert.Contains("a%2Bb%20c", q);                                                  // "+" is encoded
        Assert.Equal(JsonIO.Compact(new AyaQuery { Filter = new("firma", "a+b c", "id"), Limit = 50 }.Json()),
                     Uri.UnescapeDataString(q[(q.IndexOf("query=") + 6)..]));
        Assert.Equal("https://api.hey-aya.ai/api/workspaces/WS/records/firma/search?q=MUSTER%2BSOLAR&limit=20", h.Requests[2].Url.AbsoluteUri);
        Assert.All(h.Requests, r => Assert.Null(r.Body));
    }

    [Fact]
    public async Task WritesSendValuesAndOpenApiHasNoAuth()
    {
        var (c, h, _) = Make((r, _) => r.RequestUri!.AbsolutePath.EndsWith("openapi.json") ? FakeHandler.Json("{}") : FakeHandler.Json("""{"id":"R1","computedTitle":" TEST "}"""));
        var created = await c.CreateAsync("WS", "pmMeetings", new Dictionary<string, JsonNode?> { ["title__value"] = "TEST", ["project__id"] = null });
        Assert.Equal("R1", created.Id);
        Assert.Equal("TEST", created.Title);
        await c.UpdateAsync("WS", "pmMeetings", "R1", new Dictionary<string, JsonNode?> { ["done__value"] = true });
        await c.DeleteAsync("WS", "pmMeetings", "R1");
        await c.OpenApiAsync("WS");
        Assert.Equal((HttpMethod.Post, """{"values":{"project__id":null,"title__value":"TEST"}}""", "application/json"),
                     (h.Requests[0].Method, h.Requests[0].Body, h.Requests[0].ContentType));
        Assert.Equal((HttpMethod.Put, "https://api.hey-aya.ai/api/workspaces/WS/records/pmMeetings/R1"), (h.Requests[1].Method, h.Requests[1].Url.AbsoluteUri));
        Assert.Equal(HttpMethod.Delete, h.Requests[2].Method);
        Assert.Null(h.Requests[3].Auth);
    }

    [Fact]
    public async Task NotConfiguredWithoutKeyOrOrg()
    {
        var (c, h, _) = Make((_, _) => FakeHandler.Json("[]"), key: " ");
        Assert.Equal(AyaErrorKind.NotConfigured, (await Assert.ThrowsAsync<AyaException>(() => c.WorkspacesAsync())).Kind);
        Assert.Empty(h.Requests);
    }

    [Theory]
    [InlineData(402, "{}", AyaErrorKind.CreditsExhausted)]
    [InlineData(429, """{"code":"limit_exceeded"}""", AyaErrorKind.CreditsExhausted)]
    [InlineData(401, "{}", AyaErrorKind.Unauthorized)]
    [InlineData(403, """{"error":"no"}""", AyaErrorKind.Forbidden)]
    [InlineData(404, """{"error":"gone"}""", AyaErrorKind.NotFound)]
    [InlineData(400, """{"error":"bad"}""", AyaErrorKind.BadRequest)]
    [InlineData(422, "plain text", AyaErrorKind.BadRequest)]
    [InlineData(500, "{}", AyaErrorKind.Server)]
    public async Task StatusCodesMapToErrors(int code, string body, AyaErrorKind kind)
    {
        var (c, h, _) = Make((_, _) => FakeHandler.Json(body, (HttpStatusCode)code));
        var e = await Assert.ThrowsAsync<AyaException>(() => c.MeAsync());
        Assert.Equal(kind, e.Kind);
        Assert.Single(h.Requests);
        if (code == 404) Assert.Equal("Not found in Aya (workspace, list or record). gone", e.Message);
        if (code == 422) Assert.Equal("Aya rejected the request: plain text", e.Message);
        if (code == 401) Assert.Equal("Aya rejected the API key. Check it in Settings › Assist.", e.Message);
    }

    [Fact]
    public async Task RetriesRateLimitsAndGatewayErrors()
    {
        var (c, h, sleeps) = Make((_, n) =>
        {
            var r = FakeHandler.Json("{}", HttpStatusCode.TooManyRequests);
            if (n == 1) r.Headers.Add("Retry-After", "45");
            return r;
        });
        var e = await Assert.ThrowsAsync<AyaException>(() => c.MeAsync());
        Assert.Equal((AyaErrorKind.Server, 429), (e.Kind, e.StatusCode));
        Assert.Equal(4, h.Requests.Count);
        Assert.Equal(new[] { 30.0, 4, 6 }, sleeps.Select(s => s.TotalSeconds));       // Retry-After capped at 30, then attempt×2

        var (c2, h2, sleeps2) = Make((_, n) => n < 3 ? FakeHandler.Json("{}", HttpStatusCode.BadGateway) : FakeHandler.Json("""{"userId":"U"}"""));
        Assert.Equal("U", await c2.MeAsync());
        Assert.Equal(new[] { 1.0, 2 }, sleeps2.Select(s => s.TotalSeconds));

        var (c3, h3, sleeps3) = Make((_, _) => throw new HttpRequestException("offline"));
        Assert.Equal(AyaErrorKind.Network, (await Assert.ThrowsAsync<AyaException>(() => c3.MeAsync())).Kind);
        Assert.Equal(3, h3.Requests.Count);
        Assert.Equal(new[] { 1.0, 2 }, sleeps3.Select(s => s.TotalSeconds));
    }

    [Fact]
    public async Task PagesThroughAllRecords()
    {
        var (c, h, _) = Make((r, n) => FakeHandler.Json("[" + string.Join(",", Enumerable.Range(0, n < 3 ? 200 : 7).Select(i => $$"""{"id":"{{n}}-{{i}}"}""")) + "]"));
        var all = await c.AllRecordsAsync("WS", "firma");
        Assert.Equal(407, all.Count);
        Assert.Equal(3, h.Requests.Count);
        Assert.Contains("%22offset%22%3A400", h.Requests[2].Url.AbsoluteUri);
        var capped = await Make((_, _) => FakeHandler.Json("[" + string.Join(",", Enumerable.Range(0, 200).Select(i => "{}")) + "]")).Item1.AllRecordsAsync("WS", "x", max: 400);
        Assert.Equal(400, capped.Count);
    }

    [Fact]
    public async Task BadJsonIsADecodingError()
    {
        var (c, _, _) = Make((_, _) => FakeHandler.Json("{not json"));
        Assert.Equal(AyaErrorKind.Decoding, (await Assert.ThrowsAsync<AyaException>(() => c.WorkspacesAsync())).Kind);
    }
}

/// Records, texts, coercion and config helpers (spec 02 §8.2, §8.4, §8.7, §8.10).
public class AyaModelTests
{
    [Fact]
    public void RecordHelpers()
    {
        var r = AssistTestData.Rec(("id", "R"), ("computedTitle", "  "), ("a__id", "X"), ("b__ids", new[] { "Y", "Z" }),
                                   ("a__computed_title", "Ax"), ("b__computed_titles", new[] { "Yy", "Zz" }), ("d__value", "2026-11-10"));
        Assert.Equal("R", r.Title);
        Assert.Equal("X", r.LinkedID("a__id"));
        Assert.Equal("Y", r.LinkedID("b__ids"));
        Assert.Equal(new[] { "Y", "Z" }, r.LinkedIDs("b__ids"));
        Assert.Equal("Ax", r.LinkedTitle("a__id"));
        Assert.Equal("Yy, Zz", r.LinkedTitle("b__ids"));
        Assert.Equal(new DateTime(2026, 11, 10).ToUniversalTime(), r.Date("d__value"));
        Assert.Equal(new DateTime(2026, 11, 10, 8, 0, 0, 123, DateTimeKind.Utc), AyaText.ParseDate("2026-11-10T09:00:00.123+01:00"));
        Assert.Null(AyaText.ParseDate("2026-11-10"));
    }

    [Fact]
    public void LocalizedLabels()
    {
        AyaText.LanguageOverride = "fr";
        try
        {
            Assert.Equal("E", AyaText.Localized(new Dictionary<string, string> { ["de"] = "D", ["en"] = "E" }));
            Assert.Equal("D", AyaText.Localized(new Dictionary<string, string> { ["de"] = "D", ["it"] = "I" }));
            Assert.Equal("A", AyaText.Localized(new Dictionary<string, string> { ["it"] = "I", ["es"] = "A" }));
            Assert.Equal("slug", new AyaWorkspace { Id = "id", Slug = "slug" }.Label);
        }
        finally { AyaText.LanguageOverride = null; }
    }

    [Fact]
    public void HumanizeAndBrickNames()
    {
        Assert.Equal("Next steps", AyaField.Humanize("next_steps"));
        Assert.Equal("Ma zahl", AyaField.Humanize("maZahl"));
        Assert.Equal("meetingDate", new AyaField("meeting_date__value", AyaFieldKind.Datetime, true).BrickTechName);
    }

    [Fact]
    public void KindInference()
    {
        JsonNode P(string json) => JsonNode.Parse(json)!;
        Assert.Equal(AyaFieldKind.Embedded, AyaListSchema.KindOf("x__id", P("""{"description":"Embedded record"}""")));
        Assert.Equal(AyaFieldKind.Files, AyaListSchema.KindOf("x__file_count", P("{}")));
        Assert.Equal(AyaFieldKind.Email, AyaListSchema.KindOf("x__value", P("""{"description":"Email address"}""")));
        Assert.Equal(AyaFieldKind.Url, AyaListSchema.KindOf("x__value", P("""{"description":"A link"}""")));
        Assert.Equal(AyaFieldKind.Date, AyaListSchema.KindOf("x__value", P("""{"description":"ISO date"}""")));
        Assert.Equal(AyaFieldKind.Number, AyaListSchema.KindOf("x__value", P("""{"anyOf":[{"type":"integer"}]}""")));
        Assert.Equal(AyaFieldKind.Text, AyaListSchema.KindOf("x__value", P("""{"type":"string"}""")));
        Assert.Equal(AyaFieldKind.Other, AyaListSchema.KindOf("x__value", P("{}")));
    }

    [Fact]
    public void Coercion()
    {
        var s = new AyaListSchema("W", "t", new[]
        {
            new AyaField("b", AyaFieldKind.Boolean, true), new AyaField("d", AyaFieldKind.Date, true), new AyaField("r", AyaFieldKind.Relation, true),
            new AyaField("rs", AyaFieldKind.Relations, true), new AyaField("t", AyaFieldKind.Markdown, true), new AyaField("f", AyaFieldKind.Files, true),
            new AyaField("s", AyaFieldKind.Select, true, new[] { "a" }),
        });
        Assert.False(s.TryCoerce((JsonNode?)null, "b", out _));                       // null → nil for boolean
        Assert.True(s.TryCoerce((JsonNode?)null, "t", out var v) && v is null);       // … JSON null otherwise
        Assert.True(s.TryCoerce("NEIN", "b", out v) && v.AsBool() == false);
        Assert.False(s.TryCoerce("vielleicht", "b", out _));
        Assert.True(s.TryCoerce("", "d", out v) && v is null);
        Assert.True(s.TryCoerce("23.11.2026", "d", out v) && v.AsString() == "23.11.2026");   // 10 characters
        Assert.False(s.TryCoerce("morgen", "d", out _));
        Assert.True(s.TryCoerce("", "r", out v) && v is null);
        Assert.False(s.TryCoerce("X", "rs", out _));
        Assert.True(s.TryCoerce(new JsonArray("X"), "rs", out v) && v is JsonArray);
        Assert.True(s.TryCoerce("  text \n", "t", out v) && v.AsString() == "text");
        Assert.False(s.TryCoerce("x", "f", out _));
        Assert.True(s.TryCoerce("", "s", out v) && v is null);
    }

    [Fact]
    public void WebUrls()
    {
        var c = new AyaConfig { OrgId = "o g" };
        Assert.Equal("https://app.hey-aya.ai/org/o%20g", c.OrgUrl);
        Assert.Equal(c.OrgUrl, c.LogoUrl);
        c.SetList(AyaSlot.Meetings, new AyaListRef("W", "PM", "pmMeetings", "Meetings"));
        Assert.Equal("https://app.hey-aya.ai/workspace/W/recordlist/pmMeetings/record/R", c.WebUrl(AyaSlot.Meetings, "R"));
        Assert.Equal("https://app.hey-aya.ai/workspace/W/recordlist/pmMeetings", c.LogoUrl);
    }

    [Fact]
    public void CompleteNeedsTheRequiredLists()
    {
        var c = new AyaConfig { OrgId = "o" };
        foreach (var s in new[] { AyaSlot.Clients, AyaSlot.Contacts, AyaSlot.Templates }) c.SetList(s, new AyaListRef("W", "", s.DefaultTechName(), ""));
        Assert.False(c.IsComplete);
        c.SetList(AyaSlot.Meetings, new AyaListRef("W", "", "pmMeetings", ""));
        Assert.True(c.IsComplete);
        Assert.False(AyaSlot.Projects.IsRequired() || AyaSlot.Members.IsRequired());
    }
}

/// AyaService over a fake transport and a temporary cache.
[Collection("AppRoot")]
public class AyaServiceTests
{
    static AyaConfig Config()
    {
        var c = new AyaConfig { OrgId = "ORG" };
        foreach (var s in AyaSlots.All) c.SetList(s, new AyaListRef("W", "WS", s.DefaultTechName(), s.Title()));
        return c;
    }

    [Fact]
    public async Task AutoSelectFollowsTheHomeWorkspace()
    {
        using var home = new AssistTestData.TempHome();
        var (svc, _) = Service(new AyaConfig { OrgId = "ORG" }, r => r.RequestUri!.AbsolutePath switch
        {
            "/api/workspaces" => """[{"id":"WA","slug":"a"},{"id":"WB","slug":"b"}]""",
            "/api/workspaces/WA/record-specs" => """[{"id":"1","techName":"firma","workspaceId":"WB"},{"id":"2","techName":"kontakt"}]""",
            _ => """[{"id":"3","techName":"pmMeetings","workspaceId":"WB"}]""",
        });
        var cfg = await svc.AutoSelectListsAsync();
        Assert.Equal("WB", cfg.List(AyaSlot.Clients)?.WorkspaceId);
        Assert.Equal("WA", cfg.List(AyaSlot.Contacts)?.WorkspaceId);
        Assert.Equal("pmMeetings", cfg.List(AyaSlot.Meetings)?.TechName);
        Assert.Null(cfg.List(AyaSlot.Templates));
    }

    static (AyaService, FakeHandler) Service(AyaConfig cfg, Func<HttpRequestMessage, string> answer, DateTime? now = null)
    {
        var h = new FakeHandler((r, _) => FakeHandler.Json(answer(r)));
        return (new AyaService(cfg, new AyaClient("ORG", "KEY", handler: h, delay: _ => Task.CompletedTask), now is null ? null : () => now.Value), h);
    }

    static string QueryOf(HttpRequestMessage r) => Uri.UnescapeDataString(r.RequestUri!.Query);

    [Fact]
    public async Task MeetingsByDateStopPagingAtTheEnd()
    {
        using var home = new AssistTestData.TempHome();
        var from = new DateTime(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc);
        var pages = 0;
        var (svc, h) = Service(Config(), r =>
        {
            pages++;
            var q = JsonNode.Parse(QueryOf(r)[(QueryOf(r).IndexOf("query=") + 6)..])!;
            Assert.Equal("meetingDate", q["filter"]!["brickTechName"]!.GetValue<string>());
            Assert.Equal("greaterThanEqual", q["filter"]!["comparator"]!.GetValue<string>());
            Assert.Equal("2026-11-10T00:00:00Z", q["filter"]!["value"]!.GetValue<string>());
            var day = pages == 1 ? 10 : 12;
            return "[" + string.Join(",", Enumerable.Range(0, 2).Select(i => $$"""{"id":"m{{pages}}{{i}}","meeting_date__value":"2026-11-{{day + i}}T09:00:00Z"}""")) + "]";
        });
        var found = await svc.MeetingsAsync(from, from.AddDays(2), pageSize: 2);
        Assert.Equal(2, h.Requests.Count);               // the second page ends after `to`
        Assert.Equal(new[] { "m10", "m11" }, found.Select(m => m.Id));
    }

    [Fact]
    public async Task MeetingContextFollowsThePlannedMeetingsLinks()
    {
        using var home = new AssistTestData.TempHome();
        var now = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
        var (svc, h) = Service(Config(), r =>
        {
            var p = r.RequestUri!.AbsolutePath;
            if (p.EndsWith("/pmMeetings/M1")) return """{"id":"M1","customer__id":"C1","load_template__id":"T1","meeting_date__value":"2026-11-10T09:00:00Z"}""";
            if (p.EndsWith("/firma/C1")) return """{"id":"C1","computedTitle":"MUSTER SOLAR GmbH"}""";
            if (p.EndsWith("/pmMeetingTemplates/T1")) return """{"id":"T1","computedTitle":"Erstgespräch","agenda__value":"- a"}""";
            if (p.EndsWith("/kontakt")) return """[{"id":"K1","computedTitle":"Erika Musterfrau"}]""";
            if (p.EndsWith("/pmMeetings"))
                return """[{"id":"M1","meeting_date__value":"2026-11-10T09:00:00Z"},{"id":"M0","meeting_date__value":"2026-11-01T09:00:00Z"},{"id":"M9","meeting_date__value":"2026-12-01T09:00:00Z"}]""";
            return "{}";
        }, now);
        var ctx = await svc.MeetingContextAsync(meetingID: "M1");
        Assert.Equal("C1", ctx.Client?.Id);
        Assert.Equal("T1", ctx.Template?.Id);
        Assert.Null(ctx.Project);
        Assert.Equal("K1", Assert.Single(ctx.Contacts).Id);
        Assert.Equal("M0", Assert.Single(ctx.Recaps).Id);            // the current one and future ones are left out
        var contactQuery = h.Requests.Single(r => r.Url.AbsolutePath.EndsWith("/kontakt")).Url;
        Assert.Contains("\"brickTechName\":\"firma\",\"brickValueKey\":\"id\",\"comparator\":\"equal\"", Uri.UnescapeDataString(contactQuery.Query));
        var assist = MeetingAssistant.Core.Assist.AssistContext.FromAya(ctx, svc.Config, "", MeetingAssistant.Core.Model.MeetingMode.Call, "Me", now);
        Assert.Equal("M1", assist.MeetingID);
        Assert.Equal(new[] { "P1" }, assist.PlaybookItems.Select(i => i.Id));
    }

    [Fact]
    public async Task EarlierMeetingsFallBackToTheClientName()
    {
        using var home = new AssistTestData.TempHome();
        var now = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
        var (svc, h) = Service(Config(), r => r.RequestUri!.AbsolutePath.EndsWith("/search")
            ? """[{"id":"A","meeting_date__value":"2026-10-01T09:00:00Z"},{"id":"B","meeting_date__value":"2026-11-01T09:00:00Z"},{"id":"F","meeting_date__value":"2027-01-01T09:00:00Z"}]"""
            : "[]", now);
        var client = AssistTestData.Rec(("id", "C1"), ("computedTitle", "MUSTER SOLAR GmbH & Co. KG"));
        var found = await svc.EarlierMeetingsAsync(client, "P1", 3);
        Assert.Equal(new[] { "B", "A" }, found.Select(m => m.Id));
        Assert.Equal("MUSTER SOLAR", Uri.UnescapeDataString(h.Requests.Last().Url.Query).Split("q=")[1].Split('&')[0]);
        Assert.Equal(3, h.Requests.Count);       // client link, project link, then name search
    }

    [Fact]
    public async Task RefreshListKeepsOnlyPreviewFieldsAndCaches()
    {
        using var home = new AssistTestData.TempHome();
        var (svc, _) = Service(Config(), _ => """[{"id":"2","computedTitle":"beta","branche__value":"x","secret__value":"y"},{"id":"1","computedTitle":"Alpha"}]""");
        var list = await svc.RefreshListAsync(AyaSlot.Clients);
        Assert.Equal(new[] { "1", "2" }, list.Select(r => r.Id));
        Assert.False(list[1].Has("secret__value"));
        Assert.True(list[1].Has("branche__value"));
        Assert.Equal(2, AyaCache.List(AyaSlot.Clients)?.Records.Count);
        var templates = await svc.RefreshListAsync(AyaSlot.Templates);
        Assert.True(templates[1].Has("secret__value"));                 // templates are kept whole
        AyaCache.Clear();
        Assert.Null(AyaCache.List(AyaSlot.Clients));
    }

    [Fact]
    public async Task RefreshSchemasLearnsOptionsAndReportsMissingDocs()
    {
        using var home = new AssistTestData.TempHome();
        var cfg = new AyaConfig { OrgId = "ORG" };
        cfg.SetList(AyaSlot.Meetings, new AyaListRef("W", "WS", "pmMeetings", "Meetings"));
        var (svc, h) = Service(cfg, r => r.RequestUri!.AbsolutePath.EndsWith("openapi.json") ? AyaSchemaTests.OpenApi
            : """[{"meeting_type__value":"internal"},{"meeting_type__value":"customer"}]""");
        var schemas = await svc.RefreshSchemasAsync();
        Assert.Equal(new[] { "customer", "internal" }, schemas["meetings"].Field("meeting_type__value")!.Options);
        Assert.Equal("customer", AyaCache.Schema(AyaSlot.Meetings)!.Field("meeting_type__value")!.Options[0]);
        Assert.Contains("%22brickValueKey%22%3A%22updatedAt%22", h.Requests[1].Url.AbsoluteUri);

        var h404 = new FakeHandler((_, _) => FakeHandler.Json("{}", HttpStatusCode.NotFound));
        var svc404 = new AyaService(cfg, new AyaClient("ORG", "KEY", handler: h404, delay: _ => Task.CompletedTask));
        var e = await Assert.ThrowsAsync<AyaException>(() => svc404.RefreshSchemasAsync());
        Assert.Equal("Not found in Aya (workspace, list or record). API documentation is switched off. Turn it on in the Aya organization settings.", e.Message);
    }
}
