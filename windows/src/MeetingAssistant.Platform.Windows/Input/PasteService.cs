using System.Runtime.InteropServices;
using MeetingAssistant.Core.Input;
using MeetingAssistant.Platform.Windows.Interop;

namespace MeetingAssistant.Platform.Windows.Input;

/// What happened to a dictation's text (spec 04 §3.1).
public enum DeliveryOutcome
{
    /// Put on the clipboard and Ctrl+V sent.
    Pasted,
    /// The cursor moved to another field: only copied.
    CopiedFieldChanged,
    /// The focused window runs elevated (UIPI blocks SendInput): only copied.
    CopiedElevatedWindow,
    /// The clipboard could not be opened.
    ClipboardFailed,
}

/// Clipboard text via Win32 (retries while another app holds the clipboard).
public static class ClipboardText
{
    /// Replace the clipboard with <paramref name="text"/>. 10 attempts 20 ms apart. The previous
    /// content is not restored afterwards (the text stays there so it can be pasted again).
    public static bool Set(string text)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            if (TrySet(text)) return true;
            Thread.Sleep(20);
        }
        return false;
    }

    static bool TrySet(string text)
    {
        if (!Native.OpenClipboard(0)) return false;
        try
        {
            Native.EmptyClipboard();
            var bytes = (text.Length + 1) * 2;
            var h = Native.GlobalAlloc(Native.GMEM_MOVEABLE, (nuint)bytes);
            if (h == 0) return false;
            var p = Native.GlobalLock(h);
            if (p == 0) { Native.GlobalFree(h); return false; }
            Marshal.Copy(text.ToCharArray(), 0, p, text.Length);
            Marshal.WriteInt16(p, text.Length * 2, 0);
            Native.GlobalUnlock(h);
            if (Native.SetClipboardData(Native.CF_UNICODETEXT, h) == 0)
            {
                Native.GlobalFree(h);   // ownership stays with us on failure
                return false;
            }
            return true;
        }
        finally
        {
            Native.CloseClipboard();
        }
    }
}

/// Pastes dictation text into the focused app (spec 04 §3.1, Windows notes).
public static class PasteService
{
    /// Toasts for the copy-only cases (Windows wording: Ctrl+V instead of ⌘V).
    public const string FieldChangedToast = "Copied – the cursor moved to another field. Paste with Ctrl+V.";
    public const string ElevatedToast = "Copied – that window runs as administrator. Paste with Ctrl+V.";
    public const string ClipboardFailedToast = "Could not use the clipboard – try again";

    /// Poll interval and maximum number of polls while waiting for the shortcut's modifiers to be
    /// released (20 ms × 75 = 1.5 s); otherwise Ctrl+V would become e.g. Ctrl+Shift+V.
    public const int ModifierPollMs = 20, ModifierPolls = 75;

    /// Delivers <paramref name="text"/>: copy-only when the field changed since the shortcut was
    /// pressed or when the window is elevated; otherwise waits for the modifiers to be released, puts
    /// the text on the clipboard and sends Ctrl+V. <paramref name="now"/> may be null (read here).
    public static async Task<DeliveryOutcome> DeliverAsync(string text, FieldIdentity? start, FieldIdentity? now = null)
    {
        if (start is not null)
        {
            now ??= (await FocusedFieldReader.Shared.ReadAsync(readText: false))?.Identity;
            if (now is not null && !start.IsSameField(now))
                return ClipboardText.Set(text) ? DeliveryOutcome.CopiedFieldChanged : DeliveryOutcome.ClipboardFailed;
        }
        if (IsForegroundElevatedAboveUs())
            return ClipboardText.Set(text) ? DeliveryOutcome.CopiedElevatedWindow : DeliveryOutcome.ClipboardFailed;
        await WaitForModifiersReleasedAsync();
        if (!ClipboardText.Set(text)) return DeliveryOutcome.ClipboardFailed;
        SendCtrlV();
        return DeliveryOutcome.Pasted;
    }

    /// Waits until Shift, Ctrl, Alt and both Win keys are up (max 1.5 s).
    public static async Task WaitForModifiersReleasedAsync()
    {
        for (int i = 0; i < ModifierPolls; i++)
        {
            if (!AnyModifierDown()) return;
            await Task.Delay(ModifierPollMs);
        }
    }

    public static bool AnyModifierDown()
    {
        foreach (var vk in new[] { Vk.Shift, Vk.Control, Vk.Menu, Vk.LWin, Vk.RWin })
            if ((Native.GetAsyncKeyState(vk) & 0x8000) != 0) return true;
        return false;
    }

    /// Ctrl down, V down, V up, Ctrl up.
    public static void SendCtrlV()
    {
        const ushort V = 0x56;
        Native.Send(
            Native.Key(Vk.LControl, up: false),
            Native.Key(V, up: false),
            Native.Key(V, up: true),
            Native.Key(Vk.LControl, up: true));
    }

    /// True when the foreground window belongs to a process with a higher integrity level than this
    /// one (an elevated app while we are not): SendInput is silently blocked by UIPI there.
    public static bool IsForegroundElevatedAboveUs()
    {
        var hwnd = Native.GetForegroundWindow();
        if (hwnd == 0) return false;
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0 || pid == (uint)Environment.ProcessId) return false;
        var ours = IntegrityLevel(Native.GetCurrentProcess());
        var proc = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (proc == 0) return false;
        try
        {
            var theirs = IntegrityLevel(proc);
            if (ours is null) return false;
            // A token we may not even query belongs to an elevated (or system) process when we are not elevated.
            if (theirs is null) return ours < HighIntegrity;
            return theirs > ours;
        }
        finally { Native.CloseHandle(proc); }
    }

    const int HighIntegrity = 0x3000;

    static int? IntegrityLevel(nint process)
    {
        if (!Native.OpenProcessToken(process, Native.TOKEN_QUERY, out var token)) return null;
        try
        {
            Native.GetTokenInformation(token, Native.TokenIntegrityLevel, 0, 0, out int size);
            if (size <= 0) return null;
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                if (!Native.GetTokenInformation(token, Native.TokenIntegrityLevel, buf, size, out _)) return null;
                var sid = Marshal.ReadIntPtr(buf);   // TOKEN_MANDATORY_LABEL.Label.Sid
                int count = Marshal.ReadByte(Native.GetSidSubAuthorityCount(sid));
                return Marshal.ReadInt32(Native.GetSidSubAuthority(sid, (uint)(count - 1)));
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally { Native.CloseHandle(token); }
    }
}
