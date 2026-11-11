using MeetingAssistant.Core.Model;
using MeetingAssistant.Core.Pipeline;

namespace MeetingAssistant.Speech;

/// Voiceprint enrollment (spec 01-recording, "Voiceprint"): VAD speech regions of the sample
/// are concatenated; at least 8 s of speech are needed; embeddings of consecutive 6 s windows
/// (a window is used while at least 3 s remain) are averaged and normalised, then saved as
/// `me-voiceprint.json`.
public static class Enrollment
{
    /// Enroll from 16 kHz mono samples of the user reading aloud. Throws `PipelineException`
    /// with the spec's message when there is too little speech.
    public static Voiceprint Enroll(string name, float[] samples, SpeechEngine? engine = null)
    {
        engine ??= SpeechEngine.Shared;
        var audio = samples.ToArray();
        AudioLevel.Apply(audio, AudioLevel.Gain(audio));
        var speech = new List<float>();
        foreach (var r in SpeechEngine.SpeechRegions(audio)) speech.AddRange(SpeechEngine.Slice(audio, r.Start, r.End));
        double seconds = speech.Count / 16_000.0;
        if (seconds < VoiceprintStore.MinimumSpeechSeconds) throw PipelineException.EnrollmentTooShort(seconds);
        var all = speech.ToArray();
        var embeddings = VoiceprintStore.EnrollmentWindows(all.Length)
            .Select(w => engine.Embedding(all[w.Start..w.End]))
            .Where(e => e.Length > 0)
            .ToList();
        var v = new Voiceprint
        {
            Name = name, Embedding = SpeakerMath.Mean(embeddings), Seconds = seconds,
            CreatedAt = DateTime.UtcNow,
        };
        VoiceprintStore.Save(v);
        return v;
    }
}
