using System.Text.Json.Nodes;

namespace MeetingAssistant.Speech;

/// The ONNX Runtime execution provider and thread count of the GPU-capable models (Parakeet, CAM++,
/// pyannote segmentation). Silero VAD always runs on the CPU.
public sealed record SpeechProviderChoice(string Provider, int NumThreads)
{
    public const string CpuProvider = "cpu", DirectMlProvider = "directml";

    public bool OnGpu => Provider == DirectMlProvider;

    /// The CPU with the current per-model thread count.
    public static SpeechProviderChoice Cpu() => new(CpuProvider, SpeechEngine.Threads);

    /// DirectML; the CPU only runs the nodes DirectML does not take, with <see cref="SpeechAcceleration.GpuThreads"/> threads.
    public static SpeechProviderChoice Gpu() => new(DirectMlProvider, SpeechAcceleration.GpuThreads);
}

/// Outcome of the graphics-card check: usable, or why not.
public sealed record GpuProbeResult(bool Ok, string? Reason)
{
    public static GpuProbeResult Success => new(true, null);
    public static GpuProbeResult Failed(string reason) => new(false, reason);
}

/// The provider in use for this app run: the choice, whether the setting asked for the graphics
/// card at that moment, and why the processor is used although it did.
public sealed record SpeechProviderDecision(SpeechProviderChoice Choice, bool GpuWanted, string? Reason);

/// <summary>
/// Rules of Settings › Models › *Use the graphics card for transcription* (Windows only; spec 04 §6.5,
/// 01 "Windows implementation"). Before the first GPU model load the app runs itself with
/// <see cref="ProbeArgument"/> in a child process, which loads CAM++, pyannote segmentation and
/// Parakeet with DirectML and runs each once. The graphics card is used only when the child exits
/// with 0, printed <see cref="OkMarker"/> and sherpa-onnx wrote no fallback message to stderr; a
/// native DirectML crash only ends the child. The result is cached in `preferences.json` under
/// <see cref="CacheKey"/> for one fingerprint (app version, onnxruntime.dll size, display adapters).
/// </summary>
public static class SpeechAcceleration
{
    /// Command-line argument of the child process.
    public const string ProbeArgument = "-gpuProbe";
    /// Line the child prints to stdout after every model ran.
    public const string OkMarker = "GPU-PROBE-OK";
    /// The child is killed after this long.
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(90);
    /// Threads per model with DirectML (CPU-side nodes only).
    public const int GpuThreads = 2;
    /// Settings-file key of the cached probe result.
    public const string CacheKey = "speech.gpuProbe";

    /// sherpa-onnx's messages when it could not append the DirectML provider and runs on the CPU.
    static readonly string[] FallbackMarkers = { "Fallback to cpu", "Failed to enable DirectML", "DirectML is for Windows only" };

