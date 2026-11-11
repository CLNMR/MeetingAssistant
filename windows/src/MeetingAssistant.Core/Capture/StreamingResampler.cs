namespace MeetingAssistant.Core.Capture;

/// Converts a continuous stream of mono samples at any rate to 16 kHz (or another rate) with a
/// windowed-sinc low-pass filter, keeping state between buffers so chunk borders leave no
/// clicks (spec 01-recording, "Track writer" step 1: "resampled to 16 kHz mono (high-quality
/// resampler)"). Not thread-safe: one instance per capture stream.
public sealed class StreamingResampler
{
    const int Phases = 512;           // kernel table resolution per input sample
    const int ZeroCrossings = 16;     // filter half-width in output-rate periods

    public int FromRate { get; }
    public int ToRate { get; }
    readonly int _halfWidth;          // in input samples
    readonly float[]? _table;         // kernel at x = i / Phases, i = 0 … halfWidth * Phases
    float[] _buf = new float[4096];
    int _len;                         // valid samples in _buf
    long _dropped;                    // input samples removed from the front of _buf
    long _outIndex;                   // next output sample number

    public StreamingResampler(int fromRate, int toRate = 16_000)
    {
        if (fromRate <= 0 || toRate <= 0) throw new ArgumentOutOfRangeException(nameof(fromRate));
        FromRate = fromRate;
        ToRate = toRate;
        if (fromRate == toRate) return;
        double scale = Math.Min(1.0, toRate / (double)fromRate);
        double cutoff = 0.5 * scale * 0.92;                   // cycles per input sample
        _halfWidth = (int)Math.Ceiling(ZeroCrossings / scale);
        _table = new float[_halfWidth * Phases + 2];
        for (int i = 0; i < _table.Length; i++)
        {
            double x = i / (double)Phases;
            if (x > _halfWidth) { _table[i] = 0; continue; }
            double sinc = x == 0 ? 1 : Math.Sin(2 * Math.PI * cutoff * x) / (2 * Math.PI * cutoff * x);
            double w = x / _halfWidth;                          // 0…1, Blackman window over [-N, N]
            double win = 0.42 + 0.5 * Math.Cos(Math.PI * w) + 0.08 * Math.Cos(2 * Math.PI * w);
            _table[i] = (float)(2 * cutoff * sinc * win);
        }
    }

    /// Feed input samples; returns the output samples that are ready (may be empty).
    public float[] Process(ReadOnlySpan<float> input)
    {
        if (_table is null) return input.ToArray();
        Append(input);
        return Drain();
    }

    /// Output the samples still held back by the filter (pads the input with zeros).
    public float[] Flush()
    {
        if (_table is null) return Array.Empty<float>();
        Append(new float[_halfWidth + 1]);
        return Drain();
    }

    void Append(ReadOnlySpan<float> input)
    {
        if (_len + input.Length > _buf.Length)
            Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _len + input.Length));
        input.CopyTo(_buf.AsSpan(_len));
        _len += input.Length;
    }

    float[] Drain()
    {
        var table = _table!;
        var outp = new List<float>(_len * ToRate / FromRate + 2);
        double step = FromRate / (double)ToRate;
        while (true)
        {
            double pos = _outIndex * step - _dropped;        // position in _buf
            int center = (int)Math.Floor(pos);
            int last = center + _halfWidth;
            if (last >= _len) break;
            int first = Math.Max(0, center - _halfWidth + 1);
            double sum = 0, wsum = 0;
            for (int j = first; j <= last; j++)
            {
                double d = Math.Abs(j - pos) * Phases;
                int k = (int)d;
                if (k >= table.Length - 1) continue;
                double t = d - k;
                double w = table[k] + (table[k + 1] - table[k]) * t;
                sum += _buf[j] * w;
                wsum += w;
            }
            outp.Add((float)(wsum > 1e-9 ? sum / wsum : 0));
            _outIndex++;
        }
        // Drop input that no future output needs.
        double next = _outIndex * (FromRate / (double)ToRate) - _dropped;
        int keepFrom = Math.Max(0, (int)Math.Floor(next) - _halfWidth);
        if (keepFrom > 0)
        {
            Array.Copy(_buf, keepFrom, _buf, 0, _len - keepFrom);
            _len -= keepFrom;
            _dropped += keepFrom;
        }
        return outp.ToArray();
    }

    /// Average interleaved channels into mono.
    public static float[] Downmix(ReadOnlySpan<float> interleaved, int channels)
    {
        if (channels <= 1) return interleaved.ToArray();
        int frames = interleaved.Length / channels;
        var mono = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            float s = 0;
            for (int c = 0; c < channels; c++) s += interleaved[f * channels + c];
            mono[f] = s / channels;
        }
        return mono;
    }
}
