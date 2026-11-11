using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeetingAssistant.Core.Aya;
using MeetingAssistant.Core.Json;

namespace MeetingAssistant.Core.Assist;

/// <summary>How often updates are sent (spec 02 §2.1). More updates react faster but use more of the subscription.</summary>
public enum AssistFrequency { Frequent, Normal, Economical }

/// <summary>Settings of the live assistant (spec 02 §2.1), persisted as JSON under <c>assist.settings.v1</c>.</summary>
public sealed record AssistSettings
{
    /// opus | sonnet | haiku (Claude Code aliases).
    [JsonPropertyName("model")] public string Model { get; set; } = "opus";
    [JsonPropertyName("effort")] public string Effort { get; set; } = "low";
    /// Empty = find automatically.
    [JsonPropertyName("claudePath")] public string ClaudePath { get; set; } = "";
    /// Send when this many new words have arrived …
    [JsonPropertyName("minWords")] public int MinWords { get; set; } = 60;
    /// … and at least this many seconds since the last update.
    [JsonPropertyName("minInterval")] public double MinInterval { get; set; } = 20;
    /// Send any new text after this long.
    [JsonPropertyName("maxInterval")] public double MaxInterval { get; set; } = 60;
    /// Wait this long for a speaker label.
    [JsonPropertyName("labelWait")] public double LabelWait { get; set; } = 3;
    /// Start a fresh conversation above this context size …
    [JsonPropertyName("restartTokens")] public int RestartTokens { get; set; } = 80_000;
    /// … or after this many minutes.
    [JsonPropertyName("restartMinutes")] public double RestartMinutes { get; set; } = 60;

    /// <summary>The preset: minWords ≤ 40 is frequent, ≥ 100 economical, else normal. Setting it writes the three values.</summary>
    [JsonIgnore]
    public AssistFrequency Frequency
    {
        get => MinWords <= 40 ? AssistFrequency.Frequent : MinWords >= 100 ? AssistFrequency.Economical : AssistFrequency.Normal;
        set
        {
            (MinWords, MinInterval, MaxInterval) = value switch
            {
                AssistFrequency.Frequent => (40, 15.0, 45.0),
                AssistFrequency.Normal => (60, 20.0, 60.0),
                _ => (100, 40.0, 120.0),
            };
        }
    }

    public static string FrequencyTitle(AssistFrequency f) => f switch
    {
        AssistFrequency.Frequent => "Frequent",
        AssistFrequency.Normal => "Normal",
        _ => "Economical",
    };

    public static string FrequencyExplanation(AssistFrequency f) => f switch
    {
        AssistFrequency.Frequent => "About every 15–45 s. Fastest reactions, uses the most.",
        AssistFrequency.Normal => "About every 20–60 s.",
        _ => "About every 40–120 s. Uses the least.",
    };

    /// Preferences key of the settings.
    public const string PreferencesKey = "assist.settings.v1";

    public static AssistSettings Load(IPreferenceStore? prefs = null)
    {
        var s = (prefs ?? FilePreferences.Shared).GetString(PreferencesKey);
        if (s is null) return new AssistSettings();
        try { return JsonSerializer.Deserialize<AssistSettings>(s, JsonIO.Options) ?? new AssistSettings(); }
        catch (JsonException) { return new AssistSettings(); }
    }

    public void Save(IPreferenceStore? prefs = null) =>
        (prefs ?? FilePreferences.Shared).SetString(PreferencesKey, JsonIO.Compact(JsonSerializer.SerializeToNode(this, JsonIO.Options)));
}

/// <summary>Engine status (spec 02 §2.2).</summary>
public abstract record AssistStatus
{
    public sealed record Idle : AssistStatus;
    public sealed record Starting : AssistStatus;
    /// Waiting for enough new transcript.
    public sealed record Listening : AssistStatus;
    /// An update is being generated.
    public sealed record Thinking : AssistStatus;
    public sealed record Reconnecting(string Message) : AssistStatus;
    /// Subscription usage limit; retries later.
    public sealed record Limited(string Message) : AssistStatus;
    public sealed record Paused : AssistStatus;
    public sealed record Failed(string Message) : AssistStatus;
    public sealed record Stopped : AssistStatus;

    /// The status text shown to the user.
    public string Text => this switch
    {
        Idle => "Not started",
        Starting => "Starting Claude…",
        Listening => "Listening",
        Thinking => "Thinking…",
        Reconnecting r => $"Reconnecting… {r.Message}",
        Limited l => $"Paused: Claude usage limit reached. {l.Message}",
        Paused => "Paused",
        Failed f => f.Message,
        _ => "Stopped",
    };

    /// Shown in the warning colour (limited, failed, reconnecting).
    public bool IsWarning => this is Limited or Failed or Reconnecting;
}

/// <summary>A transcript line ready for Claude: <c>{start, speaker, text}</c> (spec 02 §2.3).</summary>
public sealed record AssistLine(
    [property: JsonPropertyName("start")] double Start,
    [property: JsonPropertyName("speaker")] string Speaker,
    [property: JsonPropertyName("text")] string Text)
{
    /// <summary><c>[HH:MM:SS] speaker: text</c>, whole seconds since recording start.</summary>
    [JsonIgnore]
    public string Rendered
    {
        get
        {
            var s = (long)Start;
            return string.Format(CultureInfo.InvariantCulture, "[{0:00}:{1:00}:{2:00}] ", s / 3600, s / 60 % 60, s % 60) + $"{Speaker}: {Text}";
        }
    }
}
