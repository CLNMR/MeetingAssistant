using System.Buffers.Binary;
using System.Text;

namespace MeetingAssistant.Core.Audio;

/// Reads and writes session audio: 16 kHz mono 16-bit PCM. Windows writes WAV; Mac sessions
/// use CAF with the same samples, so both are read (spec 01-recording, "Session storage").
public static class AudioFile
{
    public const int SampleRate = 16_000;

    /// Samples as floats in [-1, 1]. Reads WAV (PCM 16/24/32-bit int or 32-bit float, any channel
    /// count, mixed down; resampled to 16 kHz if needed) and CAF (linear PCM, big or little endian).
    public static float[] Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 12 && Ascii(bytes, 0, 4) == "RIFF" && Ascii(bytes, 8, 4) == "WAVE") return ReadWav(bytes);
        if (bytes.Length >= 8 && Ascii(bytes, 0, 4) == "caff") return ReadCaf(bytes);
        throw new InvalidDataException($"Unsupported audio file: {Path.GetFileName(path)}");
    }

    /// Duration in seconds without decoding everything (falls back to decoding).
    public static double Duration(string path) => Read(path).Length / (double)SampleRate;

    /// Write 16 kHz mono 16-bit PCM WAV.
    public static void WriteWav(string path, ReadOnlySpan<float> samples)
    {
        using var fs = File.Create(path);
        var w = new WavWriter(fs);
        w.Append(samples);
        w.Finish();
    }

    static string Ascii(byte[] b, int o, int n) => Encoding.ASCII.GetString(b, o, n);

    static float[] ReadWav(byte[] b)
    {
        int pos = 12, channels = 1, rate = SampleRate, bits = 16, format = 1;
        int dataOff = -1, dataLen = 0;
        while (pos + 8 <= b.Length)
        {
            var id = Ascii(b, pos, 4);
            int len = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(pos + 4));
            int body = pos + 8;
            if (id == "fmt ")
            {
                format = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(body));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(body + 2));
                rate = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(body + 4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(body + 14));
                if (format == 0xFFFE && len >= 26) format = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(body + 24));
            }
            else if (id == "data")
            {
                dataOff = body;
                dataLen = (len < 0 || body + len > b.Length) ? b.Length - body : len; // tolerate unfinished files
                break;
            }
            pos = body + len + (len & 1);
        }
        if (dataOff < 0) return Array.Empty<float>();
        var mono = Decode(b.AsSpan(dataOff, dataLen), channels, bits, float32: format == 3, bigEndian: false);
        return rate == SampleRate ? mono : Resample(mono, rate, SampleRate);
    }

    static float[] ReadCaf(byte[] b)
    {
        int pos = 8, channels = 1, bits = 16;
        double rate = SampleRate;
        bool isFloat = false, little = false;
        while (pos + 12 <= b.Length)
        {
            var id = Ascii(b, pos, 4);
            long len = BinaryPrimitives.ReadInt64BigEndian(b.AsSpan(pos + 4));
            int body = pos + 12;
            if (id == "desc")
            {
                rate = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(b.AsSpan(body)));
                var fmtFlags = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(body + 12));
                channels = (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(body + 24));
                bits = (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(body + 28));
                isFloat = (fmtFlags & 1) != 0;
                little = (fmtFlags & 2) != 0;
            }
            else if (id == "data")
            {
                int start = body + 4; // edit count
                int n = len < 0 || body + len > b.Length ? b.Length - start : (int)len - 4;
                var mono = Decode(b.AsSpan(start, Math.Max(0, n)), channels, bits, isFloat, bigEndian: !little);
                return (int)rate == SampleRate ? mono : Resample(mono, (int)rate, SampleRate);
            }
            if (len < 0) break;
            pos = body + (int)len;
        }
        return Array.Empty<float>();
    }

    static float[] Decode(ReadOnlySpan<byte> d, int channels, int bits, bool float32, bool bigEndian)
    {
        int bps = bits / 8, frame = bps * Math.Max(1, channels);
        int frames = d.Length / frame;
        var outp = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            double sum = 0;
            for (int c = 0; c < channels; c++)
            {
                var s = d.Slice(f * frame + c * bps, bps);
                double v = (bits, float32) switch
                {
                    (32, true) => bigEndian ? BinaryPrimitives.ReadSingleBigEndian(s) : BinaryPrimitives.ReadSingleLittleEndian(s),
                    (16, _) => (bigEndian ? BinaryPrimitives.ReadInt16BigEndian(s) : BinaryPrimitives.ReadInt16LittleEndian(s)) / 32768.0,
                    (24, _) => Int24(s, bigEndian) / 8388608.0,
                    (32, false) => (bigEndian ? BinaryPrimitives.ReadInt32BigEndian(s) : BinaryPrimitives.ReadInt32LittleEndian(s)) / 2147483648.0,
                    _ => 0,
                };
                sum += v;
            }
            outp[f] = (float)(sum / Math.Max(1, channels));
        }
        return outp;
    }

    static int Int24(ReadOnlySpan<byte> s, bool be)
    {
        int v = be ? (s[0] << 16) | (s[1] << 8) | s[2] : (s[2] << 16) | (s[1] << 8) | s[0];
        return (v & 0x800000) != 0 ? v - 0x1000000 : v;
    }

    /// Linear-interpolation resampler (good enough for speech at 16 kHz).
    public static float[] Resample(ReadOnlySpan<float> input, int fromRate, int toRate)
    {
        if (fromRate == toRate || input.Length == 0) return input.ToArray();
        long n = (long)Math.Floor(input.Length * (double)toRate / fromRate);
        var outp = new float[n];
        double step = fromRate / (double)toRate;
        for (long i = 0; i < n; i++)
        {
            double x = i * step;
            int a = (int)x;
            int bIdx = Math.Min(a + 1, input.Length - 1);
            double t = x - a;
            outp[i] = (float)(input[a] * (1 - t) + input[bIdx] * t);
        }
        return outp;
    }
}

