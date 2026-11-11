using System.Globalization;
using System.Text.Json.Serialization;
using MeetingAssistant.Core.Json;

namespace MeetingAssistant.Core.Aya;

/// <summary>
/// On-disk cache for Aya data under <c>&lt;AppRoot&gt;/Aya/</c> (spec 02 §8.9): list schemas and the records
/// shown in the pickers. Every Aya call costs API credits, so pickers read this cache and refresh on demand.
/// </summary>
public static class AyaCache
{
    public static string Dir => AppPaths.Aya;
    public static string SchemaFile => Path.Combine(Dir, "schemas.json");
    public static string ListFile(AyaSlot slot) => Path.Combine(Dir, $"list-{slot.Raw()}.json");

    /// <summary><c>list-&lt;slot&gt;.json</c>.</summary>
    public sealed class CachedList
    {
        [JsonPropertyName("ref")] public AyaListRef Ref { get; set; } = new("", "", "", "");
        [JsonPropertyName("records")] public List<AyaRecord> Records { get; set; } = new();
        [JsonPropertyName("fetchedAt")] public DateTime FetchedAt { get; set; }
    }

    /// Slot raw name → schema; empty when there is no cache.
    public static Dictionary<string, AyaListSchema> Schemas() =>
        JsonIO.TryRead<Dictionary<string, AyaListSchema>>(SchemaFile, out var s) && s is not null ? s : new();

    public static AyaListSchema? Schema(AyaSlot slot) => Schemas().TryGetValue(slot.Raw(), out var s) ? s : null;

    public static void SaveSchemas(Dictionary<string, AyaListSchema> s) => JsonIO.Write(s, SchemaFile);

    public static CachedList? List(AyaSlot slot) => JsonIO.TryRead<CachedList>(ListFile(slot), out var l) ? l : null;

    public static void SaveList(AyaSlot slot, CachedList list) => JsonIO.Write(list, ListFile(slot));

    /// Deletes the whole cache folder (Disconnect).
    public static void Clear()
    {
        try { if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true); } catch (IOException) { }
    }
}

/// <summary>
/// Everything about one meeting's context that comes from Aya (spec 02 §8.8). Every part is optional:
/// a meeting can be about a client, a project, both, or neither (an internal meeting).
/// </summary>
public sealed class AyaMeetingContext
{
    public AyaRecord? Client { get; set; }
    public List<AyaRecord> Contacts { get; set; } = new();
    public AyaRecord? Project { get; set; }
    public AyaRecord? Template { get; set; }
    /// The planned meeting in Aya this meeting belongs to.
    public AyaRecord? Meeting { get; set; }
    /// Earlier meetings with the client or in the project, newest first.
    public List<AyaRecord> Recaps { get; set; } = new();
    public AyaListSchema? ClientSchema { get; set; }
    public AyaListSchema? ContactSchema { get; set; }
    public AyaListSchema? ProjectSchema { get; set; }
    /// The meetings list's schema.
    public AyaListSchema? RecapSchema { get; set; }
}

/// <summary>Higher-level Aya operations built on <see cref="AyaClient"/> and the stored <see cref="AyaConfig"/> (spec 02 §8.5–8.8).</summary>
public sealed class AyaService
{
    public AyaConfig Config { get; }
    public AyaClient Client { get; }
    readonly Func<DateTime> _now;

    public AyaService(AyaConfig config, AyaClient client, Func<DateTime>? now = null)
    {
        Config = config;
        Client = client;
        _now = now ?? (() => DateTime.UtcNow);
    }

    /// A service for the stored settings, or null when not configured.
    public static AyaService? Create(AyaConfig config, Platform.ISecretStore? secrets) =>
        config.Client(secrets) is { } c ? new AyaService(config, c) : null;

    /// <summary>
    /// Picks the default list for every slot that is still empty, and moves lists whose record type now
    /// lives in another workspace (spec 02 §8.5 autoSelectLists). Returns the updated config.
    /// </summary>
    public async Task<AyaConfig> AutoSelectListsAsync()
    {
        var cfg = Config.Copy();
        var (workspaces, specs) = await CatalogAsync();
        foreach (var slot in AyaSlots.All)
        {
            var current = cfg.List(slot);
            var tech = current?.TechName ?? slot.DefaultTechName();
            var matching = specs.Where(s => s.TechName == tech).ToList();
            // Same record type name in several workspaces: keep the configured one if it is among them.
            var spec = matching.FirstOrDefault(s => s.WorkspaceId == current?.WorkspaceId) ?? matching.FirstOrDefault();
            var ws = spec is null ? null : workspaces.FirstOrDefault(w => w.Id == spec.WorkspaceId);
            if (spec is null || ws is null) continue;
            if (current is null || current.WorkspaceId != ws.Id)
                cfg.SetList(slot, new AyaListRef(ws.Id, ws.Label, spec.TechName, spec.Label));
        }
        return cfg;
    }

