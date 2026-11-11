using MeetingAssistant.Core.Audio;

namespace MeetingAssistant.Core.Capture;

/// A source whose buffers are pushed by code (tests, and adapters around other capture APIs).
public sealed class ManualAudioSource : ITimedAudioSource
{
    public event Action<float[]>? Samples;
    public event Action<float[], double>? TimedSamples;
    public event Action<string>? Failed;
    public bool IsRunning { get; private set; }
    /// Make <see cref="Start"/> throw (simulates a device that cannot be opened).
    public Exception? FailOnStart { get; set; }
    public int StartCount { get; private set; }
    public int StopCount { get; private set; }

    public void Start()
    {
        StartCount++;
        if (FailOnStart is not null) throw FailOnStart;
        IsRunning = true;
    }

    public void Stop() { StopCount++; IsRunning = false; }

    /// Deliver a buffer captured at host-clock time <paramref name="time"/> (ignored while stopped).
    public void Push(float[] samples, double time)
    {
        if (!IsRunning) return;
        Samples?.Invoke(samples);
        TimedSamples?.Invoke(samples, time);
    }

    /// Report a device failure.
    public void Fail(string message) => Failed?.Invoke(message);

    public void Dispose() => Stop();
}

/// Plays an audio file (WAV or CAF) into the recorder in real time (or faster), in 100 ms blocks,
/// stamping each block with a time on the given clock. Used by `msctl record` and tests.
public sealed class FileAudioSource : ITimedAudioSource
{
    readonly float[] _samples;
    readonly Func<double> _clock;
    readonly double _startDelay;
    readonly int _block;
    Thread? _thread;
    volatile bool _stop;

    public event Action<float[]>? Samples;
    public event Action<float[], double>? TimedSamples;
    public event Action<string>? Failed;

    /// Raised once when the whole file was delivered.
    public event Action? Finished;

    /// <param name="path">Audio file, read with <see cref="AudioFile.Read"/>.</param>
    /// <param name="clock">The clock the recorder uses; blocks are delivered in that clock's time
    /// (pass a <see cref="ScaledClock"/> to feed faster than real time).</param>
    /// <param name="startDelay">Seconds of clock time before the first block (simulates a late device).</param>
    public FileAudioSource(string path, Func<double> clock, double startDelay = 0, int blockSamples = 1600)
        : this(AudioFile.Read(path), clock, startDelay, blockSamples) { }

    public FileAudioSource(float[] samples, Func<double> clock, double startDelay = 0, int blockSamples = 1600)
    {
        _samples = samples;
        _clock = clock;
        _startDelay = Math.Max(0, startDelay);
        _block = Math.Max(1, blockSamples);
    }

    public double DurationSeconds => _samples.Length / (double)AudioFile.SampleRate;

    public void Start()
    {
        _stop = false;
        double t0 = _clock() + _startDelay;
        _thread = new Thread(() => Run(t0)) { IsBackground = true, Name = "FileAudioSource" };
        _thread.Start();
    }

    void Run(double t0)
    {
        try
        {
            for (int pos = 0; pos < _samples.Length && !_stop; pos += _block)
            {
                double due = t0 + (pos + _block) / (double)AudioFile.SampleRate;   // a block is delivered once captured
                while (!_stop && _clock() < due)
                {
                    Thread.Sleep(TimeSpan.FromSeconds(Math.Clamp(due - _clock(), 0.001, 0.02)));
                }
                if (_stop) break;
                var n = Math.Min(_block, _samples.Length - pos);
                var chunk = _samples.AsSpan(pos, n).ToArray();
                Samples?.Invoke(chunk);
                TimedSamples?.Invoke(chunk, t0 + pos / (double)AudioFile.SampleRate);
            }
            if (!_stop) Finished?.Invoke();
        }
        catch (Exception e)
        {
            Failed?.Invoke(e.Message);
        }
    }

    public void Stop()
    {
        _stop = true;
        if (_thread is not null && _thread != Thread.CurrentThread) _thread.Join();
        _thread = null;
    }

    public void Dispose() => Stop();
}

/// A clock that runs <paramref name="speed"/> times faster than the host clock, starting at the
/// host clock's current value (msctl `record --speed`).
public sealed class ScaledClock(double speed)
{
    readonly double _origin = HostClock.Now;
    public double Now() => _origin + (HostClock.Now - _origin) * speed;
}
