using System.Diagnostics;
using MeetingAssistant.Core;
using MeetingAssistant.Core.Audio;
using MeetingAssistant.Core.Json;
using MeetingAssistant.Core.Model;
using MeetingAssistant.Core.Pipeline;
using MeetingAssistant.Speech;

namespace Msctl.Commands;

/// Speech commands (spec 04 §8): models, asr, transcribe, livetest, enroll, speakers.
public sealed class SpeechCommands : ICommandGroup
{
    public IEnumerable<Command> Commands => new[]
    {
        new Command("models", "models [status|download] — speech model folders and their download", Models),
        new Command("transcribe", "transcribe <session-id> [qwen|parakeet] [auto|german|english] [--quiet] [--timing] [--cluster-threshold X] — final pipeline with progress, prints the transcript and processing time", Transcribe),
        new Command("livetest", "livetest <mic.wav> [system.wav] [call|inPerson|hybrid] [--speed N] [--from S] [--seconds S] [--quiet] [--save file] — live transcriber on files (default 4x: 100 ms every 25 ms)", LiveTest),
        new Command("enroll", "enroll <voice.wav> [name] — record the voiceprint from a file (default name Me)", Enroll),
        new Command("speakers", "speakers [list|forget <name>|voiceprint|delete-voiceprint] — remembered voices and the voiceprint", Speakers),
        new Command("asr", "asr parakeet [auto|german|english] <files...> — raw Parakeet text per file (<file>\\t<text>), RTFx on stderr", Asr),
    };

    static async Task<int> Models(string[] args)
    {
        var sub = args.FirstOrDefault() ?? "status";
        if (sub == "download")
        {
            string last = "";
            await ModelCatalog.DownloadAllAsync((p, msg) =>
            {
                var line = $"  [{p * 100,3:F0}%] {msg}";
                if (line != last) { last = line; Console.WriteLine(line); }
            });
        }
        foreach (var (m, _, _) in ModelCatalog.Steps)
            Console.WriteLine($"{m.Folder}\t{(m.IsReady ? "ready" : "missing")}\t{m.Directory}");
        Console.WriteLine(ModelCatalog.CoreModelsReady() ? "models ready" : "models missing (msctl models download)");
        return 0;
    }

    static Task<int> Asr(string[] args)
    {
        var files = args.Where(a => !a.StartsWith("--") && a is not ("parakeet" or "qwen" or "auto" or "german" or "english")).ToList();
        if (files.Count == 0) throw new UsageException();
        if (args.Contains("qwen")) Console.Error.WriteLine("note: Qwen3-ASR is not available on Windows; using Parakeet");
        var engine = SpeechEngine.Shared;
        double dur = 0;
        var sw = Stopwatch.StartNew();
        foreach (var f in files)
        {
            var s = AudioFile.Read(f);
            dur += s.Length / 16_000.0;
            var words = engine.Transcribe(s, 0);
            Console.WriteLine($"{Path.GetFileName(f)}\t{string.Join(" ", words.Select(w => w.W))}");
            if (args.Contains("--debug"))
            {
                var r = engine.Recognize(s);
                Console.WriteLine(string.Join("|", r.Tokens.Select((t, i) => $"{t}@{r.Timestamps.ElementAtOrDefault(i):F2}/{r.Durations?.ElementAtOrDefault(i):F2}")));
                foreach (var w in words) Console.WriteLine($"  {w.Start:F2}-{w.End:F2} {w.W}");
            }
        }
        Console.Error.WriteLine($"RTFx {dur / sw.Elapsed.TotalSeconds:F1}");
        return Task.FromResult(0);
    }

    /// Optional transcript cleanup (credits + vocabulary) supplied by Core/Text once wired.
    public static ITranscriptTextCleanup? Cleanup { get; set; }

