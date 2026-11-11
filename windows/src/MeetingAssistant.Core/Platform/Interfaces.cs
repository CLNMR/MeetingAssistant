namespace MeetingAssistant.Core.Platform;

/// Secure storage for the Aya API key (Mac: Keychain, service "MeetingAssistant", account
/// "aya-api-key"; Windows: Credential Manager, target "MeetingAssistant/aya-api-key").
public interface ISecretStore
{
    string? Load(string account);
    void Save(string account, string? value);
}

/// Keeps secrets in memory only (tests, msctl with an environment variable).
public sealed class MemorySecretStore : ISecretStore
{
    readonly Dictionary<string, string> _values = new();
    public string? Load(string account) => _values.TryGetValue(account, out var v) ? v : null;
    public void Save(string account, string? value)
    {
        if (string.IsNullOrEmpty(value)) _values.Remove(account); else _values[account] = value;
    }
}

/// A source of 16 kHz mono float samples (mic or call audio). Implemented with WASAPI on Windows
/// and by file/fake sources in tests and msctl.
public interface IAudioSource : IDisposable
{
    /// Raised on a capture thread with 16 kHz mono samples.
    event Action<float[]>? Samples;
    /// Raised when the device fails or disappears; the recording continues with what it has.
    event Action<string>? Failed;
    void Start();
    void Stop();
}
