using SherpaOnnx;

namespace MeetingAssistant.Speech;

/// Streaming Silero VAD for live utterance cutting (spec 01-recording, "Per-track utterance
/// cutting"): one speech/non-speech decision per 4096-sample (256 ms) block with carried state.
/// sherpa-onnx does not expose Silero's raw probability, so the block runs as eight 512-sample
/// Silero windows (threshold 0.5, no minimum speech/silence) and counts as speech when any window
/// is in speech; that is the closest equivalent of the Mac's "block probability ≥ 0.5".
public sealed class StreamingVad : IDisposable
{
    public const int BlockSize = 4096;
    const int Window = 512;
    readonly VoiceActivityDetector _vad = SpeechEngine.CreateVad(0f, 0f, 30f, 60f);
    int _sinceReset;

    /// Speech decision for one block of exactly 4096 samples.
    public bool IsSpeech(float[] block)
    {
        bool speech = false;
        for (int i = 0; i + Window <= block.Length; i += Window)
        {
            _vad.AcceptWaveform(block.AsSpan(i, Window).ToArray());
            if (_vad.IsSpeechDetected()) speech = true;
        }
        while (!_vad.IsEmpty()) _vad.Pop();
        // sherpa-onnx keeps the samples of an open segment; reset now and then (at a pause when
        // possible) so its 60 s buffer never overflows during long stretches of speech.
        if (++_sinceReset >= 200 || (_sinceReset >= 100 && !speech)) { _vad.Reset(); _sinceReset = 0; }
        return speech;
    }

    public void Dispose() => _vad.Dispose();
}