    static Task<int> Transcribe(string[] args)
    {
        var pos = Args.Positional(args, "--cluster-threshold");
        if (pos.Count == 0) throw new UsageException();
        var id = pos[0];
        var settings = new PipelineSettings();
        if (pos.Contains("qwen")) settings = settings with { Engine = AsrEngine.Qwen3Large };
        if (pos.Contains("parakeet")) settings = settings with { Engine = AsrEngine.ParakeetUltra };
        if (pos.Skip(1).Select(AsrEngineText.ParseLanguage).FirstOrDefault(l => l is not null) is { } lang)
            settings = settings with { Language = lang };
        if (settings.Engine == AsrEngine.Qwen3Large) Console.Error.WriteLine("note: Qwen3-ASR is not available on Windows; using Parakeet");
        string last = "";
        var clock = Stopwatch.StartNew();
        if (Args.Option(args, "--cluster-threshold") is { } ct) SpeechEngine.DiarizationClusterThreshold = float.Parse(ct, System.Globalization.CultureInfo.InvariantCulture);
        var t = TranscriptionPipeline.Run(id, settings, Cleanup, (p, msg) =>
        {
            if (msg != last) { last = msg; Console.WriteLine($"  [{p * 100,3:F0}%] {msg}" + (Args.Flag(args, "--timing") ? $" (+{clock.Elapsed.TotalSeconds:F1}s)" : "")); }
        });
        if (!Args.Flag(args, "--quiet"))
            foreach (var turn in t.Turns) Console.WriteLine($"[{Clock(turn.Start)}] {t.Label(turn.Speaker)}: {turn.Text}");
        Console.WriteLine($"processing {t.ProcessingSeconds:F1}s");
        return Task.FromResult(0);
    }

    static async Task<int> LiveTest(string[] args)
    {
        var pos = Args.Positional(args, "--speed", "--from", "--seconds", "--save");
        var modes = new Dictionary<string, MeetingMode> { ["call"] = MeetingMode.Call, ["inPerson"] = MeetingMode.InPerson, ["hybrid"] = MeetingMode.Hybrid };
        var files = pos.Where(p => !modes.ContainsKey(p)).ToList();
        if (files.Count == 0) throw new UsageException();
        var mode = pos.Where(modes.ContainsKey).Select(p => modes[p]).Cast<MeetingMode?>().FirstOrDefault()
                   ?? (files.Count > 1 ? MeetingMode.Call : MeetingMode.InPerson);
        double speed = double.Parse(Args.Option(args, "--speed") ?? "4", System.Globalization.CultureInfo.InvariantCulture);
        double from = double.Parse(Args.Option(args, "--from") ?? "0", System.Globalization.CultureInfo.InvariantCulture);
        double? seconds = Args.Option(args, "--seconds") is { } sec ? double.Parse(sec, System.Globalization.CultureInfo.InvariantCulture) : null;
        bool quiet = Args.Flag(args, "--quiet");

        var tracks = files.Count > 1 ? new[] { Track.Mic, Track.System } : new[] { Track.Mic };
        var audio = files.Select(f =>
        {
            var a = AudioFile.Read(f);
            int start = Math.Min(a.Length, (int)(from * 16_000));
            int end = seconds is { } s ? Math.Min(a.Length, start + (int)(s * 16_000)) : a.Length;
            return a[start..end];
        }).ToList();

        // Stability: how often a line's known speaker changes after it was first known.
        var firstSpeaker = new Dictionary<string, string>();
        int flips = 0, updates = 0;
        var flipLock = new object();
        var live = new LiveTranscriber(mode, lines =>
        {
            lock (flipLock)
            {
                updates++;
                foreach (var l in lines.Where(l => l.Speaker is not null))
                {
                    if (!firstSpeaker.TryGetValue(l.Id, out var f)) firstSpeaker[l.Id] = l.Speaker!;
                    else if (f != l.Speaker) { flips++; firstSpeaker[l.Id] = l.Speaker!; }
                }
            }
        });
        live.Start(tracks);
        const int block = 1600;
        int n = audio.Max(a => a.Length);
        var clock = Stopwatch.StartNew();
        for (int i = 0; i < n; i += block)
        {
            for (int k = 0; k < audio.Count; k++)
                if (i < audio[k].Length) live.Feed(tracks[k], audio[k][i..Math.Min(audio[k].Length, i + block)]);
            // 100 ms of audio every 100/speed ms.
            var due = TimeSpan.FromSeconds((i + block) / 16_000.0 / speed);
            if (due > clock.Elapsed) await Task.Delay(due - clock.Elapsed);
            if ((i + block) % (16_000 * 10) < block)
            {
                var lines = live.Lines;
                var last = lines.Count > 0 ? lines[^1] : null;
                Console.WriteLine($"t={(i + block) / 16_000.0,5:F1}s audio, {lines.Count} lines, unknown speaker {lines.Count(l => l.Speaker is null)}"
                                  + (quiet ? "" : $", last: {last?.Text ?? "-"}"));
            }
        }
        await live.StopAsync();
        Console.WriteLine($"fed {n / 16_000.0:F1}s in {clock.Elapsed.TotalSeconds:F1}s");
        var final = live.Lines;
        if (!quiet)
            foreach (var l in final)
                Console.WriteLine($"[{l.Start,5:F1}] {(l.IsFinal ? " " : "~")} {l.Speaker ?? "?"}: {l.Text}");
        var labels = new LiveSpeakerLabels(mode, "Me");
        var bySpeaker = final.GroupBy(l => l.Speaker ?? "?").Select(g => $"{g.Key} ({labels.Label(g.Key == "?" ? null : g.Key)}): {g.Count()} lines, {g.Sum(l => l.Text.Split(' ').Length)} words");
        Console.WriteLine($"lines {final.Count}, final {final.Count(l => l.IsFinal)}, words {final.Sum(l => l.Text.Split(' ').Length)}, updates {updates}, speaker changes after first label {flips}");
        foreach (var line in bySpeaker) Console.WriteLine("  " + line);
        if (Args.Option(args, "--save") is { } save)
            File.WriteAllText(save, System.Text.Json.JsonSerializer.Serialize(final.Select(l => new { id = l.Id, track = l.Track.Raw(), start = l.Start + from, end = l.End + from, speaker = l.Speaker, words = l.Text.Split(' ').Length })));
        return 0;
    }

