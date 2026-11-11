using System.Runtime.InteropServices;
using MeetingAssistant.Core.Input;
using MeetingAssistant.Platform.Windows.Interop;

namespace MeetingAssistant.Platform.Windows.Input;

/// One global shortcut slot (plain or Scribe+). Events are raised on the synchronization context
/// given to <see cref="GlobalHotKeys"/> (the UI thread).
public sealed class GlobalHotKey
{
    readonly GlobalHotKeys _owner;
    internal readonly HotKeyMatcher Matcher = new();
    internal GlobalHotKey(GlobalHotKeys owner) => _owner = owner;

    public event Action? Pressed;
    public event Action? Released;
    /// Another key was typed while a modifier-only shortcut was held, or the shortcut changed while held.
    public event Action? Cancelled;

    public HotKey? HotKey => Matcher.HotKey;
    /// Error under the shortcut in Settings (combination taken by another app), or null.
    public string? RegistrationError { get; private set; }

    /// Use <paramref name="hotKey"/> (null = no shortcut). Returns the registration error, or null.
    public string? Register(HotKey? hotKey)
    {
        RegistrationError = null;
        if (hotKey is { ModifierOnly: false } && !_owner.IsCombinationFree(hotKey))
        {
            RegistrationError = hotKey.ConflictMessage(GlobalHotKeys.KeyChar);
            hotKey = null;
        }
        _owner.SetShortcut(this, hotKey);
        return RegistrationError;
    }

    internal void Raise(HotKeyEvent e)
    {
        switch (e)
        {
            case HotKeyEvent.Press: Pressed?.Invoke(); break;
            case HotKeyEvent.Release: Released?.Invoke(); break;
            case HotKeyEvent.Cancel: Cancelled?.Invoke(); break;
        }
    }
}

/// Global push-to-talk shortcuts with a low-level keyboard hook (spec 04 §3.4 Windows notes): sees
/// key-down and key-up, distinguishes left/right modifiers, works for modifier-only shortcuts and key
/// combinations. The hook runs on its own thread with a message loop and only updates key state and
/// posts events, so it returns well within `LowLevelHooksTimeout`.
/// - Modifier-only shortcuts observe the keys, never block them; another key cancels.
/// - Key combinations swallow their key (like a registered hot key); a combination already registered
///   by another app (checked with RegisterHotKey) is refused with the spec's error text.
/// - Shortcuts with Alt or Win send an unassigned key (VK 0xE8) when they fire, so releasing Alt does
///   not open the menu bar and releasing Win does not open Start.
/// - AltGr's phantom Left Ctrl (scan code 0x21D) is ignored.
public sealed class GlobalHotKeys : IDisposable
{
    const uint WM_MASKKEY = Native.WM_APP + 1;
    readonly SynchronizationContext? _context;
    readonly List<GlobalHotKey> _keys = new();
    readonly object _lock = new();
    readonly Thread _thread;
    readonly ManualResetEventSlim _ready = new();
    uint _threadId;
    nint _hook;
    Native.LowLevelKeyboardProc? _proc;   // kept alive while hooked
    ShortcutRecording? _recording;
    Action<ShortcutRecordingResult, HotKey?>? _recordingDone;
    bool _suspended;
    static int _conflictId = 0x4D00;

    /// <param name="context">Where events are raised (the WPF dispatcher's context); null = thread pool.</param>
    public GlobalHotKeys(SynchronizationContext? context)
    {
        _context = context;
        _thread = new Thread(HookThread) { IsBackground = true, Name = "KeyboardHook" };
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
        if (_hook == 0) throw new InvalidOperationException("Could not install the keyboard hook.");
    }

    /// A new shortcut slot.
    public GlobalHotKey Add()
    {
        var k = new GlobalHotKey(this);
        lock (_lock) _keys.Add(k);
        return k;
    }

    /// While true, shortcuts are ignored (e.g. while the Settings shortcut recorder is open).
    public bool Suspended
    {
        get { lock (_lock) return _suspended; }
        set
        {
            List<(GlobalHotKey, HotKeyEvent)> events = new();
            lock (_lock)
            {
                _suspended = value;
                if (value) foreach (var k in _keys) events.Add((k, k.Matcher.Reset()));
            }
            foreach (var (k, e) in events) if (e != HotKeyEvent.None) Post(() => k.Raise(e));
        }
    }

