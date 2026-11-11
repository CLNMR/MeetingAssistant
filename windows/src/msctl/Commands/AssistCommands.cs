using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using MeetingAssistant.Core;
using MeetingAssistant.Core.Assist;
using MeetingAssistant.Core.Aya;
using MeetingAssistant.Core.Claude;
using MeetingAssistant.Core.Json;
using MeetingAssistant.Core.Model;

namespace Msctl.Commands;

/// <summary>Prints what changed between two assist states (for replays).</summary>
sealed class StatePrinter
{
    AssistState _last = new();
    AssistStatus _lastStatus = new AssistStatus.Idle();
    readonly Stopwatch _t0 = Stopwatch.StartNew();
    readonly object _lock = new();

    public void Show(AssistState s, AssistStatus status)
    {
        lock (_lock)
        {
            var ts = $"{_t0.Elapsed.TotalSeconds,6:F1}s";
            if (status != _lastStatus) { Console.WriteLine($"{ts} · {status.Text}"); _lastStatus = status; }
            if (!s.Questions.SequenceEqual(_last.Questions) && s.Questions.Count > 0)
            {
                var q = s.Questions[0];
                Console.WriteLine($"{ts} ? {q.Text}{(q.Why is { } w ? $"   ({w})" : "")}");
                foreach (var alt in s.Questions.Skip(1)) Console.WriteLine($"          · {alt.Text}");
            }
            for (int i = 0; i < s.Summary.Count; i++)
            {
                if (i >= _last.Summary.Count) Console.WriteLine($"{ts} + [{i}] {s.Summary[i]}");
                else if (s.Summary[i] != _last.Summary[i]) Console.WriteLine($"{ts} ~ [{i}] {s.Summary[i]}");
            }
            foreach (var p in s.OpenPoints.Where(p => !_last.OpenPoints.Contains(p))) Console.WriteLine($"{ts} O {p.Id} [{AssistPrompts.Raw(p.Status)}] {p.Text}");
            foreach (var a in s.Attendees.Where(a => !_last.Attendees.Contains(a)))
                Console.WriteLine($"{ts} @ {a.Name}{(a.Role is { } r ? $", {r}" : "")}{(a.Speaker is { } sp ? $" = {sp}" : "")}{(a.ContactID is { } c ? $" [Aya {c}]" : "")}");
            foreach (var i in s.Playbook.Where(i => !_last.Playbook.Contains(i)))
                Console.WriteLine($"{ts} P {i.Id} {AssistPrompts.Raw(i.Status)}{(i.Evidence is { } e ? $" — {e}" : "")}");
            foreach (var t in s.Tables ?? new())
            {
                var before = _last.Tables?.FirstOrDefault(x => x.Id == t.Id);
                foreach (var r in t.Rows)
                {
                    var old = before?.Rows.FirstOrDefault(x => x.Id == r.Id);
                    if (!t.IsFilled(r) || (old is not null && old.Cells.SequenceEqual(r.Cells))) continue;
                    Console.WriteLine($"{ts} T {r.Id} {t.Label(r)} → " + string.Join(" · ", t.AnswerColumns.Select(c => r.Cells[c]).Where(c => c.Length > 0)));
                }
            }
            _last = s;
        }
    }
}

/// <summary>msctl assist … (spec 02 §9): Claude check, transcript replay through the engine, tables, recap, CRM, assist.json checks.</summary>
public sealed class AssistCommands : ICommandGroup
{
    /// The claude binary: $CLAUDE_PATH first, then the usual places.
    internal static string ClaudeBinary() =>
        ClaudeCli.Locate(Environment.GetEnvironmentVariable("CLAUDE_PATH")) ?? throw ClaudeException.NotInstalled();

    public IEnumerable<Command> Commands => new[]
    {
        new Command("assist", "assist check | oneshot <prompt> | replay <session> [--speed N] [--minutes M] [--model m] [--effort e] [--goal g] [--me name] [--save file] [--meeting|--client|--project|--template id] | tables|recap|crm <session> [--record file] [--model m] [--upload-test] | roundtrip <assist.json…> | aya-cache-roundtrip <dir>",
            Run),
    };

    /// Context for a session: assist.json unless Aya ids are given.
    static async Task<AssistContext> ReplayContext(string[] args, string sid, MeetingMode mode)
    {
        var ids = new[] { "--meeting", "--client", "--project", "--template" }.Select(f => Args.Option(args, f)).ToArray();
        if (AssistRecord.Load(sid) is { } rec && ids.All(x => x is null)) return rec.Context;
        var svc = AyaCommands.Service();
        var aya = await svc.MeetingContextAsync(ids[0], ids[1], ids[2], ids[3]);
        return AssistContext.FromAya(aya, svc.Config, Args.Option(args, "--goal") ?? "", mode, Args.Option(args, "--me") ?? "Me", DateTime.UtcNow);
    }

