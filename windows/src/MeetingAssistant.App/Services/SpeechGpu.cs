using System.Diagnostics;
using System.IO;
using System.Reflection;
using MeetingAssistant.Core;
using MeetingAssistant.Core.Storage;
using MeetingAssistant.Speech;
using Microsoft.Win32;

namespace MeetingAssistant.App.Services;

/// <summary>
/// Settings › Models › *Use the graphics card for transcription* (spec 04 §6.5, 01 "Windows
/// implementation"). Installed as <see cref="SpeechEngine.ProviderSource"/>: the first GPU-capable
/// model load of the app run decides the provider for the whole run (changing the setting applies
/// after a restart, so loaded models never switch under a running transcription).
/// With the setting on, the graphics card is checked first: in a child process (the app with
/// `-gpuProbe`), because a DirectML failure in native code could end the process. The result is
/// cached in `preferences.json` (<see cref="SpeechAcceleration.CacheKey"/>) until the app version,
/// its onnxruntime.dll or the display adapters / drivers change.
/// </summary>
public sealed class SpeechGpu
{
    readonly SettingsStore _settings;
    readonly Func<Preferences> _preferences;
    /// Held while deciding (and during the check); the UI reads the volatile fields without it.
    readonly object _decideLock = new();
    volatile SpeechProviderDecision? _decided;
    volatile GpuProbeResult? _probe;
    readonly Lazy<string> _fingerprint = new(ComputeFingerprint);

    public SpeechGpu(SettingsStore settings, Func<Preferences> preferences)
    {
        _settings = settings;
        _preferences = preferences;
    }

    string Fingerprint() => _fingerprint.Value;

    /// Raised (on a background thread) after the provider of this run was decided.
    public event Action? Changed;

    /// The provider of this app run, or null while no GPU-capable model has loaded.
    public SpeechProviderDecision? Decided => _decided;

    /// The check result of this run, else the cached one; null when unknown.
    public GpuProbeResult? KnownProbe
    {
        get
        {
            if (_probe is { } p) return p;
            return SpeechAcceleration.CachedResult(_settings.Get(SpeechAcceleration.CacheKey), Fingerprint());
        }
    }

    /// The status line of the setting.
    public string Status => SpeechAcceleration.Status(_preferences().SpeechOnGpu, Decided, KnownProbe);

    /// Forgets the cached check, so the next start checks the graphics card again (setting switched on).
    public void ForgetCachedCheck() => _settings.Set(SpeechAcceleration.CacheKey, null);

    /// <see cref="SpeechEngine.ProviderSource"/>: the provider of this run, deciding it on first use.
    /// Blocks while the check runs (only on the loading thread; the first load is in the background).
    public SpeechProviderChoice Choose()
    {
        SpeechProviderDecision decision;
        if (_decided is { } done) return done.Choice;
        lock (_decideLock)
        {
            if (_decided is not null) return _decided.Choice;
            bool wanted = _preferences().SpeechOnGpu;
            GpuProbeResult? probe = null;
            if (wanted)
            {
                // The check needs the models; without them, decide at a later load.
                if (!ModelCatalog.CampPlus.IsReady || !ModelCatalog.Segmentation.IsReady || !ModelCatalog.Parakeet.IsReady)
                    return SpeechProviderChoice.Cpu();
                probe = _probe ??= CachedOrProbe();
            }
            decision = SpeechAcceleration.Decide(wanted, probe);
            _decided = decision;
        }
        AppLog.Write(decision.Choice.OnGpu
            ? $"speech: graphics card (DirectML), {decision.Choice.NumThreads} threads"
            : $"speech: processor, {decision.Choice.NumThreads} threads" + (decision.Reason is { } r ? $" ({r})" : decision.GpuWanted ? "" : " (setting off)"));
        Changed?.Invoke();
        return decision.Choice;
    }