/// Streaming 16 kHz mono 16-bit WAV writer. The header is updated on Finish (and on Flush), so a
/// file cut off by a crash is still readable up to the last flush.
public sealed class WavWriter : IDisposable
{
    readonly Stream _s;
    long _samples;
    bool _finished;

    public WavWriter(Stream s)
    {
        _s = s;
        WriteHeader();
    }

    public static WavWriter Create(string path) => new(new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read));

    public long SamplesWritten => _samples;
    public double Seconds => _samples / (double)AudioFile.SampleRate;

    public void Append(ReadOnlySpan<float> samples)
    {
        var buf = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            var v = (short)Math.Clamp((int)Math.Round(samples[i] * 32767.0), short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(i * 2), v);
        }
        _s.Write(buf);
        _samples += samples.Length;
    }

    /// Silence, used to align a track that started late (spec "Track writer").
    public void AppendSilence(long count)
    {
        var chunk = new float[Math.Min(count, 16_000)];
        while (count > 0)
        {
            var n = (int)Math.Min(count, chunk.Length);
            Append(chunk.AsSpan(0, n));
            count -= n;
        }
    }

    public void Flush()
    {
        var pos = _s.Position;
        PatchSizes();
        _s.Position = pos;
        _s.Flush();
    }

    public void Finish()
    {
        if (_finished) return;
        _finished = true;
        PatchSizes();
        _s.Flush();
    }

    void WriteHeader()
    {
        Span<byte> h = stackalloc byte[44];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(h);
        BinaryPrimitives.WriteInt32LittleEndian(h[4..], 36);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(h[8..]);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(h[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(h[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(h[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(h[22..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(h[24..], AudioFile.SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(h[28..], AudioFile.SampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(h[32..], 2);
        BinaryPrimitives.WriteInt16LittleEndian(h[34..], 16);
        Encoding.ASCII.GetBytes("data").CopyTo(h[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(h[40..], 0);
        _s.Write(h);
    }

    void PatchSizes()
    {
        var data = (int)Math.Min(int.MaxValue - 36, _samples * 2);
        Span<byte> b = stackalloc byte[4];
        _s.Position = 4; BinaryPrimitives.WriteInt32LittleEndian(b, 36 + data); _s.Write(b);
        _s.Position = 40; BinaryPrimitives.WriteInt32LittleEndian(b, data); _s.Write(b);
        _s.Position = _s.Length;
    }

    public void Dispose()
    {
        Finish();
        _s.Dispose();
    }
}
