using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using MeetingAssistant.Core.Input;
using MeetingAssistant.Platform.Windows.Interop;

namespace MeetingAssistant.Platform.Windows.Input;

/// What the app can see of the field a dictation goes into (spec 04 §3.2).
/// <param name="App">Display name of the foreground app (FileDescription, else process name).</param>
/// <param name="Identity">For the same-field check at paste time.</param>
/// <param name="Before">Up to 2,000 characters before the selection, or null when unknown.</param>
/// <param name="After">Up to 600 characters after the selection, or null when unknown.</param>
public sealed record FocusedField(string? App, FieldIdentity Identity, string? Before, string? After, bool IsPassword);

/// Reads the focused element with UI Automation (COM `IUIAutomation`, raw vtable calls so no interop
/// assembly is needed) on a dedicated MTA thread, with UIA connection/transaction timeouts of 0.25 s
/// (spec: "each accessibility call has a 0.25 s timeout"). Password fields are never read.
/// Text: TextPattern selection; `before` = selection start moved back ≤ 2,000 characters, `after` =
/// selection end moved forward ≤ 600 characters (the selected text itself is excluded). Without a
/// TextPattern or selection, before/after stay null (fit is skipped, no context is sent).
public sealed unsafe class FocusedFieldReader : IDisposable
{
    public const int BeforeLimit = 2_000, AfterLimit = 600;
    public const uint TimeoutMs = 250;

    static readonly Lazy<FocusedFieldReader> _shared = new(() => new FocusedFieldReader());
    /// The app-wide reader.
    public static FocusedFieldReader Shared => _shared.Value;

    // CLSIDs / IIDs (UIAutomationClient.idl).
    static readonly Guid CLSID_CUIAutomation8 = new("e22ad333-b25f-460c-83d0-0581107395c9");
    static readonly Guid CLSID_CUIAutomation = new("ff48dba4-60ef-4201-aa87-54103eef594e");
    static readonly Guid IID_IUIAutomation2 = new("34723aff-0c9d-49d0-9896-7ab52df8cd8a");
    static readonly Guid IID_IUIAutomation = new("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee");
    static readonly Guid IID_IUIAutomationTextPattern = new("32eba289-3583-42c9-9c59-3b6d9a1e9b6a");
    const int UIA_TextPatternId = 10014;
    const int TextUnit_Character = 0, Endpoint_Start = 0, Endpoint_End = 1;

    // Vtable slots = 3 (IUnknown) + method index in the IDL.
    const int IUIAutomation_GetFocusedElement = 3 + 5;
    const int IUIAutomation2_put_ConnectionTimeout = 3 + 55 + 3;
    const int IUIAutomation2_put_TransactionTimeout = 3 + 55 + 5;
    const int Element_GetRuntimeId = 3 + 1;
    const int Element_GetCurrentPatternAs = 3 + 11;
    const int Element_CurrentProcessId = 3 + 17;
    const int Element_CurrentControlType = 3 + 18;
    const int Element_CurrentIsPassword = 3 + 32;
    const int Element_CurrentBoundingRectangle = 3 + 40;
    const int TextPattern_GetSelection = 3 + 2;
    const int RangeArray_Length = 3 + 0;
    const int RangeArray_GetElement = 3 + 1;
    const int Range_Clone = 3 + 0;
    const int Range_GetText = 3 + 9;
    const int Range_MoveEndpointByUnit = 3 + 11;
    const int Range_MoveEndpointByRange = 3 + 12;

    readonly BlockingCollection<Action> _work = new();
    readonly Thread _thread;
    nint _uia;

    FocusedFieldReader()
    {
        _thread = new Thread(Worker) { IsBackground = true, Name = "UIAutomation" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    void Worker()
    {
        Native.CoInitializeEx(0, Native.COINIT_MULTITHREADED);
        if (Native.CoCreateInstance(CLSID_CUIAutomation8, 0, Native.CLSCTX_INPROC_SERVER, IID_IUIAutomation2, out var uia2) >= 0 && uia2 != 0)
        {
            _uia = uia2;
            Call(_uia, IUIAutomation2_put_ConnectionTimeout, TimeoutMs);
            Call(_uia, IUIAutomation2_put_TransactionTimeout, TimeoutMs);
        }
        else if (Native.CoCreateInstance(CLSID_CUIAutomation, 0, Native.CLSCTX_INPROC_SERVER, IID_IUIAutomation, out var uia) >= 0)
        {
            _uia = uia;
        }
        foreach (var job in _work.GetConsumingEnumerable())
        {
            try { job(); } catch { /* each job reports its own result */ }
        }
        Native.Release(_uia);
    }

    /// Reads the focused field of the foreground app; text only with <paramref name="readText"/>.
    /// Returns null when there is no foreground window. Never throws.
    public Task<FocusedField?> ReadAsync(bool readText)
    {
        var tcs = new TaskCompletionSource<FocusedField?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_work.IsAddingCompleted) { tcs.SetResult(null); return tcs.Task; }
        _work.Add(() =>
        {
            try { tcs.SetResult(Read(readText)); }
            catch (Exception) { tcs.TrySetResult(null); }
        });
        // Safety net on top of the UIA timeouts: a hung provider must not block the dictation.
        return tcs.Task.WaitAsync(TimeSpan.FromSeconds(2)).ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : null);
    }