    /// Record the next shortcut gesture (spec 04 §3.4 "Recording a shortcut"); all keys are swallowed
    /// and shortcuts are suspended meanwhile. <paramref name="done"/> gets Saved (with the shortcut) or
    /// Cancelled; Beep results only beep and keep waiting.
    public void RecordShortcut(Action<ShortcutRecordingResult, HotKey?> done)
    {
        Suspended = true;
        lock (_lock) { _recording = new ShortcutRecording(); _recordingDone = done; }
    }

    /// Stop recording without a result.
    public void CancelRecording()
    {
        Action<ShortcutRecordingResult, HotKey?>? done;
        lock (_lock) { done = _recordingDone; _recording = null; _recordingDone = null; }
        Suspended = false;
        if (done is not null) Post(() => done(ShortcutRecordingResult.Cancelled, null));
    }

    internal void SetShortcut(GlobalHotKey key, HotKey? hotKey)
    {
        HotKeyEvent e;
        lock (_lock) e = key.Matcher.Set(hotKey);
        if (e != HotKeyEvent.None) Post(() => key.Raise(e));
    }

    /// True when no other app has registered this key combination (RegisterHotKey succeeds; it is
    /// unregistered again immediately, the hook does the actual work).
    internal bool IsCombinationFree(HotKey hk)
    {
        int id = Interlocked.Increment(ref _conflictId);
        if (!Native.RegisterHotKey(0, id, (uint)hk.Modifiers | Native.MOD_NOREPEAT, (uint)hk.KeyCode)) return false;
        Native.UnregisterHotKey(0, id);
        return true;
    }

    /// The character a key produces on the current keyboard layout (for display), or null.
    public static string? KeyChar(int vk)
    {
        uint c = Native.MapVirtualKeyW((uint)vk, Native.MAPVK_VK_TO_CHAR) & 0x7FFFFFFF;   // high bit: dead key
        return c is > 32 and < 0xFFFF ? ((char)c).ToString() : null;
    }

    void HookThread()
    {
        _threadId = Native.GetCurrentThreadId();
        _proc = HookProc;
        _hook = Native.SetWindowsHookExW(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandleW(null), 0);
        _ready.Set();
        if (_hook == 0) return;
        while (Native.GetMessageW(out var msg, 0, 0, 0) > 0)
        {
            if (msg.message == WM_MASKKEY)
                Native.Send(Native.Key(Vk.MaskKey, up: false), Native.Key(Vk.MaskKey, up: true));
        }
        Native.UnhookWindowsHookEx(_hook);
        _hook = 0;
    }

    unsafe nint HookProc(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            var k = (Native.KBDLLHOOKSTRUCT*)lParam;
            int msg = (int)wParam;
            bool down = msg is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
            bool up = msg is Native.WM_KEYUP or Native.WM_SYSKEYUP;
            bool ours = (k->flags & Native.LLKHF_INJECTED) != 0 && k->dwExtraInfo == Native.InjectedMarker;
            bool altGrPhantom = k->vkCode == Vk.LControl && (k->scanCode & 0x200) != 0;
            if ((down || up) && !ours && !altGrPhantom && Handle((int)k->vkCode, down)) return 1;
        }
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// Returns true to swallow the key.
    bool Handle(int vk, bool down)
    {
        lock (_lock)
        {
            if (_recording is not null)
            {
                var r = _recording.Feed(vk, down);
                if (r == ShortcutRecordingResult.Beep) Native.MessageBeep(0xFFFFFFFF);
                else if (r is ShortcutRecordingResult.Saved or ShortcutRecordingResult.Cancelled)
                {
                    var done = _recordingDone;
                    var result = _recording.Result;
                    _recording = null; _recordingDone = null;
                    Post(() => { Suspended = false; done?.Invoke(r, r == ShortcutRecordingResult.Saved ? result : null); });
                }
                return true;
            }
            if (_suspended) return false;
            bool swallow = false;
            foreach (var key in _keys)
            {
                var e = key.Matcher.Feed(vk, down, out var sw);
                swallow |= sw;
                if (e == HotKeyEvent.None) continue;
                if (e == HotKeyEvent.Press && key.HotKey?.NeedsMaskKey == true) Native.PostThreadMessageW(_threadId, WM_MASKKEY, 0, 0);
                Post(() => key.Raise(e));
            }
            return swallow;
        }
    }

    void Post(Action a)
    {
        if (_context is not null) _context.Post(_ => a(), null);
        else ThreadPool.QueueUserWorkItem(_ => a());
    }

    public void Dispose()
    {
        if (_threadId != 0) Native.PostThreadMessageW(_threadId, Native.WM_QUIT, 0, 0);
        _thread.Join(TimeSpan.FromSeconds(2));
    }
}
