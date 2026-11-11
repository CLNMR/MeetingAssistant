using MeetingAssistant.Core.Audio;
using MeetingAssistant.Core.Capture;
using Xunit;

namespace MeetingAssistant.Core.Tests.Capture;

public sealed class TrackWriterTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("ma-capture-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    static float[] Tone(int n, float v = 0.5f) => Enumerable.Repeat(v, n).ToArray();

    [Fact]
    public void PadsTheDelayBeforeTheFirstBuffer()
    {
        var w = new TrackWriter(null, sessionStart: 100, now: () => 101);
        w.Append(Tone(1600), captureTime: 100.5);              // 0.5 s late → 8000 zeros first
        Assert.Equal(8000 + 1600, w.SamplesWritten);
    }

    [Fact]
    public void IgnoresJitterUpTo100ms()
    {
        var w = new TrackWriter(null, 0, () => 0);
        w.Append(Tone(1600), 0.01);
        Assert.Equal(1600, w.SamplesWritten);                   // expected 160: no pad
        w.Append(Tone(1600), 0.1 + 0.1);                        // expected 3200, written 1600: gap 1600 = not > 1600
        Assert.Equal(3200, w.SamplesWritten);
        w.Append(Tone(1600), 0.3 + 0.0001);                     // expected 4801, written 3200: gap 1601 → pad
        Assert.Equal(4801 + 1600, w.SamplesWritten);
    }

    [Fact]
    public void NeverDropsSamplesWhenAhead()
    {
        var w = new TrackWriter(null, 0, () => 0);
        w.Append(Tone(16000), 0.0);
        w.Append(Tone(16000), 0.2);                             // arrives "early": kept in full
        Assert.Equal(32000, w.SamplesWritten);
    }

    [Fact]
    public void BuffersWithoutValidTimeAreAppendedAsIs()
    {
        var w = new TrackWriter(null, 50, () => 50);
        w.Append(Tone(100), null);
        w.Append(Tone(100), 40);                                // before the session start: no alignment
        Assert.Equal(200, w.SamplesWritten);
    }

    [Fact]
    public void DropoutIsFilledWithSilenceAndForwarded()
    {
        var forwarded = new List<float>();
        var w = new TrackWriter(null, 0, () => 0) { OnSamples = s => forwarded.AddRange(s) };
        w.Append(Tone(16000), 0);
        w.Append(Tone(16000), 3.0);                             // 2 s dropout
        Assert.Equal(16000 * 4, w.SamplesWritten);
        Assert.Equal(16000 * 4, forwarded.Count);
        Assert.All(forwarded.Skip(16000).Take(32000), x => Assert.Equal(0f, x));
        Assert.Equal(0.5f, forwarded[^1]);
    }

    [Fact]
    public void FinishPadsToTheGivenLength()
    {
        var path = Path.Combine(_dir, "mic.wav");
        var w = new TrackWriter(path, 0, () => 0);
        w.Append(Tone(8000), 0);
        w.Finish(2.0);
        var s = AudioFile.Read(path);
        Assert.Equal(32000, s.Length);
        Assert.Equal(0.5f, s[0], 3);
        Assert.Equal(0f, s[^1]);
        w.Append(Tone(10), 3);                                  // closed: ignored
    }

    [Fact]
    public void FileIsReadableAfterFlushWithoutFinish()
    {
        var path = Path.Combine(_dir, "crash.wav");
        var w = new TrackWriter(path, 0, () => 0);
        w.Append(Tone(4000, 0.25f), 0);
        w.Flush();
        // Simulated crash: copy the file while the writer is still open.
        var copy = Path.Combine(_dir, "copy.wav");
        using (var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var dst = File.Create(copy)) src.CopyTo(dst);
        var s = AudioFile.Read(copy);
        Assert.Equal(4000, s.Length);
        Assert.Equal(0.25f, s[123], 3);
        w.Dispose();
    }

    [Fact]
    public void FlushesTheHeaderEverySecondOfAudio()
    {
        var path = Path.Combine(_dir, "auto.wav");
        var w = new TrackWriter(path, 0, () => 0);
        w.Append(Tone(TrackWriter.FlushEverySamples + 10), 0);
        var copy = Path.Combine(_dir, "auto-copy.wav");
        using (var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var dst = File.Create(copy)) src.CopyTo(dst);
        Assert.Equal(TrackWriter.FlushEverySamples + 10, AudioFile.Read(copy).Length);
        w.Dispose();
    }

    [Fact]
    public void LevelIsPeakHoldAndDecaysToZeroAfter300ms()
    {
        double now = 0;
        var w = new TrackWriter(null, 0, () => now);
        w.Append(Tone(160, 0.5f), null);
        Assert.Equal(0.5f, w.Level, 4);
        w.Append(Tone(160, 0.1f), null);                        // max(0.1, 0.5 × 0.8)
        Assert.Equal(0.4f, w.Level, 4);
        now = 0.29;
        Assert.Equal(0.4f, w.Level, 4);
        now = 0.31;
        Assert.Equal(0f, w.Level);
    }
}

