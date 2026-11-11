using System.Threading;

namespace MeetingAssistant.App.Infrastructure;

/// Keeps one app instance per user session. A second launch signals the first one (which brings its
/// main window to the front) and exits. Mac: macOS does this for every app.
public sealed class SingleInstance : IDisposable
{
    const string MutexName = @"Local\de.local.MeetingScribe.MeetingAssistant";
    const string EventName = @"Local\de.local.MeetingScribe.MeetingAssistant.Activate";

    readonly Mutex _mutex;
    readonly EventWaitHandle _activate;
    readonly bool _owned;
    RegisteredWaitHandle? _wait;

    public SingleInstance()
    {
        _mutex = new Mutex(true, MutexName, out _owned);
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
    }

    /// True for the first instance.
    public bool IsFirst => _owned;

    /// Second instance: ask the first one to show itself.
    public void SignalFirst() => _activate.Set();

    /// First instance: call <paramref name="onActivate"/> (on a thread-pool thread) whenever a later launch signals.
    public void Listen(Action onActivate)
        => _wait = ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) => onActivate(), null, Timeout.Infinite, false);

    public void Dispose()
    {
        _wait?.Unregister(null);
        if (_owned) { try { _mutex.ReleaseMutex(); } catch (ApplicationException) { } }
        _mutex.Dispose();
        _activate.Dispose();
    }
}