    /// <summary>
    /// All workspaces of the organization and the record types of all of them (without system types),
    /// sorted by label. Each record type appears once, with its home workspace (spec 02 §8.5).
    /// </summary>
    public async Task<(List<AyaWorkspace> Workspaces, List<AyaRecordSpec> Specs)> CatalogAsync()
    {
        var workspaces = await Client.WorkspacesAsync();
        if (workspaces.Count == 0)
            throw new AyaException(AyaErrorKind.NotFound,
                "No workspaces found for this organization id. Check the id: it is part of the Aya URL, app.hey-aya.ai/org/<id>/…");
        var listed = new List<(AyaWorkspace, List<AyaRecordSpec>)>();
        foreach (var ws in workspaces) listed.Add((ws, await Client.RecordSpecsAsync(ws.Id)));
        return (workspaces, MergeSpecs(listed));
    }

    /// <summary>
    /// Merges the record-type listings of several workspaces: a spec without workspaceId gets the
    /// workspace that listed it; each (workspaceId, techName) once; system types removed; sorted by
    /// label, then workspace order.
    /// </summary>
    public static List<AyaRecordSpec> MergeSpecs(IEnumerable<(AyaWorkspace Workspace, List<AyaRecordSpec> Specs)> listed)
    {
        var list = listed.ToList();
        var order = new Dictionary<string, int>();
        for (int i = 0; i < list.Count; i++) order.TryAdd(list[i].Workspace.Id, i);
        var seen = new HashSet<string>();
        var output = new List<AyaRecordSpec>();
        foreach (var (ws, specs) in list)
        {
            foreach (var s in specs.Where(s => s.IsSystem != true))
            {
                var spec = new AyaRecordSpec
                {
                    Id = s.Id, TechName = s.TechName, LocalizedLabel = s.LocalizedLabel, IsSystem = s.IsSystem,
                    WorkspaceId = string.IsNullOrEmpty(s.WorkspaceId) ? ws.Id : s.WorkspaceId,
                };
                if (!seen.Add($"{spec.WorkspaceId}/{spec.TechName}")) continue;
                output.Add(spec);
            }
        }
        var cmp = StringComparer.Create(CultureInfo.CurrentCulture, CompareOptions.IgnoreCase);
        return output
            .Select((s, i) => (s, i))
            .OrderBy(x => x.s.Label, cmp)
            .ThenBy(x => order.TryGetValue(x.s.WorkspaceId ?? "", out var o) ? o : int.MaxValue)
            .ThenBy(x => x.i)
            .Select(x => x.s).ToList();
    }

    /// <summary>
    /// Reads the field schema of every configured list from the workspaces' OpenAPI documents and learns
    /// select options from up to 200 recent records per list, then caches them (spec 02 §8.6).
    /// </summary>
    public async Task<Dictionary<string, AyaListSchema>> RefreshSchemasAsync()
    {
        var output = new Dictionary<string, AyaListSchema>();
        var docs = new Dictionary<string, byte[]>();
        foreach (var slot in AyaSlots.All)
        {
            if (Config.List(slot) is not { } r) continue;
            if (!docs.ContainsKey(r.WorkspaceId))
            {
                try { docs[r.WorkspaceId] = await Client.OpenApiAsync(r.WorkspaceId); }
                catch (AyaException e) when (e.Kind == AyaErrorKind.NotFound)
                {
                    throw new AyaException(AyaErrorKind.NotFound, "API documentation is switched off. Turn it on in the Aya organization settings.");
                }
            }
            var schema = AyaListSchema.Parse(docs[r.WorkspaceId], r.WorkspaceId, r.TechName);
            List<AyaRecord> sample;
            try
            {
                sample = await Client.RecordsAsync(r.WorkspaceId, r.TechName,
                    new AyaQuery { SortField = "system", SortValueKey = "updatedAt", Descending = true, Limit = 200 });
            }
            catch (AyaException) { sample = new(); }
            schema.LearnOptions(sample);
            output[slot.Raw()] = schema;
        }
        AyaCache.SaveSchemas(output);
        return output;
    }

    static readonly HashSet<string> PreviewFields = new()
        { "branche__value", "domain__value", "region__value", "area__value", "status__value", "goal__value" };