public sealed class StreamingResamplerTests
{
    static float[] Sine(int rate, double hz, double seconds, double amp = 0.5) =>
        Enumerable.Range(0, (int)(rate * seconds)).Select(i => (float)(amp * Math.Sin(2 * Math.PI * hz * i / rate))).ToArray();

    static double Rms(ReadOnlySpan<float> s) => LevelMath.Rms(s);

    [Theory]
    [InlineData(48000)]
    [InlineData(44100)]
    [InlineData(24000)]
    [InlineData(8000)]
    public void KeepsAToneAndTheLength(int rate)
    {
        var r = new StreamingResampler(rate);
        var input = Sine(rate, 440, 2.0);
        var outp = r.Process(input).Concat(r.Flush()).ToArray();
        Assert.InRange(outp.Length, 32000 - 40, 32000 + 40);
        var mid = outp.AsSpan(4000, 24000);
        Assert.InRange(Rms(mid), 0.5 / Math.Sqrt(2) * 0.97, 0.5 / Math.Sqrt(2) * 1.03);
    }

    [Fact]
    public void RemovesContentAboveTheNewNyquist()
    {
        var r = new StreamingResampler(48000);
        var outp = r.Process(Sine(48000, 12000, 1.0));
        Assert.True(Rms(outp.AsSpan(2000, 10000)) < 0.01);
    }

    [Fact]
    public void ChunkedEqualsWhole()
    {
        var input = Sine(44100, 1000, 1.0);
        var whole = new StreamingResampler(44100).Process(input);
        var r = new StreamingResampler(44100);
        var parts = new List<float>();
        var rnd = new Random(7);
        for (int pos = 0; pos < input.Length;)
        {
            int n = Math.Min(rnd.Next(1, 900), input.Length - pos);
            parts.AddRange(r.Process(input.AsSpan(pos, n)));
            pos += n;
        }
        Assert.Equal(whole.Length, parts.Count);
        for (int i = 0; i < whole.Length; i++) Assert.Equal(whole[i], parts[i], 5);
    }

    [Fact]
    public void SameRatePassesThrough()
    {
        var r = new StreamingResampler(16000);
        var x = new float[] { 0.1f, -0.2f, 0.3f };
        Assert.Equal(x, r.Process(x));
    }

    [Fact]
    public void DownmixAveragesChannels()
    {
        Assert.Equal(new[] { 0.5f, 0f }, StreamingResampler.Downmix(new[] { 1f, 0f, 0.5f, -0.5f }, 2));
    }
}

public sealed class LevelMathTests
{
    [Fact]
    public void QuantizesTo2dBStepsAndCutsBelowMinus60()
    {
        Assert.Equal(0f, LevelMath.Quantize(0));
        Assert.Equal(0f, LevelMath.Quantize(0.0005f));                 // −66 dB
        Assert.Equal(MathF.Pow(10, -20 / 20f), LevelMath.Quantize(0.1f), 5);
        Assert.Equal(MathF.Pow(10, -20 / 20f), LevelMath.Quantize(0.105f), 5); // −19.6 dB → −20
        Assert.Equal(MathF.Pow(10, -18 / 20f), LevelMath.Quantize(0.12f), 5);  // −18.4 dB → −18
    }

    [Fact]
    public void MeterMapsMinus60To0dB()
    {
        Assert.Equal(0, LevelMath.Meter(0.0001f));
        Assert.Equal(0.5, LevelMath.Meter((float)Math.Pow(10, -30 / 20.0)), 5);
        Assert.Equal(1, LevelMath.Meter(1f), 6);
    }

    [Fact]
    public void DictationBarsMapMinus55ToMinus10dB()
    {
        Assert.Equal(0, LevelMath.DictationBars(0));
        Assert.Equal(1, LevelMath.DictationBars(0.5f));
        Assert.Equal(0.5, LevelMath.DictationBars((float)Math.Pow(10, -32.5 / 20)), 4);
    }
}
