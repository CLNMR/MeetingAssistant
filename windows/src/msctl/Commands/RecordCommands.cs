using System.Globalization;
using MeetingAssistant.Core;
using MeetingAssistant.Core.Capture;
using MeetingAssistant.Core.Model;

namespace Msctl.Commands;

/// Recording (spec 04 §8 `record`). msctl is a cross-platform console app without the WASAPI
/// library, so it records from audio files that stand in for the microphone and the call audio,
/// through the same SessionRecorder and track writers the app uses. Live capture is the app's job.
public sealed class RecordCommands : ICommandGroup
{
    public IEnumerable<Command> Commands => new[]
    {
        new Command("record",
            "record <seconds> [call|inPerson|hybrid] --mic <file> [--system <file>] [--system-delay <s>] [--speed <n>] — record a session from audio files (mode default: call)",
            Record),
    };

    static async Task<int> Record(string[] args)
    {
        var pos = Args.Positional(args, "--mic", "--system", "--system-delay", "--speed");
        if (pos.Count < 1 || !double.TryParse(pos[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var secs) || secs <= 0)
            throw new UsageException();
        var mode = pos.Count > 1 ? ParseMode(pos[1]) : MeetingMode.Call;
        var micFile = Args.Option(args, "--mic")
                      ?? throw new UsageException("msctl records from files: pass --mic <file> (live capture runs in the app)");
        var sysFile = Args.Option(args, "--system");
        double delay = ParseDouble(Args.Option(args, "--system-delay") ?? "0");
        double speed = ParseDouble(Args.Option(args, "--speed") ?? "1");
        if (speed <= 0) throw new UsageException("--speed must be > 0");

        var clock = new ScaledClock(speed);
        var rec = new SessionRecorder(mode, clock: clock.Now);
        using var mic = new FileAudioSource(micFile, clock.Now);
        using var sys = mode.RecordsSystemAudio()
            ? (sysFile is not null ? new FileAudioSource(sysFile, clock.Now, startDelay: delay) : new FileAudioSource(Array.Empty<float>(), clock.Now))
            : null;
        rec.SourceFailed += (t, m) => Console.Error.WriteLine($"  {t.Raw()} failed: {m}");
        rec.Start(mic, sys);
        Console.WriteLine($"recording {rec.Info.Id} mode={mode.Raw()} for {secs.ToString(CultureInfo.InvariantCulture)}s ...");
        for (double next = 1; ; next++)
        {
            var target = Math.Min(next, secs);
            while (rec.Elapsed < target)
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(0.002, (target - rec.Elapsed) / speed)));
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "  t={0,4:F1}s  mic={1:F3}  system={2:F3}",
                rec.Elapsed, rec.MicLevel, rec.SystemLevel));
            if (target >= secs) break;
        }
        var info = rec.Stop();
        Console.WriteLine($"saved {AppPaths.Session(info.Id)} duration={info.DurationSec.ToString(CultureInfo.InvariantCulture)}");
        return 0;
    }

    static MeetingMode ParseMode(string s) => s switch
    {
        "call" => MeetingMode.Call,
        "inPerson" => MeetingMode.InPerson,
        "hybrid" => MeetingMode.Hybrid,
        _ => throw new UsageException($"unknown mode: {s} (call, inPerson, hybrid)"),
    };

    static double ParseDouble(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : throw new UsageException($"not a number: {s}");
}