    FocusedField? Read(bool readText)
    {
        var hwnd = Native.GetForegroundWindow();
        if (hwnd == 0) return null;
        Native.GetWindowThreadProcessId(hwnd, out var pidU);
        int pid = (int)pidU;
        string? app = AppName(pid);
        var noElement = new FocusedField(app, new FieldIdentity(pid, null, null, null), null, null, false);
        if (_uia == 0) return noElement;

        nint el = 0;
        if (Call(_uia, IUIAutomation_GetFocusedElement, &el) < 0 || el == 0) return noElement;
        try
        {
            int[]? runtimeId = RuntimeId(el);
            int controlType = 0;
            int? ct = Call(el, Element_CurrentControlType, &controlType) >= 0 ? controlType : null;
            RECT r;
            (int, int)? pos = Call(el, Element_CurrentBoundingRectangle, &r) >= 0 ? (r.left, r.top) : null;
            var identity = new FieldIdentity(pid, runtimeId, ct, pos);
            int isPassword = 0;
            Call(el, Element_CurrentIsPassword, &isPassword);
            if (!readText || isPassword != 0) return new FocusedField(app, identity, null, null, isPassword != 0);
            var (before, after) = TextAround(el);
            return new FocusedField(app, identity, before, after, false);
        }
        finally { Native.Release(el); }
    }

    static (string?, string?) TextAround(nint el)
    {
        nint pattern = 0, ranges = 0, sel = 0, b = 0, a = 0;
        try
        {
            var iid = IID_IUIAutomationTextPattern;
            if (CallPattern(el, UIA_TextPatternId, &iid, &pattern) < 0 || pattern == 0) return (null, null);
            if (Call(pattern, TextPattern_GetSelection, &ranges) < 0 || ranges == 0) return (null, null);
            int count = 0;
            if (Call(ranges, RangeArray_Length, &count) < 0 || count < 1) return (null, null);
            if (CallIndex(ranges, RangeArray_GetElement, 0, &sel) < 0 || sel == 0) return (null, null);

            // before: collapse a copy to the selection start, then move its start back.
            if (Call(sel, Range_Clone, &b) < 0 || b == 0) return (null, null);
            MoveByRange(b, Endpoint_End, sel, Endpoint_Start);
            MoveByUnit(b, Endpoint_Start, -BeforeLimit);
            var before = GetText(b, -1);
            // after: collapse a copy to the selection end, then move its end forward.
            if (Call(sel, Range_Clone, &a) < 0 || a == 0) return (null, null);
            MoveByRange(a, Endpoint_Start, sel, Endpoint_End);
            MoveByUnit(a, Endpoint_End, AfterLimit);
            var after = GetText(a, AfterLimit);
            if (before is null || after is null) return (null, null);
            if (before.Length > BeforeLimit) before = before[^BeforeLimit..];
            if (after.Length > AfterLimit) after = after[..AfterLimit];
            return (before, after);
        }
        finally
        {
            Native.Release(a); Native.Release(b); Native.Release(sel); Native.Release(ranges); Native.Release(pattern);
        }
    }

    static int[]? RuntimeId(nint el)
    {
        nint sa = 0;
        if (Call(el, Element_GetRuntimeId, &sa) < 0 || sa == 0) return null;
        try
        {
            if (Native.SafeArrayGetLBound(sa, 1, out var lo) < 0 || Native.SafeArrayGetUBound(sa, 1, out var hi) < 0) return null;
            int n = hi - lo + 1;
            if (n <= 0) return Array.Empty<int>();
            if (Native.SafeArrayAccessData(sa, out var data) < 0) return null;
            try { return new ReadOnlySpan<int>((void*)data, n).ToArray(); }
            finally { Native.SafeArrayUnaccessData(sa); }
        }
        finally { Native.SafeArrayDestroy(sa); }
    }

    static string? GetText(nint range, int max)
    {
        nint bstr = 0;
        if (CallIndex(range, Range_GetText, max, &bstr) < 0) return null;
        if (bstr == 0) return "";
        try { return Marshal.PtrToStringBSTR(bstr); }
        finally { Marshal.FreeBSTR(bstr); }
    }

    static void MoveByRange(nint range, int src, nint other, int target)
    {
        var vt = *(nint**)range;
        ((delegate* unmanaged[Stdcall]<nint, int, nint, int, int>)vt[Range_MoveEndpointByRange])(range, src, other, target);
    }

    static void MoveByUnit(nint range, int endpoint, int count)
    {
        int moved;
        var vt = *(nint**)range;
        ((delegate* unmanaged[Stdcall]<nint, int, int, int, int*, int>)vt[Range_MoveEndpointByUnit])(range, endpoint, TextUnit_Character, count, &moved);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int left, top, right, bottom; }

    static int Call(nint obj, int slot, void* outArg)
    {
        var vt = *(nint**)obj;
        return ((delegate* unmanaged[Stdcall]<nint, void*, int>)vt[slot])(obj, outArg);
    }

    static int Call(nint obj, int slot, uint value)
    {
        var vt = *(nint**)obj;
        return ((delegate* unmanaged[Stdcall]<nint, uint, int>)vt[slot])(obj, value);
    }

    static int CallIndex(nint obj, int slot, int index, void* outArg)
    {
        var vt = *(nint**)obj;
        return ((delegate* unmanaged[Stdcall]<nint, int, void*, int>)vt[slot])(obj, index, outArg);
    }

    static int CallPattern(nint el, int patternId, Guid* iid, nint* outArg)
    {
        var vt = *(nint**)el;
        return ((delegate* unmanaged[Stdcall]<nint, int, Guid*, nint*, int>)vt[Element_GetCurrentPatternAs])(el, patternId, iid, outArg);
    }

    static string? AppName(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            string? desc = null;
            try { desc = p.MainModule?.FileVersionInfo.FileDescription; } catch (System.ComponentModel.Win32Exception) { }
            return string.IsNullOrWhiteSpace(desc) ? p.ProcessName : desc.Trim();
        }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    public void Dispose() => _work.CompleteAdding();
}