    /// The result of a finished (or killed) probe run.
    public static GpuProbeResult ParseProbe(int? exitCode, bool timedOut, string stdout, string stderr)
    {
        if (timedOut) return GpuProbeResult.Failed($"the graphics card check took longer than {ProbeTimeout.TotalSeconds:0} s");
        var lines = (stderr ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var failed = lines.FirstOrDefault(l => l.Contains("Failed to enable DirectML", StringComparison.OrdinalIgnoreCase));
        if (failed is not null)
        {
            int colon = failed.IndexOf(':', failed.IndexOf("DirectML", StringComparison.OrdinalIgnoreCase));
            var detail = colon >= 0 ? failed[(colon + 1)..].Trim() : "";
            detail = TrimFallback(detail);
            return GpuProbeResult.Failed(detail.Length > 0 ? $"DirectML could not be started ({Shorten(detail)})" : "DirectML could not be started");
        }
        if (lines.Any(l => FallbackMarkers.Any(m => l.Contains(m, StringComparison.OrdinalIgnoreCase))))
            return GpuProbeResult.Failed("this build has no DirectML support");
        if (exitCode is not 0)
        {
            var last = lines.LastOrDefault();
            var code = exitCode is { } c ? $"exit code {c}" : "no exit code";
            return GpuProbeResult.Failed(last is null ? $"the graphics card check failed ({code})"
                                                      : $"the graphics card check failed ({code}: {Shorten(last)})");
        }
        if (!(stdout ?? "").Contains(OkMarker, StringComparison.Ordinal))
            return GpuProbeResult.Failed("the graphics card check gave no result");
        return GpuProbeResult.Success;
    }

    static string TrimFallback(string s)
    {
        int i = s.IndexOf("Fallback to cpu", StringComparison.OrdinalIgnoreCase);
        return (i >= 0 ? s[..i] : s).Trim().TrimEnd('.', ',', ';').Trim();
    }

    static string Shorten(string s) => s.Length <= 160 ? s : s[..157] + "…";

    /// The provider for the GPU-capable models.
    public static SpeechProviderDecision Decide(bool useGpu, GpuProbeResult? probe) => (useGpu, probe) switch
    {
        (false, _) => new(SpeechProviderChoice.Cpu(), false, null),
        (true, { Ok: true }) => new(SpeechProviderChoice.Gpu(), true, null),
        (true, { } p) => new(SpeechProviderChoice.Cpu(), true, p.Reason),
        (true, null) => new(SpeechProviderChoice.Cpu(), true, "the graphics card has not been checked yet"),
    };

    public const string StatusGpu = "Running on the graphics card.";
    public const string StatusCpu = "Running on the processor.";
    public const string StatusPending = "The graphics card is checked when the speech models first load.";
    public const string StatusRestart = "Restart MeetingAssistant to apply the change.";

    /// The status line under the toggle. <paramref name="decided"/> is the provider of this app run
    /// (null while no model has loaded), <paramref name="probe"/> the known check result.
    public static string Status(bool useGpu, SpeechProviderDecision? decided, GpuProbeResult? probe)
    {
        if (decided is null)
        {
            if (!useGpu) return StatusCpu;
            if (probe is null) return StatusPending;
            return probe.Ok ? StatusGpu : Processor(probe.Reason);
        }
        var text = decided.Choice.OnGpu ? StatusGpu
                 : decided.GpuWanted && decided.Reason is not null ? Processor(decided.Reason)
                 : StatusCpu;
        if (decided.GpuWanted != useGpu) text += " " + StatusRestart;
        return text;
    }

    static string Processor(string? reason) =>
        string.IsNullOrWhiteSpace(reason) ? StatusCpu : $"Running on the processor: {reason.Trim().TrimEnd('.')}.";

    // MARK: Cache

    /// Identifies what the probe result depends on: the app version, the onnxruntime.dll that ships
    /// with it (its size tells the DirectML build from the CPU one) and the display adapters with
    /// their driver versions.
    public static string Fingerprint(string appVersion, long onnxRuntimeSize, IEnumerable<string> adapters) =>
        $"{appVersion}|ort:{onnxRuntimeSize}|{string.Join(";", adapters.OrderBy(a => a, StringComparer.Ordinal))}";

    /// The cached `speech.gpuProbe` object.
    public static JsonObject CacheJson(string fingerprint, GpuProbeResult result, DateTime checkedAt)
    {
        var o = new JsonObject { ["fingerprint"] = fingerprint, ["ok"] = result.Ok, ["checked"] = checkedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'") };
        if (result.Reason is not null) o["reason"] = result.Reason;
        return o;
    }

    /// The cached result when it belongs to <paramref name="fingerprint"/>, else null (run the probe).
    public static GpuProbeResult? CachedResult(JsonNode? node, string fingerprint)
    {
        if (node is not JsonObject o) return null;
        string? Str(string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        if (Str("fingerprint") != fingerprint) return null;
        if (o["ok"] is not JsonValue ok || !ok.TryGetValue<bool>(out var isOk)) return null;
        return isOk ? GpuProbeResult.Success : GpuProbeResult.Failed(Str("reason") ?? "the graphics card check failed");
    }
}