    GpuProbeResult CachedOrProbe()
    {
        var fingerprint = Fingerprint();
        if (SpeechAcceleration.CachedResult(_settings.Get(SpeechAcceleration.CacheKey), fingerprint) is { } cached)
        {
            AppLog.Write($"gpu check: cached result: {(cached.Ok ? "ok" : cached.Reason)}");
            return cached;
        }
        var result = Probe();
        try { _settings.Set(SpeechAcceleration.CacheKey, SpeechAcceleration.CacheJson(fingerprint, result, DateTime.UtcNow)); }
        catch (Exception e) { AppLog.Write($"gpu check: could not cache the result: {e.Message}"); }
        return result;
    }

    /// Runs the app with `-gpuProbe` and reads its exit code, stdout and stderr.
    static GpuProbeResult Probe()
    {
        // The NuGet sherpa-onnx DLLs are CPU-only; only the release build ships the DirectML ones.
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "DirectML.dll")))
        {
            AppLog.Write("gpu check: DirectML.dll is not next to the app, skipped");
            return GpuProbeResult.Failed("this build has no DirectML support");
        }
        var exe = Environment.ProcessPath;
        if (exe is null || Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return GpuProbeResult.Failed("the graphics card check could not start");
        var watch = Stopwatch.StartNew();
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            psi.ArgumentList.Add(SpeechAcceleration.ProbeArgument);
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("no process");
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            bool exited = p.WaitForExit(SpeechAcceleration.ProbeTimeout);
            if (!exited)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
                p.WaitForExit(5_000);
            }
            string Read(Task<string> t) => t.Wait(5_000) ? t.Result : "";
            string so = Read(stdout), se = Read(stderr);
            var result = SpeechAcceleration.ParseProbe(exited ? p.ExitCode : null, !exited, so, se);
            AppLog.Write($"gpu check: {(result.Ok ? "ok" : $"failed: {result.Reason}")} after {watch.Elapsed.TotalSeconds:0.0} s"
                         + (exited ? $", exit code {p.ExitCode}" : ", killed"));
            if (!string.IsNullOrWhiteSpace(se)) AppLog.Write("gpu check stderr: " + Tail(se, 2_000));
            return result;
        }
        catch (Exception e)
        {
            AppLog.Write($"gpu check: could not start: {e.Message}");
            return GpuProbeResult.Failed($"the graphics card check could not start ({e.Message})");
        }
    }

    static string Tail(string s, int max) => (s.Length <= max ? s : "…" + s[^max..]).Replace("\r", "").Replace("\n", " | ").Trim();

    /// App version + onnxruntime.dll size + display adapters with driver versions.
    static string ComputeFingerprint()
    {
        var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
        long ort = -1;
        try { var f = new FileInfo(Path.Combine(AppContext.BaseDirectory, "onnxruntime.dll")); if (f.Exists) ort = f.Length; } catch { }
        return SpeechAcceleration.Fingerprint(version, ort, DisplayAdapters());
    }

    /// "Name/driver version" of every display adapter (registry class of display devices).
    static List<string> DisplayAdapters()
    {
        var list = new List<string>();
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls is null) return list;
            foreach (var name in cls.GetSubKeyNames())
            {
                if (!name.All(char.IsDigit)) continue;
                try
                {
                    using var k = cls.OpenSubKey(name);
                    if (k?.GetValue("DriverDesc") is string desc)
                        list.Add($"{desc}/{k.GetValue("DriverVersion") as string ?? "?"}");
                }
                catch { /* no access to this adapter's key */ }
            }
        }
        catch { /* no registry access: the version alone identifies the result */ }
        return list;
    }
}

/// The app log (spec 01 "Windows implementation"): `<root>\Logs\app.log`, rotated at 1 MB (one old file kept).
static class AppLog
{
    const long MaxBytes = 1_000_000;
    static readonly object Lock = new();

    public static string File => Path.Combine(AppPaths.Root, "Logs", "app.log");

    public static void Write(string line)
    {
        Debug.WriteLine(line);
        lock (Lock)
        {
            try
            {
                var path = File;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    var old = path + ".1";
                    if (System.IO.File.Exists(old)) System.IO.File.Delete(old);
                    System.IO.File.Move(path, old);
                }
                System.IO.File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}");
            }
            catch { /* logging must never break the app */ }
        }
    }
}