    /// <summary>
    /// Reloads one list for the pickers (spec 02 §8.8): all records (max 5000) sorted by title.
    /// Templates are kept whole; other slots only id, title, updatedAt and the preview fields. Cached.
    /// </summary>
    public async Task<List<AyaRecord>> RefreshListAsync(AyaSlot slot)
    {
        var r = Config.List(slot) ?? throw new AyaException(AyaErrorKind.NotConfigured);
        var records = await Client.AllRecordsAsync(r.WorkspaceId, r.TechName, max: 5000);
        var cmp = StringComparer.Create(CultureInfo.CurrentCulture, CompareOptions.IgnoreCase);
        records = records.OrderBy(x => x.Title, cmp).ToList();
        var slim = slot == AyaSlot.Templates ? records
            : records.Select(x => x.Only(k => k is "id" or "computedTitle" or "updatedAt" || PreviewFields.Contains(k))).ToList();
        AyaCache.SaveList(slot, new AyaCache.CachedList { Ref = r, Records = slim, FetchedAt = _now() });
        return slim;
    }

    /// <summary>Full-text search, limit 20 (spec 02 §8.8).</summary>
    public Task<List<AyaRecord>> SearchAsync(AyaSlot slot, string text)
    {
        var r = Config.List(slot) ?? throw new AyaException(AyaErrorKind.NotConfigured);
        return Client.SearchAsync(r.WorkspaceId, r.TechName, text, 20);
    }

    /// The camelCase brick name of a field: from the cached schema, else derived from the key.
    static string Brick(string key, AyaFieldKind kind, AyaSlot slot) =>
        (AyaCache.Schema(slot)?.Field(key) ?? new AyaField(key, kind, true)).BrickTechName;

    /// <summary>
    /// Meetings dated from <paramref name="from"/> (inclusive) to <paramref name="to"/> (exclusive), earliest first
    /// (spec 02 §8.8). Pages until a short page, a page ending at or after <paramref name="to"/>, or <paramref name="max"/>.
    /// </summary>
    public async Task<List<AyaRecord>> MeetingsAsync(DateTime from, DateTime to, int pageSize = 100, int max = 2000)
    {
        var r = Config.List(AyaSlot.Meetings) ?? throw new AyaException(AyaErrorKind.NotConfigured);
        var dateKey = Config.MeetingDateField;
        var field = Brick(dateKey, AyaFieldKind.Datetime, AyaSlot.Meetings);
        var query = new AyaQuery
        {
            Filter = new AyaQuery.Condition(field, AyaText.Iso(from), "value", "greaterThanEqual"),
            SortField = field, Descending = false, Limit = pageSize,
        };
        var toUtc = to.ToUniversalTime();
        var found = new List<AyaRecord>();
        while (found.Count < max)
        {
            var page = await Client.RecordsAsync(r.WorkspaceId, r.TechName, query);
            found.AddRange(page);
            // Sorted by date: once a page ends at or after `to`, later pages hold nothing in range.
            var lastDate = page.Count > 0 ? page[^1].Date(dateKey) : null;
            if (page.Count < pageSize || (lastDate is { } d && d >= toUtc)) break;
            query.Offset += pageSize;
        }
        return found.Where(x => x.Date(dateKey) is { } d && d < toUtc).ToList();
    }

    /// <summary>Planned meetings: from local start of today through the next <paramref name="days"/> days (+1).</summary>
    public Task<List<AyaRecord>> UpcomingMeetingsAsync(int days = 14, DateTime? now = null)
    {
        var start = (now ?? DateTime.Now).ToLocalTime().Date;
        return MeetingsAsync(start.ToUniversalTime(), start.AddDays(days + 1).ToUniversalTime());
    }

    /// <summary>Meetings on the local day of <paramref name="date"/> (recap targets, import).</summary>
    public Task<List<AyaRecord>> MeetingsOnDayOfAsync(DateTime date)
    {
        var start = date.ToLocalTime().Date;
        return MeetingsAsync(start.ToUniversalTime(), start.AddDays(1).ToUniversalTime());
    }

    public Task<AyaRecord> RecordAsync(AyaSlot slot, string id)
    {
        var r = Config.List(slot) ?? throw new AyaException(AyaErrorKind.NotConfigured);
        return Client.RecordAsync(r.WorkspaceId, r.TechName, id);
    }

