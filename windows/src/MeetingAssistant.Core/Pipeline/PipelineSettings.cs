namespace MeetingAssistant.Core.Pipeline;

/// ASR engine of the final transcript (spec 01-recording, "ASR engines"). The raw value is
/// written to `transcript.json` as `engine`.
public enum AsrEngine { ParakeetUltra, Qwen3Large }

/// Spoken language setting: `auto`, `german`, `english`.
public enum TranscriptLanguage { Auto, German, English }

public static class AsrEngineText
{
    public static string Raw(this AsrEngine e) => e == AsrEngine.Qwen3Large ? "qwen3-1.7b" : "parakeet-ultra";

    /// Parses a raw engine id; unknown ids give Parakeet.
    public static AsrEngine ParseEngine(string? raw) => raw == "qwen3-1.7b" ? AsrEngine.Qwen3Large : AsrEngine.ParakeetUltra;

    public static string Title(this AsrEngine e) => e == AsrEngine.Qwen3Large
        ? "Qwen3-ASR 1.7B — slower, 30 languages incl. Asian"
        : "Parakeet Ultra — fast, 25 European languages (recommended)";

    /// Longest chunk (s) the engine handles well.
    public static double MaxChunkSeconds(this AsrEngine e) => e == AsrEngine.Qwen3Large ? 28 : 14;

    /// Windows: only Parakeet is available (sherpa-onnx has no Qwen3 forced aligner); a Qwen3
    /// request runs Parakeet and the transcript records `parakeet-ultra`.
    public static bool AvailableOnWindows(this AsrEngine e) => e == AsrEngine.ParakeetUltra;

    public static string Raw(this TranscriptLanguage l) => l switch
    {
        TranscriptLanguage.German => "german",
        TranscriptLanguage.English => "english",
        _ => "auto",
    };

    /// Parses `auto`/`german`/`english`; null for anything else.
    public static TranscriptLanguage? ParseLanguage(string? raw) => raw switch
    {
        "auto" => TranscriptLanguage.Auto,
        "german" => TranscriptLanguage.German,
        "english" => TranscriptLanguage.English,
        _ => null,
    };

    public static string Title(this TranscriptLanguage l) => l switch
    {
        TranscriptLanguage.German => "German",
        TranscriptLanguage.English => "English",
        _ => "Automatic",
    };
}

/// Settings of one offline transcription run (spec 01-recording, "Settings used").
public sealed record PipelineSettings
{
    public AsrEngine Engine { get; init; } = AsrEngine.ParakeetUltra;
    public TranscriptLanguage Language { get; init; } = TranscriptLanguage.Auto;
    /// Label for the user; empty becomes "Me".
    public string MyName { get; init; } = "Me";
    /// Cosine needed to call a cluster "me" via the voiceprint.
    public float MeThreshold { get; init; } = 0.50f;
    /// Cosine needed to name a cluster from the speaker library.
    public float LibraryThreshold { get; init; } = 0.55f;
    /// The best match must beat the runner-up by this much.
    public float MatchMargin { get; init; } = 0.08f;
    /// Clusters at least this similar are always merged.
    public float AutoMergeThreshold { get; init; } = 0.80f;

    /// `MyName` trimmed, "Me" when empty.
    public string EffectiveName => string.IsNullOrWhiteSpace(MyName) ? "Me" : MyName.Trim();
}
