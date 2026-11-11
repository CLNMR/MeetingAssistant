using System.IO;
using MeetingAssistant.Core.Audio;
using MeetingAssistant.Core.Storage;
using NAudio.Wave;

namespace MeetingAssistant.App.Services;

/// <see cref="IAudioDecoder"/> for imports (spec 01 "Import"): WAV and CAF through Core, every
/// other format (mp3, m4a, aac, wma, mp4…) through Media Foundation, mixed to mono and resampled to
/// 16 kHz. Windows notes: replaces AVFoundation's AVAudioFile on the Mac.
public sealed class MediaFoundationDecoder : IAudioDecoder
{
    static readonly WavCafDecoder Fallback = new();

    public float[] Decode16k(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".wav" or ".caf") return Fallback.Decode16k(path);

        using var reader = new MediaFoundationReader(path);
        var provider = reader.ToSampleProvider();
        int channels = provider.WaveFormat.Channels, rate = provider.WaveFormat.SampleRate;
        var mono = new List<float>(capacity: (int)Math.Min(int.MaxValue, reader.Length / Math.Max(1, reader.WaveFormat.BlockAlign)));
        var buffer = new float[rate * channels];
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i + channels <= read; i += channels)
            {
                float sum = 0;
                for (int c = 0; c < channels; c++) sum += buffer[i + c];
                mono.Add(sum / channels);
            }
        }
        if (mono.Count == 0) throw new InvalidDataException($"No audio in {Path.GetFileName(path)}.");
        var samples = mono.ToArray();
        return rate == 16_000 ? samples : AudioFile.Resample(samples, rate, 16_000);
    }
}