    static Task<int> Enroll(string[] args)
    {
        if (args.Length == 0) throw new UsageException();
        var name = args.Length > 1 ? args[1] : "Me";
        try
        {
            var v = Enrollment.Enroll(name, AudioFile.Read(args[0]));
            Console.WriteLine($"enrolled {v.Name} from {v.Seconds:F1}s of speech");
            return Task.FromResult(0);
        }
        catch (PipelineException e)
        {
            Console.WriteLine($"error: {e.Message}");
            return Task.FromResult(1);
        }
    }

    static Task<int> Speakers(string[] args)
    {
        var sub = args.FirstOrDefault() ?? "list";
        var lib = SpeakerLibraryStore.Load();
        switch (sub)
        {
            case "list":
                foreach (var e in lib.Entries) Console.WriteLine($"{e.Name} · from {e.Count} meeting{(e.Count == 1 ? "" : "s")}");
                if (lib.Entries.Count == 0) Console.WriteLine("no remembered voices");
                break;
            case "forget":
                if (args.Length < 2) throw new UsageException();
                lib.Forget(string.Join(" ", args.Skip(1)));
                lib.Save();
                Console.WriteLine($"{lib.Entries.Count} remembered voices left");
                break;
            case "voiceprint":
                var v = VoiceprintStore.Load();
                Console.WriteLine(v is null ? "no voiceprint" : $"{v.Name}: {v.Seconds:F0} s recorded {v.CreatedAt:yyyy-MM-dd}");
                break;
            case "delete-voiceprint":
                VoiceprintStore.Delete();
                Console.WriteLine("voiceprint deleted");
                break;
            default: throw new UsageException();
        }
        return Task.FromResult(0);
    }

    /// `mm:ss` below one hour, else `h:mm:ss` (spec 01 conventions).
    static string Clock(double t)
    {
        int s = (int)Math.Max(0, t);
        return s >= 3600 ? $"{s / 3600}:{s % 3600 / 60:D2}:{s % 60:D2}" : $"{s / 60:D2}:{s % 60:D2}";
    }
}
