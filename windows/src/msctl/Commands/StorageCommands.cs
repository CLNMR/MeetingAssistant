using System.Globalization;
using MeetingAssistant.Core;
using MeetingAssistant.Core.Model;
using MeetingAssistant.Core.Storage;
using MeetingAssistant.Core.Text;

namespace Msctl.Commands;

/// Sessions: import, export, trim, split, pauses, list, preferences, kept dictations
/// (spec 04 §8; the Mac's `import`, `export`, `trim`, `split` with the same output).
public sealed class StorageCommands : ICommandGroup
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    static MeetingMode? ParseMode(string s) => s switch
    {
        "call" => MeetingMode.Call,
        "inPerson" => MeetingMode.InPerson,
        "hybrid" => MeetingMode.Hybrid,
        _ => null,
    };

    static double Num(string s) =>
        double.TryParse(s, NumberStyles.Float, Inv, out var d) ? d : throw new UsageException($"not a number: {s}");

    public IEnumerable<Command> Commands => new[]
    {
        new Command("import", "import <mic.wav> [system.wav] [call|inPerson|hybrid] [--speakers N]   -> session id",
            args =>
            {
                var speakers = Args.Option(args, "--speakers");
                var rest = Args.Positional(args, "--speakers");
                var files = rest.Where(a => ParseMode(a) is null && !int.TryParse(a, out _)).ToList();
                if (files.Count == 0) throw new UsageException();
                var mode = rest.Select(ParseMode).FirstOrDefault(m => m is not null) ?? SessionImport.ModeFor(files.Count);
                var info = SessionImport.ImportFiles(files[0], files.Count > 1 ? files[1] : null, mode,
                    expectedSpeakers: speakers is null ? null : int.Parse(speakers, Inv));
                Console.WriteLine(info.Id);
                return Task.FromResult(0);
            }),

        new Command("export", "export <session-id> md|txt|srt|json",
            args =>
            {
                if (args.Length < 2 || Exporter.Parse(args[1]) is not { } f) throw new UsageException();
                var info = SessionStore.Load(args[0]);
                if (SessionStore.LoadTranscript(args[0]) is not { } t) { Console.WriteLine("no transcript"); return Task.FromResult(1); }
                Console.WriteLine(Exporter.Render(t, info, f));
                return Task.FromResult(0);
            }),

        new Command("trim", "trim <session-id> <start> <end>        keep only start…end seconds",
            args =>
            {
                if (args.Length < 3) throw new UsageException();
                var info = SessionEdit.Trim(args[0], Num(args[1]), Num(args[2]));
                Console.WriteLine(string.Format(Inv, "{0} now {1:F1}s", info.Id, info.DurationSec));
                return Task.FromResult(0);
            }),

        new Command("split", "split <session-id> <seconds>|pause [--keep-pause]   split in two (\"pause\" = at the longest suggested pause)",
            args =>
            {
                var rest = Args.Positional(args);
                if (rest.Count < 2) throw new UsageException();
                var id = rest[0];
                SessionInfo second;
                if (rest[1] == "pause")
                {
                    var info = SessionStore.Load(rest[0]);
                    var t = SessionStore.LoadTranscript(id) ?? throw new InvalidOperationException("no transcript");
                    var p = Pauses.LongestSplitSuggestion(Pauses.Find(t, info.DurationSec))
                            ?? throw new InvalidOperationException("no pause suggests a split");
                    second = SessionEdit.Split(id, p, removePause: !Args.Flag(args, "--keep-pause"));
                }
                else
                {
                    second = SessionEdit.Split(id, Num(rest[1]));
                }
                Console.WriteLine(string.Format(Inv, "new {0} ({1:F1}s)", second.Id, second.DurationSec));
                return Task.FromResult(0);
            }),

        new Command("pauses", "pauses <session-id>                    long pauses and split suggestions",
            args =>
            {
                if (args.Length < 1) throw new UsageException();
                var info = SessionStore.Load(args[0]);
                var t = SessionStore.LoadTranscript(args[0]);
                if (t is null) { Console.WriteLine("no transcript"); return Task.FromResult(1); }
                var pauses = Pauses.Find(t, info.DurationSec);
                foreach (var p in pauses)
                    Console.WriteLine(string.Format(Inv, "{0,-8} {1} – {2}  {3}{4}  split at {5:F1}, part 1 until {6:F1}",
                        p.Kind.ToString().ToLowerInvariant(), Exporter.Clock(p.Start), Exporter.Clock(p.End), Pauses.LengthText(p.Duration),
                        p.SuggestsSplit ? "  (suggests a split)" : "", p.SplitPoint, p.KeepUntil));
                if (pauses.Count == 0) Console.WriteLine("no pauses");
                return Task.FromResult(0);
            }),

        new Command("sessions", "sessions                               list recordings (id, status, mode, length, title)",
            _ =>
            {
                foreach (var s in SessionStore.List())
                    Console.WriteLine($"{s.Id}\t{s.Status.Raw()}\t{s.Mode.Raw()}\t{Exporter.Clock(s.DurationSec)}\t{s.Title}");
                return Task.FromResult(0);
            }),

        new Command("session-info", "session-info <session-id>              tracks as stored (container, rate, frames, seconds)",
            args =>
            {
                if (args.Length < 1) throw new UsageException();
                var info = SessionStore.Load(args[0]);
                Console.WriteLine(string.Format(Inv, "{0}  {1}  {2:F3}s  {3}  started {4:yyyy-MM-ddTHH:mm:ssZ}",
                    info.Id, info.Mode.Raw(), info.DurationSec, info.Status.Raw(), info.StartedAt));
                foreach (var tr in info.Tracks)
                {
                    var path = AppPaths.TrackFile(args[0], tr);
                    if (!File.Exists(path)) { Console.WriteLine($"  {tr.Raw()}: missing"); continue; }
                    var a = AudioContainer.Probe(path);
                    Console.WriteLine(string.Format(Inv, "  {0}: {1} {2} Hz {3} ch {4}-bit{5} frames={6} seconds={7:F3}",
                        tr.Raw(), Path.GetFileName(path), a.SampleRate, a.Channels, a.Bits, a.IsFloat ? " float" : "", a.Frames, a.Seconds));
                }
                Console.WriteLine($"  transcript: {(SessionStore.LoadTranscript(args[0]) is { } t ? $"{t.Turns.Count} turns, {t.Speakers.Count} speakers" : "none")}");
                Console.WriteLine($"  assist.json: {(SessionStore.HasAssistRecord(args[0]) ? "yes" : "no")}");
                return Task.FromResult(0);
            }),

        new Command("prefs", "prefs                                  show the preferences (preferences.v1) with defaults filled in",
            _ =>
            {
                Console.WriteLine(MeetingAssistant.Core.Json.JsonIO.Format(Preferences.Load().ToJson()));
                return Task.FromResult(0);
            }),

        new Command("dictations", "dictations                             kept (unfinished) dictations; older than 24 h are removed",
            _ =>
            {
                var items = DictationRecovery.Current();
                foreach (var i in items)
                    Console.WriteLine($"{i.Id}\t{DictationRecovery.Label(i)}\t{(i.Plus ? "plus" : "plain")}");
                if (items.Count == 0) Console.WriteLine("none");
                return Task.FromResult(0);
            }),

        new Command("dictation-keep", "dictation-keep <audio> [--plus]        save audio as a kept dictation (to test Unfinished Dictations)",
            args =>
            {
                var rest = Args.Positional(args);
                if (rest.Count < 1) throw new UsageException();
                var path = DictationRecovery.Save(MeetingAssistant.Core.Audio.AudioFile.Read(rest[0]), plus: Args.Flag(args, "--plus"));
                Console.WriteLine(path ?? "error: could not save");
                return Task.FromResult(path is null ? 1 : 0);
            }),
    };
}