    /// <summary>
    /// The full context for a meeting (spec 02 §8.8). A planned meeting's own client, project and
    /// template are used unless others are given. Schemas come from the cache.
    /// </summary>
    public async Task<AyaMeetingContext> MeetingContextAsync(string? meetingID = null, string? clientID = null, string? projectID = null,
                                                             string? templateID = null, int recapLimit = 3)
    {
        var schemas = AyaCache.Schemas();
        AyaListSchema? S(AyaSlot s) => schemas.TryGetValue(s.Raw(), out var x) ? x : null;
        var ctx = new AyaMeetingContext
        {
            ClientSchema = S(AyaSlot.Clients), ContactSchema = S(AyaSlot.Contacts),
            ProjectSchema = S(AyaSlot.Projects), RecapSchema = S(AyaSlot.Meetings),
        };
        if (meetingID is not null)
        {
            var m = await RecordAsync(AyaSlot.Meetings, meetingID);
            ctx.Meeting = m;
            clientID ??= Config.MeetingClientField is { } c ? m.LinkedID(c) : null;
            projectID ??= Config.MeetingProjectField is { } p ? m.LinkedID(p) : null;
            templateID ??= Config.MeetingTemplateField is { } t ? m.LinkedID(t) : null;
        }
        if (clientID is not null) ctx.Client = await RecordAsync(AyaSlot.Clients, clientID);
        if (projectID is not null && Config.List(AyaSlot.Projects) is not null)
        {
            try { ctx.Project = await RecordAsync(AyaSlot.Projects, projectID); } catch (AyaException) { }
        }
        if (templateID is not null) ctx.Template = await RecordAsync(AyaSlot.Templates, templateID);

        if (clientID is not null && Config.List(AyaSlot.Contacts) is { } cref && Config.ContactsClientField is { } link)
        {
            var field = S(AyaSlot.Contacts)?.Field(link) ?? new AyaField(link, AyaFieldKind.Relation, true);
            try
            {
                ctx.Contacts = await Client.RecordsAsync(cref.WorkspaceId, cref.TechName,
                    new AyaQuery { Filter = new AyaQuery.Condition(field.BrickTechName, clientID, "id"), Limit = 50 });
            }
            catch (AyaException) { ctx.Contacts = new(); }
        }
        List<AyaRecord> earlier;
        try { earlier = await EarlierMeetingsAsync(ctx.Client, projectID, recapLimit + 1); }
        catch (AyaException) { earlier = new(); }
        ctx.Recaps = earlier.Where(r => r.Id != meetingID).Take(recapLimit).ToList();
        return ctx;
    }

    /// <summary>
    /// Earlier meetings with the client (by the meeting → client link, else by the client's name), or in
    /// the project, newest first; only meetings dated before now (spec 02 §8.8).
    /// </summary>
    public async Task<List<AyaRecord>> EarlierMeetingsAsync(AyaRecord? company, string? projectID, int limit)
    {
        if (Config.List(AyaSlot.Meetings) is not { } r) return new();
        var dateKey = Config.MeetingDateField;
        var dateSort = Brick(dateKey, AyaFieldKind.Datetime, AyaSlot.Meetings);
        var now = _now();
        List<AyaRecord> Past(IEnumerable<AyaRecord> rs) => rs.Where(x => (x.Date(dateKey) ?? DateTime.MinValue) <= now).ToList();
        async Task<List<AyaRecord>> Linked(string link, string id)
        {
            var many = link.EndsWith("__ids", StringComparison.Ordinal);
            var field = Brick(link, many ? AyaFieldKind.Relations : AyaFieldKind.Relation, AyaSlot.Meetings);
            return Past(await Client.RecordsAsync(r.WorkspaceId, r.TechName, new AyaQuery
            {
                Filter = new AyaQuery.Condition(field, id, "id", many ? "containsAny" : "equal"),
                SortField = dateSort, Descending = true, Limit = limit + 3,
            }));
        }
        var output = new List<AyaRecord>();
        if (company is not null && Config.MeetingClientField is { } cl) output = await Linked(cl, company.Id);
        if (output.Count == 0 && projectID is not null && Config.MeetingProjectField is { } pl) output = await Linked(pl, projectID);
        if (output.Count == 0 && company is not null)
        {
            // Meetings from before the client link existed: find them by the client's name.
            var name = SearchName(company.Title);
            if (name.Length >= 3)
            {
                var found = await Client.SearchAsync(r.WorkspaceId, r.TechName, name, 20);
                output = Past(found).OrderByDescending(x => x.Date(dateKey) ?? DateTime.MinValue).ToList();
            }
        }
        return output.Take(limit).ToList();
    }

    static readonly string[] LegalSuffixes =
        { "gmbh & co. kg", "gmbh & co kg", "gmbh", "ag", "kg", "se", "ug", "e.v.", "ltd", "inc", "llc", "co. kg", "mbh" };

    /// <summary>The client name without legal suffixes: "MUSTER SOLAR GmbH &amp; Co. KG" → "MUSTER SOLAR".</summary>
    public static string SearchName(string title)
    {
        var t = title.Trim(' ');
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var s in LegalSuffixes)
            {
                if (t.ToLowerInvariant().EndsWith(" " + s, StringComparison.Ordinal))
                {
                    t = t[..^(s.Length + 1)].Trim(' ');
                    changed = true;
                }
            }
        }
        return t;
    }
}
