using MeetingAssistant.Core.Input;
using MeetingAssistant.Core.Json;
using Xunit;

namespace MeetingAssistant.Core.Tests.Capture;

public sealed class HotKeyTests
{
    static HotKeyEvent Down(HotKeyMatcher m, int vk) => m.Feed(vk, true, out _);
    static HotKeyEvent Up(HotKeyMatcher m, int vk) => m.Feed(vk, false, out _);

    [Fact]
    public void DefaultsAreRightCtrlAndRightCtrlPlusRightShift()
    {
        Assert.Equal("Right Ctrl", HotKey.DefaultPlain.Display());
        Assert.Equal("Right Ctrl + Right Shift", HotKey.DefaultPlus.Display());
        Assert.True(HotKey.DefaultPlain.IsSubsetOf(HotKey.DefaultPlus));
        Assert.False(HotKey.DefaultPlus.IsSubsetOf(HotKey.DefaultPlain));
    }

    [Fact]
    public void JsonShapeMatchesTheMacModel()
    {
        Assert.Equal("{\"keyCode\":163,\"modifiers\":0,\"modifierOnly\":true,\"modifierKeys\":[163,161]}",
            System.Text.Json.JsonSerializer.Serialize(HotKey.DefaultPlus));
        var single = JsonIO.Deserialize<HotKey>("{\"keyCode\":163,\"modifiers\":0,\"modifierOnly\":true}");
        Assert.Equal(HotKey.DefaultPlain, single with { ModifierKeys = null });
        Assert.Null(single.ModifierKeys);
    }

    [Fact]
    public void ModifierKeysAreOrdered()
    {
        var hk = HotKey.FromModifiers(new[] { Vk.RWin, Vk.RShift, Vk.LControl });
        Assert.Equal(new[] { Vk.LControl, Vk.RShift, Vk.RWin }, hk.HeldKeys);
        Assert.Equal(Vk.LControl, hk.KeyCode);
        Assert.Equal("Left Ctrl + Right Shift + Right Win", hk.Display());
    }

    [Fact]
    public void ComboDisplay()
    {
        Assert.Equal("Ctrl+Alt+Space", HotKey.Combo(Vk.Space, HotKey.ModControl | HotKey.ModAlt).Display());
        Assert.Equal("F13", HotKey.Combo(Vk.F1 + 12, 0).Display());
        Assert.Equal("Ctrl+Shift+Win+D", HotKey.Combo(0x44, HotKey.ModWin | HotKey.ModShift | HotKey.ModControl).Display());
        Assert.Equal("Alt+Ö", HotKey.Combo(0xC0, HotKey.ModAlt).Display(vk => vk == 0xC0 ? "ö" : null));
        Assert.Equal("Ctrl+Alt+Space is already used by another app. Choose a different shortcut.",
            HotKey.Combo(Vk.Space, HotKey.ModControl | HotKey.ModAlt).ConflictMessage());
    }

    [Fact]
    public void MaskKeyOnlyForAltAndWin()
    {
        Assert.False(HotKey.DefaultPlain.NeedsMaskKey);
        Assert.True(HotKey.FromModifiers(new[] { Vk.RMenu }).NeedsMaskKey);
        Assert.True(HotKey.Combo(Vk.Space, HotKey.ModWin).NeedsMaskKey);
        Assert.False(HotKey.Combo(Vk.Space, HotKey.ModControl).NeedsMaskKey);
    }

    [Fact]
    public void ModifierOnlyPressReleaseAndRepeat()
    {
        var m = new HotKeyMatcher(HotKey.DefaultPlain);
        Assert.Equal(HotKeyEvent.Press, Down(m, Vk.RControl));
        Assert.Equal(HotKeyEvent.None, Down(m, Vk.RControl));          // key repeat
        Assert.Equal(HotKeyEvent.None, Down(m, Vk.RShift));            // other modifiers may be down
        Assert.Equal(HotKeyEvent.Release, Up(m, Vk.RControl));
        Assert.Equal(HotKeyEvent.None, Up(m, Vk.RShift));
        Assert.Equal(HotKeyEvent.None, Down(m, Vk.LControl));          // left/right specific
    }

    [Fact]
    public void ModifierOnlyNeedsAllKeysAndReleasesOnAnyUp()
    {
        var m = new HotKeyMatcher(HotKey.DefaultPlus);
        Assert.Equal(HotKeyEvent.None, Down(m, Vk.RControl));
        Assert.Equal(HotKeyEvent.Press, Down(m, Vk.RShift));
        Assert.Equal(HotKeyEvent.Release, Up(m, Vk.RShift));
        Assert.Equal(HotKeyEvent.None, Up(m, Vk.RControl));
    }

    [Fact]
    public void PlainThenPlusUpgradeSequence()
    {
        var plain = new HotKeyMatcher(HotKey.DefaultPlain);
        var plus = new HotKeyMatcher(HotKey.DefaultPlus);
        var events = new List<string>();
        void Feed(int vk, bool down)
        {
            var a = plain.Feed(vk, down, out _); if (a != HotKeyEvent.None) events.Add("plain " + a);
            var b = plus.Feed(vk, down, out _); if (b != HotKeyEvent.None) events.Add("plus " + b);
        }
        Feed(Vk.RControl, true);
        Feed(Vk.RShift, true);
        Feed(Vk.RShift, false);
        Feed(Vk.RControl, false);
        Assert.Equal(new[] { "plain Press", "plus Press", "plus Release", "plain Release" }, events);
    }