    static AssistRecord RecordFor(string[] rest, string sid)
    {
        if (Args.Option(rest, "--record") is { } f) return JsonIO.Read<AssistRecord>(f);
        return AssistRecord.Load(sid) ?? throw new UsageException("no assist.json for this session; pass --record <file> from `replay --save`");
    }

    static string Clip(string s, int n) => AssistPrompts.Clip(s, n);

    static async Task<int> Run(string[] args)
    {
        var sub = args.FirstOrDefault() ?? "";
        var rest = args.Skip(1).ToArray();
        switch (sub)
        {
            case "check":
                {
                    var b = ClaudeBinary();
                    Console.WriteLine($"claude: {b}");
                    if (await ClaudeCli.AuthStatusAsync(b) is { } s)
                        Console.WriteLine($"logged in: {s.LoggedIn} · {s.Method ?? "?"} · plan: {s.Subscription ?? "?"}");
                    else Console.WriteLine("could not read `claude auth status`");
                    return 0;
                }
            case "oneshot":
                {
                    // A one-shot call through the same code as the recap (isolation, stdin, JSON output).
                    var prompt = string.Join(" ", Args.Positional(rest, "--model", "--schema"));
                    JsonNode? schema = Args.Option(rest, "--schema") is { } sc ? JsonNode.Parse(sc) : null;
                    var sw = Stopwatch.StartNew();
                    var r = await ClaudeCli.OneShotAsync(ClaudeBinary(), "Answer briefly.", prompt, Args.Option(rest, "--model") ?? "haiku", "low", schema, 120);
                    Console.WriteLine(r.Text);
                    if (r.Structured is not null) Console.WriteLine("structured: " + JsonIO.Compact(r.Structured));
                    Console.WriteLine($"({sw.Elapsed.TotalSeconds:F1}s, API-equivalent ${r.CostUsd:F4})");
                    return 0;
                }
            case "replay":
                {
                    var sid = Args.Positional(rest, "--speed", "--minutes", "--model", "--effort", "--goal", "--me", "--save",
                                              "--meeting", "--client", "--project", "--template").FirstOrDefault()
                              ?? throw new UsageException("usage: msctl assist replay <session-id> [--speed N] [--minutes M] [--model m] [--save file]");
                    var info = JsonIO.Read<SessionInfo>(AppPaths.Info(sid));
                    var transcript = JsonIO.Read<Transcript>(AppPaths.TranscriptFile(sid));
                    var speed = double.Parse(Args.Option(rest, "--speed") ?? "1", CultureInfo.InvariantCulture);
                    var limit = double.Parse(Args.Option(rest, "--minutes") ?? "0", CultureInfo.InvariantCulture) * 60;
                    var context = await ReplayContext(rest, sid, info.Mode);
                    var settings = new AssistSettings();
                    if (Args.Option(rest, "--model") is { } m) settings.Model = m;
                    if (Args.Option(rest, "--effort") is { } e) settings.Effort = e;
                    settings.MinInterval /= speed; settings.MaxInterval /= speed;
                    var printer = new StatePrinter();
                    var engine = new AssistEngine(context, settings, ClaudeBinary(), printer.Show);
                    Console.WriteLine($"replaying {info.Title} ({(int)(info.DurationSec / 60)} min) at {speed}× with {settings.Model}/{settings.Effort}");
                    Console.WriteLine($"context: {context.Title} · {context.Contacts.Count} contacts · template {context.PlaybookTitle ?? "—"} ({context.PlaybookItems.Count} items, {context.TemplateTables?.Count ?? 0} tables) · {context.Recaps.Count} recaps");
                    await engine.StartAsync();
                    var me = info.Mode == MeetingMode.Call ? transcript.Speakers.FirstOrDefault(s => s.IsMe)?.Id : null;
                    var start = Stopwatch.StartNew();
                    foreach (var turn in transcript.Turns)
                    {
                        if (limit > 0 && turn.Start > limit) break;
                        var wait = turn.End / speed - start.Elapsed.TotalSeconds;
                        if (wait > 0) await Task.Delay(TimeSpan.FromSeconds(wait));
                        var name = turn.Speaker == me ? "Me" : transcript.Label(turn.Speaker);
                        await engine.IngestLinesAsync(new[] { new AssistLine(turn.Start, name, turn.Text) });
                    }
                    // Let the last update finish.
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(settings.MaxInterval, 5)));
                    for (int i = 0; i < 60 && engine.Status is AssistStatus.Thinking; i++) await Task.Delay(1000);
                    var final = await engine.StopAsync();
                    var lat = await engine.GetLatenciesAsync();
                    var firsts = lat.Where(l => l.FirstOp is not null).Select(l => l.FirstOp!.Value).OrderBy(x => x).ToList();
                    var totals = lat.Select(l => l.Total).OrderBy(x => x).ToList();
                    static string Med(List<double> a) => a.Count == 0 ? "-" : $"{a[a.Count / 2]:F1}s";
                    Console.WriteLine($"\nupdates: {final.Updates} · median first op {Med(firsts)} · median total {Med(totals)} · API-equivalent cost ${final.CostUSD:F3}");
                    if (Args.Option(rest, "--save") is { } output)
                    {
                        new AssistRecord(context, final, await engine.GetTranscriptAsync()).Let(r => JsonIO.Write(r, output));
                        Console.WriteLine($"saved {output}");
                    }
                    return 0;
                }
            case "tables":
                {
                    var sid = Args.Positional(rest, "--record", "--model").FirstOrDefault() ?? throw new UsageException("usage: msctl assist tables <session-id> [--record file]");
                    var rec = RecordFor(rest, sid);
                    var start = rec.Tables ?? rec.State.Tables ?? rec.Context.TemplateTables ?? new();
                    if (start.Count == 0) throw new UsageException("the meeting's template has no tables");
                    var sw = Stopwatch.StartNew();
                    var done = await RecapBuilder.CompleteTablesAsync(ClaudeBinary(), Args.Option(rest, "--model") ?? "opus", rec, sid, start);
                    Console.WriteLine($"tables completed in {sw.Elapsed.TotalSeconds:F1}s");
                    foreach (var (before, t) in start.Zip(done))
                    {
                        Console.WriteLine($"{t.Id} {t.Section}: {before.FilledCount} live → {t.FilledCount}/{t.Rows.Count}");
                        foreach (var r in t.Rows.Where(t.IsFilled))
                        {
                            var old = before.Rows.FirstOrDefault(x => x.Id == r.Id);
                            var mark = old is null || !before.IsFilled(old) ? "+" : !old.Cells.SequenceEqual(r.Cells) ? "~" : " ";
                            Console.WriteLine($"  {mark} {r.Id} {t.Label(r)} → " + string.Join(" | ", t.AnswerColumns.Select(c => r.Cells[c])));
                        }
                    }
                    if (TemplateParser.Block(rec.Context.PlaybookTitle ?? "Template", done) is { } block) Console.WriteLine("\n--- Markdown for Aya ---\n" + block);
                    return 0;
                }
            case "recap":
            case "crm":
                {
                    var sid = Args.Positional(rest, "--record", "--model").FirstOrDefault() ?? throw new UsageException("usage: msctl assist recap|crm <session-id> [--record file]");
                    var rec = RecordFor(rest, sid);
                    var cfg = AyaConfig.Load();
                    var model = Args.Option(rest, "--model") ?? "opus";
                    if (sub == "recap")
                    {
                        var schema = AyaCache.Schema(AyaSlot.Meetings) ?? throw new AyaException(AyaErrorKind.NotConfigured);
                        var sw = Stopwatch.StartNew();
                        var fields = await RecapBuilder.DraftRecapAsync(ClaudeBinary(), model, rec, sid, schema, cfg);
                        Console.WriteLine($"recap draft in {sw.Elapsed.TotalSeconds:F1}s");
                        foreach (var f in fields)
                            Console.WriteLine($"── {f.Key} [{f.Kind.ToString().ToLowerInvariant()}]{(f.Include ? "" : " (empty)")}{(f.Reason is { } r ? $" ({r})" : "")}\n{f.Value}\n");
                        if (Args.Flag(rest, "--upload-test"))
                        {
                            // Creates the recap with a TEST title, prints it, and deletes it again.
                            var svc = AyaCommands.Service(cfg);
                            var info = JsonIO.Read<SessionInfo>(AppPaths.Info(sid));
                            var values = RecapBuilder.RecapValues(fields, rec, sid, info.StartedAt, schema, cfg, tables: rec.Tables ?? rec.State.Tables ?? new());
                            values[cfg.MeetingTitleField] = "TEST " + (values.TryGetValue(cfg.MeetingTitleField, out var t) ? t.Describe() : "");
                            var r0 = cfg.List(AyaSlot.Meetings)!;
                            var created = await svc.Client.CreateAsync(r0.WorkspaceId, r0.TechName, values);
                            Console.WriteLine($"created {created.Id}: {created.Title}");
                            foreach (var k in values.Keys.OrderBy(k => k, StringComparer.Ordinal))
                                Console.WriteLine($"  {k} = {Clip(created.Has(k) ? created[k].Describe() : "∅", 120)}");
                            await svc.Client.DeleteAsync(r0.WorkspaceId, r0.TechName, created.Id);
                            Console.WriteLine("deleted again");
                        }
                    }
                    else
                    {
                        var svc = AyaCommands.Service(cfg);
                        var cs = AyaCache.Schema(AyaSlot.Clients) ?? throw new AyaException(AyaErrorKind.NotConfigured);
                        var clientID = rec.Context.ClientID ?? throw new UsageException("this meeting has no client");
                        var client = await svc.RecordAsync(AyaSlot.Clients, clientID);
                        var contacts = new List<AyaRecord>();
                        if (cfg.List(AyaSlot.Contacts) is not null)
                            foreach (var c in rec.Context.Contacts)
                            {
                                try { contacts.Add(await svc.RecordAsync(AyaSlot.Contacts, c.Id)); } catch (AyaException) { }
                            }
                        var sw = Stopwatch.StartNew();
                        var p = await RecapBuilder.ProposeCrmAsync(ClaudeBinary(), model, rec, sid, client, contacts, cs, AyaCache.Schema(AyaSlot.Contacts));
                        Console.WriteLine($"CRM proposal in {sw.Elapsed.TotalSeconds:F1}s");
                        foreach (var c in p.Client) Console.WriteLine($"client {c.Key}: {Clip(c.Old, 80)} → {Clip(c.Value, 200)}\n   because: {c.Reason}");
                        foreach (var c in p.Contacts)
                        {
                            Console.WriteLine($"{(c.ContactID is null ? "NEW contact" : "contact")} {c.Name}:");
                            foreach (var ch in c.Changes) Console.WriteLine($"   {ch.Key}: {Clip(ch.Old, 60)} → {Clip(ch.Value, 160)}");
                        }
                    }
                    return 0;
                }
            case "roundtrip":
                {
                    // Reads each assist.json with the model types and checks that writing it gives the same bytes.
                    if (rest.Length == 0) throw new UsageException("usage: msctl assist roundtrip <assist.json…>");
                    int same = 0, diff = 0;
                    foreach (var f in rest)
                    {
                        var text = File.ReadAllText(f);
                        var again = JsonIO.Serialize(JsonIO.Deserialize<AssistRecord>(text));
                        if (again == text) same++; else { diff++; Console.WriteLine($"differs: {f}"); }
                    }
                    Console.WriteLine($"{same} identical, {diff} different");
                    return diff == 0 ? 0 : 1;
                }
            case "aya-cache-roundtrip":
                {
                    // Reads an Aya cache folder (schemas.json, list-<slot>.json) and checks byte-identical round trips.
                    var dir = rest.FirstOrDefault() ?? AyaCache.Dir;
                    int same = 0, diff = 0;
                    void Check(string path, Func<string, string> roundtrip)
                    {
                        if (!File.Exists(path)) return;
                        var text = File.ReadAllText(path);
                        if (roundtrip(text) == text) same++; else { diff++; Console.WriteLine($"differs: {Path.GetFileName(path)}"); }
                    }
                    Check(Path.Combine(dir, "schemas.json"), t => JsonIO.Serialize(JsonIO.Deserialize<Dictionary<string, AyaListSchema>>(t)));
                    foreach (var slot in AyaSlots.All)
                        Check(Path.Combine(dir, $"list-{slot.Raw()}.json"), t => JsonIO.Serialize(JsonIO.Deserialize<AyaCache.CachedList>(t)));
                    Console.WriteLine($"{same} identical, {diff} different");
                    return diff == 0 ? 0 : 1;
                }
            default:
                throw new UsageException("""
                    usage: msctl assist check
                           msctl assist oneshot <prompt> [--model haiku] [--schema json]
                           msctl assist replay <session-id> [--meeting|--client|--project|--template <id>] [--speed N] [--minutes M] [--model opus|sonnet|haiku] [--effort e] [--goal g] [--me name] [--save file]
                           msctl assist tables <session-id> [--record file]
                           msctl assist recap <session-id> [--record file] [--upload-test]
                           msctl assist crm <session-id> [--record file]
                           msctl assist roundtrip <assist.json…>
                           msctl assist aya-cache-roundtrip [dir]
                    """);
        }
    }
}

static class LetExtension
{
    public static void Let<T>(this T value, Action<T> f) => f(value);
}
