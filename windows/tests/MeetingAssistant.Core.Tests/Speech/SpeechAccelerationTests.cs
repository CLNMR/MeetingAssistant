using System.Text.Json.Nodes;
using MeetingAssistant.Speech;
using Xunit;

namespace MeetingAssistant.Core.Tests.Speech;

public class SpeechAccelerationTests
{
    const string Ok = SpeechAcceleration.OkMarker + "\n";

    [Fact]
    public void CleanRunUsesTheGraphicsCard()
    {
        var r = SpeechAcceleration.ParseProbe(0, false, Ok, "");
        Assert.True(r.Ok);
        Assert.Null(r.Reason);
        // Unrelated stderr output (sherpa-onnx config dumps, warnings) does not matter.
        Assert.True(SpeechAcceleration.ParseProbe(0, false, Ok, "OfflineRecognizerConfig(...)\n").Ok);
    }

    [Fact]
    public void CpuOnlyBuildFallsBack()
    {
        // The NuGet sherpa-onnx DLLs are built without DirectML.
        var r = SpeechAcceleration.ParseProbe(0, false, Ok,
            "/project/sherpa-onnx/csrc/session.cc:SetProvider:210 DirectML is for Windows only. Fallback to cpu!\n");
        Assert.False(r.Ok);
        Assert.Equal("this build has no DirectML support", r.Reason);
        Assert.False(SpeechAcceleration.ParseProbe(0, false, Ok, "Something. fallback to CPU\r\n").Ok);
    }

    [Fact]
    public void FailedDirectMlNamesTheError()
    {
        var r = SpeechAcceleration.ParseProbe(0, false, Ok,
            "C:\\sherpa\\session.cc:SetProvider:205 Failed to enable DirectML: DXGI_ERROR_UNSUPPORTED. Fallback to cpu\n");
        Assert.False(r.Ok);
        Assert.Equal("DirectML could not be started (DXGI_ERROR_UNSUPPORTED)", r.Reason);
        Assert.Equal("DirectML could not be started",
                     SpeechAcceleration.ParseProbe(0, false, Ok, "Failed to enable DirectML. Fallback to cpu").Reason);
    }

    [Fact]
    public void CrashTimeoutAndMissingMarkerFail()
    {
        Assert.Equal("the graphics card check took longer than 90 s", SpeechAcceleration.ParseProbe(null, true, "", "").Reason);
        Assert.Equal("the graphics card check failed (exit code -1073741819)",
                     SpeechAcceleration.ParseProbe(-1073741819, false, "", "").Reason);
        Assert.Equal("the graphics card check failed (exit code 1: gpu probe: InvalidOperationException: Download the speech models first)",
                     SpeechAcceleration.ParseProbe(1, false, "", "loading\ngpu probe: InvalidOperationException: Download the speech models first\n").Reason);
        Assert.Equal("the graphics card check gave no result", SpeechAcceleration.ParseProbe(0, false, "", "").Reason);
        // A non-zero exit fails even after the marker.
        Assert.False(SpeechAcceleration.ParseProbe(3, false, Ok, "").Ok);
    }

    [Fact]
    public void DecisionProviderAndThreads()
    {
        var off = SpeechAcceleration.Decide(false, GpuProbeResult.Success);
        Assert.Equal("cpu", off.Choice.Provider);
        Assert.Equal(SpeechEngine.Threads, off.Choice.NumThreads);
        Assert.False(off.GpuWanted);

        var gpu = SpeechAcceleration.Decide(true, GpuProbeResult.Success);
        Assert.Equal("directml", gpu.Choice.Provider);
        Assert.Equal(2, gpu.Choice.NumThreads);
        Assert.True(gpu.Choice.OnGpu);

        var failed = SpeechAcceleration.Decide(true, GpuProbeResult.Failed("this build has no DirectML support"));
        Assert.Equal("cpu", failed.Choice.Provider);
        Assert.Equal(SpeechEngine.Threads, failed.Choice.NumThreads);
        Assert.Equal("this build has no DirectML support", failed.Reason);
    }

    [Fact]
    public void StatusLine()
    {
        var fail = GpuProbeResult.Failed("this build has no DirectML support");
        Assert.Equal("Running on the processor.", SpeechAcceleration.Status(false, null, null));
        Assert.Equal("The graphics card is checked when the speech models first load.", SpeechAcceleration.Status(true, null, null));
        Assert.Equal("Running on the graphics card.", SpeechAcceleration.Status(true, null, GpuProbeResult.Success));
        Assert.Equal("Running on the processor: this build has no DirectML support.", SpeechAcceleration.Status(true, null, fail));

        var gpu = SpeechAcceleration.Decide(true, GpuProbeResult.Success);
        var cpu = SpeechAcceleration.Decide(true, fail);
        var off = SpeechAcceleration.Decide(false, null);
        Assert.Equal("Running on the graphics card.", SpeechAcceleration.Status(true, gpu, GpuProbeResult.Success));
        Assert.Equal("Running on the processor: this build has no DirectML support.", SpeechAcceleration.Status(true, cpu, fail));
        Assert.Equal("Running on the processor.", SpeechAcceleration.Status(false, off, null));
        // The run keeps its provider; a changed setting asks for a restart.
        Assert.Equal("Running on the graphics card. Restart MeetingAssistant to apply the change.", SpeechAcceleration.Status(false, gpu, null));
        Assert.Equal("Running on the processor. Restart MeetingAssistant to apply the change.", SpeechAcceleration.Status(true, off, null));
    }

    [Fact]
    public void CacheIsTiedToTheFingerprint()
    {
        var fp = SpeechAcceleration.Fingerprint("1.2.0", 21_000_000, new[] { "Graphics B/31.0", "Graphics A/1.2" });
        Assert.Equal("1.2.0|ort:21000000|Graphics A/1.2;Graphics B/31.0", fp);

        var json = SpeechAcceleration.CacheJson(fp, GpuProbeResult.Failed("DirectML could not be started"), new DateTime(2026, 11, 18, 9, 0, 0, DateTimeKind.Utc));
        Assert.Equal("2026-11-18T09:00:00Z", json["checked"]!.GetValue<string>());
        var back = SpeechAcceleration.CachedResult(JsonNode.Parse(json.ToJsonString()), fp);
        Assert.Equal(GpuProbeResult.Failed("DirectML could not be started"), back);

        Assert.True(SpeechAcceleration.CachedResult(SpeechAcceleration.CacheJson(fp, GpuProbeResult.Success, DateTime.UtcNow), fp)!.Ok);
        // A new app version, DLL or driver runs the check again.
        Assert.Null(SpeechAcceleration.CachedResult(json, "1.2.1|ort:21000000|Graphics A/1.2;Graphics B/31.0"));
        Assert.Null(SpeechAcceleration.CachedResult(null, fp));
        Assert.Null(SpeechAcceleration.CachedResult(JsonNode.Parse("""{"fingerprint":"x"}"""), "x"));
    }

    [Fact]
    public void ProbeAudioIsTwoSecondsOfSignal()
    {
        var a = SpeechEngine.ProbeAudio(2.0);
        Assert.Equal(32_000, a.Length);
        Assert.InRange(SpeechEngine.Rms(a), 0.01f, 0.5f);
    }
}
