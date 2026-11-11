using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using MeetingAssistant.Core.Audio;
using MeetingAssistant.Core.Capture;
using MeetingAssistant.Core.Claude;
using MeetingAssistant.Core.Dictation;
using MeetingAssistant.Core.Input;
using MeetingAssistant.Core.Platform;
using MeetingAssistant.Core.Storage;
using MeetingAssistant.Core.Text;
using MeetingAssistant.Speech;

namespace Msctl.Commands;

/// Push-to-talk dictation end to end without a keyboard or a target app (spec 04 §1, §1.9 test hook):
/// the real <see cref="DictationController"/> with the audio file as the microphone (played in real
/// time between "press" and "release"), Parakeet, the vocabulary, Scribe+ through the local `claude`
/// CLI, fit against a given cursor context, and a fake paste target that prints what would be pasted.
public sealed class DictationCommands : ICommandGroup
{
    public IEnumerable<Command> Commands => new[]
    {
        new Command("dictate",
            "dictate <audio> [--plus] [--runs N] [--direct] [--before T] [--after T] [--app NAME] [--model sonnet|haiku] [--no-context] [--cold] [--out file.jsonl] [--verbose] — a dictation through the real controller; prints what would be pasted and the step timings",
            Dictate),
    };

    static async Task<int> Dictate(string[] args)
    {
        var files = Args.Positional(args, "--runs", "--before", "--after", "--app", "--model", "--out");
        if (files.Count != 1) throw new UsageException();
        var samples = AudioFile.Read(files[0]);
        bool plus = Args.Flag(args, "--plus"), direct = Args.Flag(args, "--direct"), verbose = Args.Flag(args, "--verbose");
        int runs = int.TryParse(Args.Option(args, "--runs"), out var n) ? Math.Max(1, n) : 1;
        var outFile = Args.Option(args, "--out");

        UseWordLists();
        var prefs = Preferences.Load();
        prefs.FitToCursor = true;
        prefs.ScribePlusContext = !Args.Flag(args, "--no-context");
        prefs.ScribePlusWarm = plus && !Args.Flag(args, "--cold");
        prefs.MuteWhileDictating = false;
        if (Args.Option(args, "--model") is { } model) prefs.ScribePlusModel = model;

        var sw = Stopwatch.StartNew();
        SpeechEngine.Shared.Preload();
        Console.Error.WriteLine($"speech model loaded in {sw.Elapsed.TotalSeconds:F1} s");
        var binary = ClaudeCli.Locate();
        if (plus) Console.Error.WriteLine(binary is null ? "claude: not found (Scribe+ will fall back)" : $"claude: {binary}");

        var field = new DictationField(Args.Option(args, "--app") ?? "msctl", new FieldIdentity(1, new[] { 1 }, 50004, (0, 0)),
            Args.Option(args, "--before"), Args.Option(args, "--after"));
        // Recovery files go to a temporary folder, never into the app data.
        var recovery = new DictationRecoveryStore(Path.Combine(Path.GetTempPath(), "msctl-dictations-" + Environment.ProcessId));
        var output = new PrintingOutput();
        var lines = new List<string>();
        int rc = 0;

        PumpContext.Run(async () =>
        {
            using var c = new DictationController(
                new FileMicrophone(samples), new DelegateRecognizer(SpeechEngine.Shared.Dictate), new FixedField(field), output,
                m => binary is null ? throw ClaudeException.NotInstalled()
                                    : new ClaudeRewriteSession(binary, ScribePlus.System, m, ScribePlus.Effort),
                () => prefs, recovery: recovery);
            c.Vocabulary = Vocabulary.Load();
            c.Log += l => Console.Error.WriteLine("log: " + l);
            string last = "";
            var t0 = Stopwatch.StartNew();
            if (verbose)
                c.Changed += () =>
                {
                    var s = $"{c.Phase} {(c.Phase == DictationPhase.Message ? c.Message : c.Phase == DictationPhase.Transcribing ? c.Detail : "")} {(c.Preview.Length > 0 ? "| " + c.Preview : "")}".Trim();
                    if (s != last) { last = s; Console.Error.WriteLine($"  [{t0.Elapsed.TotalSeconds,5:F2}] {s}"); }
                };
            for (int i = 0; i < runs; i++)
            {
                if (prefs.ScribePlusWarm)
                {
                    // As in daily use (and the Mac test hook): the spare process is ready before the press.
                    c.UpdateSpare(TimeSpan.Zero);
                    await Task.Delay(TimeSpan.FromSeconds(8));
                }
                DictationText.Steps steps;
                if (direct)
                {
                    var mode = plus ? DictationMode.Plus : DictationMode.Plain;
                    steps = await c.FinishAsync(samples, InventedText.LongestVoicedRun(samples), mode,
                        (text, note) => { Console.WriteLine($"would paste: «{text}»" + (note is null ? "" : $" (note: {note})")); return Task.CompletedTask; });
                }
                else
                {
                    c.Press(plus ? DictationMode.Plus : DictationMode.Plain);
                    if (c.Phase != DictationPhase.Listening) { Console.Error.WriteLine($"not listening: {c.Message}"); rc = 1; return; }
                    await Task.Delay(TimeSpan.FromSeconds(samples.Length / 16_000.0 + 0.05));
                    await c.ReleaseAsync();
                    steps = c.LastSteps;
                    // Let the HUD settle (toast or hide after paste).
                    for (int k = 0; k < 50 && c.Phase == DictationPhase.Transcribing; k++) await Task.Delay(100);
                    if (c.Phase == DictationPhase.Message) Console.WriteLine($"toast: {c.Message}");
                }
                var json = JsonSerializer.Serialize(new
                {
                    mode = steps.Mode, audio = Math.Round(steps.Audio, 2), asr = Math.Round(steps.Asr, 3), cleanup = Math.Round(steps.Cleanup, 3),
                    field = Math.Round(steps.Field, 3), claude = Math.Round(steps.Claude, 3), total = Math.Round(steps.Total, 3),
                    outcome = steps.Outcome, raw = steps.Raw, text = steps.Text,
                }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                Console.WriteLine(json);
                lines.Add(json);
            }
            if (c.Unfinished.Count > 0) Console.Error.WriteLine($"kept dictations: {c.Unfinished.Count}");
        });
        if (outFile is not null) await File.WriteAllLinesAsync(outFile, lines);
        try { Directory.Delete(recovery.Folder, true); } catch { }
        return rc;
    }

    /// msctl has no system spell checker: word lists from MEETINGASSISTANT_WORDLISTS, else /usr/share/dict/words.
    static void UseWordLists()
    {
        if (SpellCheck.Checker is not null) return;
        var env = Environment.GetEnvironmentVariable("MEETINGASSISTANT_WORDLISTS");
        var paths = env is { Length: > 0 } ? env.Split(Path.PathSeparator) : new[] { "/usr/share/dict/words" };
        var checker = new WordListSpellChecker(paths);
        if (checker.Count > 0) SpellCheck.Checker = checker.IsKnown;
    }

    /// The audio file as the microphone, delivered in real time from the press on.
    sealed class FileMicrophone(float[] samples) : IDictationMicrophone
    {
        public MicrophonePermission Permission => MicrophonePermission.Granted;
        public void RequestPermission() { }
        public IAudioSource Create() => new FileAudioSource(samples, () => HostClock.Now);
    }

    sealed class FixedField(DictationField field) : IFocusedFieldSource
    {
        public Task<DictationField?> ReadAsync(bool readText) =>
            Task.FromResult<DictationField?>(readText ? field : field with { Before = null, After = null });
    }

    sealed class PrintingOutput : IDictationOutput
    {
        public Task<PasteResult> PasteAsync(string text)
        {
            Console.WriteLine($"pasted: «{text}»");
            return Task.FromResult(PasteResult.Ok);
        }

        public bool Copy(string text)
        {
            Console.WriteLine($"copied: «{text}»");
            return true;
        }
    }

    /// Runs async code on one thread, like the app's UI thread (the controller is single-threaded).
    sealed class PumpContext : SynchronizationContext
    {
        readonly BlockingCollection<(SendOrPostCallback, object?)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state)
        {
            try { _queue.Add((d, state)); } catch (InvalidOperationException) { ThreadPool.QueueUserWorkItem(_ => d(state)); }
        }

        public static void Run(Func<Task> body)
        {
            var ctx = new PumpContext();
            var previous = Current;
            SetSynchronizationContext(ctx);
            try
            {
                var task = body();
                task.ContinueWith(_ => ctx._queue.CompleteAdding(), TaskScheduler.Default);
                foreach (var (d, state) in ctx._queue.GetConsumingEnumerable()) d(state);
                task.GetAwaiter().GetResult();
            }
            finally { SetSynchronizationContext(previous); }
        }
    }
}
