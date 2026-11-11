using MeetingAssistant.Core.Assist;
using MeetingAssistant.Core.Aya;
using MeetingAssistant.Core.Json;
using MeetingAssistant.Core.Model;
using MeetingAssistant.Core.Platform;

namespace Msctl.Commands;

/// <summary>
/// msctl aya … (spec 02 §9). The API key comes from $AYA_API_KEY (msctl does not read the app's secure store);
/// the config from the app's preferences file under the app data folder (MEETINGASSISTANT_HOME overrides it).
/// </summary>
public sealed class AyaCommands : ICommandGroup
{
    /// The service for the stored config and $AYA_API_KEY.
    internal static AyaService Service(AyaConfig? cfg = null)
    {
        cfg ??= AyaConfig.Load();
        return AyaService.Create(cfg, new MemorySecretStore()) ?? throw new AyaException(AyaErrorKind.NotConfigured);
    }

    static string Slots => string.Join("|", AyaSlots.All.Select(s => s.Raw()));

    static AyaSlot Slot(string? raw) => raw is not null && AyaSlots.Parse(raw) is { } s ? s : throw new UsageException($"slot: {Slots}");

    public IEnumerable<Command> Commands => new[]
    {
        new Command("aya", "aya setup <orgId> | config | workspaces | specs <ws> | catalog | schema <slot> | list <slot> | search <slot> <text> | upcoming [days] | day <yyyy-MM-dd> | context [--meeting|--client|--project|--template id] — Aya (key from $AYA_API_KEY)",
            Run),
    };

    static async Task<int> Run(string[] args)
    {
        var sub = args.FirstOrDefault() ?? "";
        var rest = args.Skip(1).ToArray();
        switch (sub)
        {
            case "setup":
                {
                    // Stores the org id, picks the default lists and reads their schemas.
                    if (rest.Length == 0) throw new UsageException("usage: msctl aya setup <orgId>");
                    var cfg = AyaConfig.Load();
                    cfg.OrgId = rest[0];
                    cfg.Save();
                    var svc = Service(cfg);
                    cfg = await svc.AutoSelectListsAsync();
                    cfg.Save();
                    foreach (var slot in AyaSlots.All)
                    {
                        var r = cfg.List(slot);
                        Console.WriteLine($"{slot.Title(),-17} {(r is null ? "—" : $"{r.Label} ({r.TechName}) in {r.WorkspaceLabel}")}");
                    }
                    var schemas = await new AyaService(cfg, svc.Client).RefreshSchemasAsync();
                    foreach (var (k, s) in schemas.OrderBy(x => x.Key, StringComparer.Ordinal))
                        Console.WriteLine($"schema {k}: {s.Fields.Count} fields, {s.WritableContent.Count} writable content, relations: {string.Join(", ", s.Relations.Select(f => f.Key))}");
                    return 0;
                }
            case "config":
                Console.WriteLine(JsonIO.Format(System.Text.Json.Nodes.JsonNode.Parse(AyaConfig.Load().ToJson())));
                Console.WriteLine($"api key: {(AyaConfig.ApiKey(null) is null ? "missing" : "set")}");
                return 0;
            case "workspaces":
                foreach (var w in await Service().Client.WorkspacesAsync()) Console.WriteLine($"{w.Id}  {w.Label}");
                return 0;
            case "specs":
                if (rest.Length == 0) throw new UsageException("usage: msctl aya specs <workspaceId>");
                foreach (var s in await Service().Client.RecordSpecsAsync(rest[0])) Console.WriteLine($"{s.TechName}  {s.Label}  home={s.WorkspaceId ?? "-"}");
                return 0;
            case "catalog":
                {
                    var (ws, specs) = await Service().CatalogAsync();
                    foreach (var s in specs) Console.WriteLine($"{s.WorkspaceId}/{s.TechName}  {s.Label} · {ws.FirstOrDefault(w => w.Id == s.WorkspaceId)?.Label}");
                    Console.WriteLine($"({ws.Count} workspaces, {specs.Count} record types)");
                    return 0;
                }
            case "schema":
                {
                    var slot = Slot(rest.FirstOrDefault());
                    var s = AyaCache.Schema(slot) ?? throw new UsageException($"no cached schema for {slot.Raw()} (run aya setup)");
                    foreach (var f in s.Fields)
                    {
                        var opts = f.Options.Count == 0 ? "" : $"  options: {string.Join(", ", f.Options.Take(8))}{(f.Options.Count > 8 ? " …" : "")}";
                        Console.WriteLine($"{(f.Writable ? "w" : "r")} {f.Kind.ToString().ToLowerInvariant(),-9} {f.Key}  [{f.Label}]{opts}");
                    }
                    return 0;
                }
            case "list":
                {
                    var r = await Service().RefreshListAsync(Slot(rest.FirstOrDefault()));
                    foreach (var x in r.Take(40)) Console.WriteLine($"{x.Id}  {x.Title}");
                    Console.WriteLine($"({r.Count} records, cached)");
                    return 0;
                }
            case "search":
                {
                    if (rest.Length < 2) throw new UsageException("usage: msctl aya search <slot> <text>");
                    foreach (var x in await Service().SearchAsync(Slot(rest[0]), string.Join(" ", rest.Skip(1)))) Console.WriteLine($"{x.Id}  {x.Title}");
                    return 0;
                }
            case "upcoming":
                {
                    var svc = Service();
                    var days = int.TryParse(rest.FirstOrDefault(), out var d) ? d : 14;
                    PrintMeetings(svc, await svc.UpcomingMeetingsAsync(days));
                    return 0;
                }
            case "day":
                {
                    var svc = Service();
                    var date = rest.Length > 0 ? DateTime.ParseExact(rest[0], "yyyy-MM-dd", null) : DateTime.Now;
                    PrintMeetings(svc, await svc.MeetingsOnDayOfAsync(date));
                    return 0;
                }
            case "context":
                {
                    var svc = Service();
                    var ctx = await svc.MeetingContextAsync(Args.Option(rest, "--meeting"), Args.Option(rest, "--client"),
                                                            Args.Option(rest, "--project"), Args.Option(rest, "--template"));
                    Console.WriteLine(AssistPrompts.ContextMessage(AssistContext.FromAya(ctx, svc.Config, "", MeetingMode.Call, "Me", DateTime.UtcNow)));
                    return 0;
                }
            default:
                throw new UsageException($"""
                    usage: msctl aya setup <orgId> | config | workspaces | specs <ws> | catalog | schema <slot>
                           msctl aya list <slot> | search <slot> <text> | upcoming [days] | day <yyyy-MM-dd>
                           msctl aya context [--meeting id] [--client id] [--project id] [--template id]
                    slots: {Slots.Replace('|', ' ')} · API key from $AYA_API_KEY
                    """);
        }
    }

    static void PrintMeetings(AyaService svc, List<AyaRecord> meetings)
    {
        foreach (var m in meetings)
        {
            var c = svc.Config;
            var links = string.Join(" · ", new[] { c.MeetingClientField, c.MeetingProjectField, c.MeetingTemplateField }
                .Where(k => k is not null).Select(k => m.LinkedTitle(k!)).Where(t => t is not null));
            var date = m.Date(c.MeetingDateField)?.ToLocalTime().ToString("ddd dd.MM. HH:mm") ?? "?";
            Console.WriteLine($"{m.Id}  {date}  {m.Title}{(links.Length == 0 ? "" : $"  [{links}]")}");
        }
        Console.WriteLine($"({meetings.Count} meetings)");
    }
}