    [Fact]
    public void OtherKeyWhileHeldCancelsWithoutSwallowing()
    {
        var m = new HotKeyMatcher(HotKey.DefaultPlain);
        Down(m, Vk.RControl);
        Assert.Equal(HotKeyEvent.Cancel, m.Feed(0x43, true, out var swallow));   // Ctrl+C still works
        Assert.False(swallow);
        Assert.False(m.IsDown);
        Assert.Equal(HotKeyEvent.None, Up(m, Vk.RControl));                       // no release after cancel
        Assert.Equal(HotKeyEvent.Press, Down(m, Vk.RControl));
    }

    [Fact]
    public void ComboNeedsExactModifiersAndSwallowsItsKey()
    {
        var m = new HotKeyMatcher(HotKey.Combo(Vk.Space, HotKey.ModControl | HotKey.ModAlt));
        Down(m, Vk.LControl);
        Assert.Equal(HotKeyEvent.None, m.Feed(Vk.Space, true, out var s0));
        Assert.False(s0);
        m.Feed(Vk.Space, false, out _);
        Down(m, Vk.RMenu);                                                         // left/right not distinguished
        Assert.Equal(HotKeyEvent.Press, m.Feed(Vk.Space, true, out var s1));
        Assert.True(s1);
        Assert.Equal(HotKeyEvent.None, m.Feed(Vk.Space, true, out var s2));       // repeat
        Assert.True(s2);
        Up(m, Vk.RMenu);                                                           // released by the key, not the modifier
        Assert.True(m.IsDown);
        Assert.Equal(HotKeyEvent.Release, m.Feed(Vk.Space, false, out var s3));
        Assert.True(s3);
        Down(m, Vk.RMenu); Down(m, Vk.LShift);
        Assert.Equal(HotKeyEvent.None, m.Feed(Vk.Space, true, out _));            // extra Shift: no match
    }

    [Fact]
    public void FunctionKeyAlone()
    {
        var m = new HotKeyMatcher(HotKey.Combo(Vk.F1 + 7, 0));
        Assert.Equal(HotKeyEvent.Press, Down(m, Vk.F1 + 7));
        Assert.Equal(HotKeyEvent.Release, Up(m, Vk.F1 + 7));
    }

    [Fact]
    public void ChangingTheShortcutWhileHeldCancels()
    {
        var m = new HotKeyMatcher(HotKey.DefaultPlain);
        Down(m, Vk.RControl);
        Assert.Equal(HotKeyEvent.Cancel, m.Set(null));
        Assert.Equal(HotKeyEvent.None, Down(m, Vk.RControl));
    }

    [Fact]
    public void RecordingAShortcut()
    {
        var r = new ShortcutRecording();
        Assert.Equal(ShortcutRecordingResult.Beep, r.Feed(0x41, true));            // A alone
        Assert.Equal(ShortcutRecordingResult.Waiting, r.Feed(0x41, false));
        Assert.Equal(ShortcutRecordingResult.Waiting, r.Feed(Vk.LControl, true));
        Assert.Equal(ShortcutRecordingResult.Waiting, r.Feed(Vk.LMenu, true));
        Assert.Equal(ShortcutRecordingResult.Saved, r.Feed(Vk.Space, true));
        Assert.Equal(HotKey.Combo(Vk.Space, HotKey.ModControl | HotKey.ModAlt), r.Result);

        r = new ShortcutRecording();
        r.Feed(Vk.RShift, true);
        r.Feed(Vk.RControl, true);
        Assert.Equal(ShortcutRecordingResult.Waiting, r.Feed(Vk.RShift, false));
        Assert.Equal(ShortcutRecordingResult.Saved, r.Feed(Vk.RControl, false));
        Assert.Equal(HotKey.DefaultPlus, r.Result);

        Assert.Equal(ShortcutRecordingResult.Cancelled, new ShortcutRecording().Feed(Vk.Escape, true));
        r = new ShortcutRecording();
        Assert.Equal(ShortcutRecordingResult.Saved, r.Feed(Vk.F1 + 4, true));
        Assert.Equal("F5", r.Result!.Display());
    }

    [Fact]
    public void SameFieldRules()
    {
        var a = new FieldIdentity(10, new[] { 42, 1 }, 50004, (100, 200));
        Assert.True(a.IsSameField(a with { }));
        Assert.False(a.IsSameField(a with { ProcessId = 11 }));
        Assert.True(a.IsSameField(a with { RuntimeId = null }));                   // no focused element: app only
        Assert.True(a.IsSameField(a with { RuntimeId = new[] { 42, 2 } }));         // rebuilt object, same place
        Assert.False(a.IsSameField(a with { RuntimeId = new[] { 42, 2 }, Position = (100, 260) }));
        Assert.False(a.IsSameField(a with { RuntimeId = new[] { 42, 2 }, ControlType = 50020 }));
    }
}
