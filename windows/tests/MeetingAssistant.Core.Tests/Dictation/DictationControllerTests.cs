using MeetingAssistant.Core.Dictation;
using MeetingAssistant.Core.Text;
using Xunit;

namespace MeetingAssistant.Core.Tests.Dictation;

/// The dictation state machine (spec 04 §1–§3) with fake platform parts.
public sealed class DictationControllerTests
{
    static async Task Done(Task t)
    {
        var finished = await Task.WhenAny(t, Task.Delay(5000));
        Assert.Same(t, finished);
        await t;
    }

    // ---------------- Press, listening, release ----------------

    [Fact]
    public async Task PlainDictationIsCleanedFittedAndPasted()
    {
        using var r = new DictationRig();
        r.Asr.Text = _ => "Wir, ähm, treffen uns um 16 Uhr 45 mit hubspot.";
        r.C.Vocabulary = new Vocabulary { Terms = { "HubSpot" } };
        r.Fields.Before = "Hallo";
        r.Fields.After = "";

        r.C.Press(DictationMode.Plain);
        Assert.Equal(DictationPhase.Listening, r.C.Phase);
        Assert.Equal(DictationMode.Plain, r.C.Mode);
        Assert.True(r.Mic.Current!.Running);
        Assert.Equal(new[] { false }, r.Fields.Reads);           // start field: identity only

        var audio = DictationRig.Speech(1.5);
        r.Feed(audio);
        var release = r.C.ReleaseAsync();
        Assert.Equal(DictationPhase.Transcribing, r.C.Phase);
        Assert.Equal("Transcribing…", r.C.Detail);
        Assert.True(r.Mic.Current!.Running);                     // 200 ms for the last buffer
        r.Clock.Advance(0.19);
        Assert.True(r.Mic.Current!.Running);
        r.Clock.Advance(0.01);
        Assert.True(r.Mic.Current!.Stopped);

        Assert.Equal(audio.Length, r.Asr.Calls[^1].Length);      // the whole recording
        Assert.Equal(new[] { " wir treffen uns um 16:45 Uhr mit HubSpot." }, r.Output.Pasted);
        Assert.Equal(" wir treffen uns um 16:45 Uhr mit HubSpot.", r.C.LastText);
        Assert.Equal(" wir treffen uns um 16:45 Uhr mit HubSpot.", r.C.Preview);   // final text in the HUD
        Assert.Empty(r.KeptFiles);                                // delivered → recovery file gone
        Assert.Equal(DictationPhase.Transcribing, r.C.Phase);
        r.Clock.Advance(0.59);
        Assert.Equal(DictationPhase.Transcribing, r.C.Phase);
        r.Clock.Advance(0.01);                                    // hidden 0.6 s after the paste
        await Done(release);
        Assert.Equal(DictationPhase.Idle, r.C.Phase);
        Assert.Equal("ok", r.C.LastSteps.Outcome);
        Assert.Contains(r.Logs, l => l.StartsWith("dictation plain: audio 2.5 s, asr ") && l.EndsWith(" – ok"));
        Assert.DoesNotContain(r.Logs, l => l.Contains("HubSpot"));   // text is never logged
    }

    [Fact]
    public async Task TapShorterThan300MsDoesNothing()
    {
        using var r = new DictationRig();
        r.Asr.Text = _ => "Hallo";
        r.C.Press(DictationMode.Plain);
        r.Mic.Current!.Push(DictationRig.Speech(0.2, 0, 0));
        r.Clock.Advance(0.29);
        var t = r.C.ReleaseAsync();
        r.Clock.Advance(0.2);
        await Done(t);
        Assert.Equal(DictationPhase.Idle, r.C.Phase);
        Assert.Empty(r.Asr.Calls);
        Assert.Empty(r.Output.Pasted);
        Assert.Empty(r.KeptFiles);
    }

    [Fact]
    public async Task HeldPressAt300MsIsNoTap()
    {
        using var r = new DictationRig();
        r.Asr.Text = _ => "Hallo zusammen.";
        r.C.Press(DictationMode.Plain);
        r.Mic.Current!.Push(DictationRig.Speech(0.25, 0.1, 0.1));
        r.Clock.Advance(0.3);
        var t = r.C.ReleaseAsync();
        r.Clock.Advance(0.2);
        r.Clock.Advance(1);
        await Done(t);
        Assert.Equal(new[] { "Hallo zusammen." }, r.Output.Pasted);
    }

    [Fact]
    public async Task AccidentalPressWithoutSpeechShowsNoSpeech()
    {
        using var r = new DictationRig();
        var t = r.Dictate(DictationRig.Silence(2.9));
        await Done(t);
        Assert.Equal(DictationPhase.Message, r.C.Phase);
        Assert.Equal("No speech detected", r.C.Message);
        Assert.DoesNotContain(r.Asr.Calls, c => c.Length == 46_400);   // no final ASR
        Assert.Empty(r.KeptFiles);                                // no recovery file
        r.Clock.Advance(2.49);
        Assert.Equal(DictationPhase.Message, r.C.Phase);
        r.Clock.Advance(0.01);                                    // toast: 2.5 s
        Assert.Equal(DictationPhase.Idle, r.C.Phase);
    }

    [Fact]
    public async Task SilenceHeldThreeSecondsGoesToAsrAndEmptyTextDeletesTheFile()
    {
        using var r = new DictationRig();
        r.Asr.Text = _ => "";
        var t = r.Dictate(DictationRig.Silence(3.0));
        await Done(t);
        Assert.Equal(48_000, r.Asr.Calls[^1].Length);
        Assert.Equal("No speech detected", r.C.Message);
        Assert.Equal("no speech", r.C.LastSteps.Outcome);
        Assert.Empty(r.KeptFiles);
        Assert.Empty(r.Output.Pasted);
    }

    [Theory]
    [InlineData("Untertitel im Auftrag des ZDF, 2021.")]
    [InlineData("Vielen Dank fürs Zuschauen!")]
    public async Task InventedTextIsNoSpeech(string invented)
    {
        using var r = new DictationRig();
        r.Asr.Text = _ => invented;
        await Done(r.Dictate(DictationRig.Speech(1)));
        Assert.Equal("No speech detected", r.C.Message);
        Assert.Empty(r.Output.Pasted);
    }

    [Fact]
    public async Task DoubtfulPhraseCountsOnlyWithAShortVoicedRun()
    {
        using var r = new DictationRig();
        r.Asr.Text = _ => "Danke.";
        // Voiced 0.2 s (≥ 0.15, so no accident) but < 0.35 s: "Danke" is doubtful → invented.
        await Done(r.Dictate(DictationRig.Speech(0.2, 0.5, 0.5)));
        Assert.Equal("No speech detected", r.C.Message);

        using var r2 = new DictationRig();
        r2.Asr.Text = _ => "Danke.";
        var t = r2.Dictate(DictationRig.Speech(0.6, 0.5, 0.5));
        r2.Clock.Advance(1);
        await Done(t);
        Assert.Equal(new[] { "Danke." }, r2.Output.Pasted);
    }

    [Fact]
    public async Task AsrFailureKeepsTheRecording()
    {
        using var r = new DictationRig();
        r.Asr.Fail = new InvalidOperationException("model missing");
        await Done(r.Dictate(DictationRig.Speech(1), DictationMode.Plus));
        Assert.Equal("Transcription failed – the recording is kept in the tray menu", r.C.Message);
        var kept = Assert.Single(r.KeptFiles);
        Assert.EndsWith("-plus.caf", kept);
        var item = Assert.Single(r.C.Unfinished);
        Assert.True(item.Plus);
        Assert.Equal("asr failed: model missing", r.C.LastSteps.Outcome);
        Assert.True(r.Claude.All.All(c => c.Stopped));             // Claude session closed
    }

    [Fact]
    public async Task ModelHintAfterFiveSeconds()
    {
        var asr = new TaskCompletionSource<string>();
        var slow = new SlowRecognizer(asr.Task);
        using var rig = new SlowRig(slow);
        rig.C.Press(DictationMode.Plain);
        rig.Feed(DictationRig.Speech(1));
        var t = rig.C.ReleaseAsync();
        rig.Clock.Advance(0.2);
        rig.Clock.Advance(4.9);
        Assert.Equal("Transcribing…", rig.C.Detail);
        rig.Clock.Advance(0.1);
        Assert.Equal(DictationText.ModelHint, rig.C.Detail);
        asr.SetResult("Gut.");
        rig.Clock.Advance(1);
        await Done(t);
        Assert.Equal(new[] { "Gut." }, rig.Output.Pasted);
    }

    [Fact]
    public async Task PressesWhileTranscribingAreIgnored()
    {
        var asr = new TaskCompletionSource<string>();
        using var r = new SlowRig(new SlowRecognizer(asr.Task));
        r.C.Press(DictationMode.Plain);
        r.Feed(DictationRig.Speech(1));
        var t = r.C.ReleaseAsync();
        r.Clock.Advance(0.2);
        r.C.Press(DictationMode.Plain);
        r.C.Press(DictationMode.Plus);
        Assert.Equal(DictationPhase.Transcribing, r.C.Phase);
        Assert.Equal(1, r.Mic.Created);
        asr.SetResult("Ok, passt.");
        r.Clock.Advance(1);
        await Done(t);
    }

    [Fact]
    public async Task PlusShortcutUpgradesARunningPlainDictation()
    {
        using var r = new DictationRig();
        r.Asr.Text = _ => "das ist äh ein test";
        r.Claude.OnSend = (c, _) => c.Answer("Das ist ein Test.");
        r.C.Press(DictationMode.Plain);
        Assert.Empty(r.Claude.All);
        r.Feed(DictationRig.Speech(0.5));
        r.C.Press(DictationMode.Plus);                              // Scribe+ shortcut added
        Assert.Equal(DictationMode.Plus, r.C.Mode);
        Assert.Single(r.Claude.All);                                // Claude started at that moment
        r.C.Press(DictationMode.Plain);                             // no downgrade
        Assert.Equal(DictationMode.Plus, r.C.Mode);
        r.Feed(DictationRig.Speech(0.5));
        var t = r.C.ReleaseAsync();
        r.Clock.Advance(0.2);
        r.Clock.Advance(1);
        await Done(t);
        Assert.Equal(new[] { "Das ist ein Test." }, r.Output.Pasted);
        Assert.Equal("plus", r.C.LastSteps.Mode);
    }

    [Fact]
    public void CancelDropsTheRecording()
    {
        using var r = new DictationRig();
        r.Asr.Text = _ => "x";
        r.C.Press(DictationMode.Plus);
        r.Feed(DictationRig.Speech(1));
        r.C.Cancel();
        Assert.Equal(DictationPhase.Idle, r.C.Phase);
        Assert.True(r.Mic.Current!.Stopped);
        Assert.True(r.Claude.All.Single().Stopped);
        Assert.Equal(1, r.Muter.Restores);
        r.Clock.Advance(5);
        Assert.Empty(r.Output.Pasted);
        Assert.Empty(r.KeptFiles);
    }

    [Fact]
    public void MutesOnlyWhenEnabledAndNotRecording()
    {
        using var r = new DictationRig();
        r.C.Press(DictationMode.Plain);
        Assert.Equal(1, r.Muter.Mutes);
        r.C.Cancel();
        Assert.Equal(1, r.Muter.Restores);

        r.Recording = true;
        r.C.Press(DictationMode.Plain);
        r.C.Cancel();
        Assert.Equal(1, r.Muter.Mutes);

        r.Recording = false;
        r.Prefs.MuteWhileDictating = false;
        r.C.Press(DictationMode.Plain);
        r.C.Cancel();
        Assert.Equal(1, r.Muter.Mutes);
    }

    [Fact]
    public async Task OutputIsRestoredOnRelease()
    {
        using var r = new DictationRig();
        r.C.Press(DictationMode.Plain);
        r.Clock.Advance(0.1);
        var t = r.C.ReleaseAsync();
        Assert.Equal(0, r.Muter.Restores);
        r.Clock.Advance(0.2);
        await Done(t);
        Assert.Equal(1, r.Muter.Restores);
    }

    [Theory]
    [InlineData(MicrophonePermission.NotDetermined, "Allow microphone access, then try again", 1)]
    [InlineData(MicrophonePermission.Denied, "No microphone access (Settings › Privacy)", 0)]
    public void MicrophonePermissionToasts(MicrophonePermission p, string toast, int requests)
    {
        using var r = new DictationRig();
        r.Mic.Permission = p;
        r.C.Press(DictationMode.Plain);
        Assert.Equal(DictationPhase.Message, r.C.Phase);
        Assert.Equal(toast, r.C.Message);
        Assert.Equal(requests, r.Mic.PermissionRequests);
        Assert.Equal(0, r.Mic.Created);
    }

    [Fact]
    public void MicrophoneErrors()
    {
        using var r = new DictationRig();
        r.Mic.FailStart = new InvalidOperationException("no device");
        r.C.Press(DictationMode.Plain);
        Assert.Equal("Microphone unavailable", r.C.Message);
        r.Mic.FailStart = new UnauthorizedAccessException();
        r.C.Press(DictationMode.Plain);
        Assert.Equal("No microphone access (Settings › Privacy)", r.C.Message);
        Assert.Equal(0, r.Muter.Mutes);
    }

    [Fact]
    public void PressCancelsAPendingToast()
    {
        using var r = new DictationRig();
        r.Mic.Permission = MicrophonePermission.Denied;
        r.C.Press(DictationMode.Plain);
        r.Mic.Permission = MicrophonePermission.Granted;
        r.Clock.Advance(1);
        r.C.Press(DictationMode.Plain);
        r.Clock.Advance(3);                                         // the old hide timer must not fire
        Assert.Equal(DictationPhase.Listening, r.C.Phase);
        r.C.Cancel();
    }

    [Fact]
    public void LongToastsLastFourSeconds()
    {
        using var r = new DictationRig();
        r.Mic.Permission = MicrophonePermission.NotDetermined;   // 39 characters → 2.5 s
        r.C.Press(DictationMode.Plain);
        r.Clock.Advance(2.5);
        Assert.Equal(DictationPhase.Idle, r.C.Phase);
        Assert.Equal(4, DictationText.ToastDuration(DictationText.TranscriptionFailedKept));
    }

    // ---------------- Level meter and live preview ----------------

    [Fact]
    public void LevelMeterFollowsTheLast50Ms()
    {
        using var r = new DictationRig();
        r.C.Press(DictationMode.Plain);
        r.Mic.Current!.Push(Enumerable.Repeat(0.5f, 800).ToArray());
        r.Clock.Advance(0.05);
        Assert.Equal(0.5f, r.C.Level, 3);
        r.Mic.Current!.Push(new float[800]);
        r.Clock.Advance(0.05);
        Assert.Equal(0f, r.C.Level);
        r.C.Cancel();
    }

    [Fact]
    public void PreviewEvery700MsOnceHalfASecondIsThere()
    {
        using var r = new DictationRig();
        r.Asr.Text = s => $"wort{s.Length / 16_000}";
        r.C.Press(DictationMode.Plain);
        r.Mic.Current!.Push(new float[7_999]);
        r.Clock.Advance(0.7);
        Assert.Empty(r.Asr.Calls);                                  // < 8,000 samples
        r.Mic.Current!.Push(new float[1]);
        r.Clock.Advance(0.7);
        Assert.Single(r.Asr.Calls);
        Assert.Equal("wort0", r.C.Preview);
        r.C.Cancel();
        Assert.Equal("", r.C.Preview);
    }

    [Fact]
    public void PreviewOfLongDictationsUsesTheLast14SecondsAndDropsTheCutWord()
    {
        using var r = new DictationRig();
        r.Asr.Text = s => "ent und weiter";
        r.C.Press(DictationMode.Plain);
        r.Mic.Current!.Push(new float[15 * 16_000]);
        r.Clock.Advance(0.7);
        Assert.Equal(14 * 16_000, r.Asr.Calls.Single().Length);
        Assert.Equal("… und weiter", r.C.Preview);
        r.C.Cancel();
    }

    // ---------------- Delivery ----------------

    [Fact]
    public async Task CursorMovedToAnotherFieldOnlyCopies()
    {
        using var r = new DictationRig();
        r.Asr.Text = _ => "Bis morgen.";
        r.C.Press(DictationMode.Plain);
        r.Feed(DictationRig.Speech(1));
        r.Fields.Identity = FakeFields.OtherField;
        var t = r.C.ReleaseAsync();
        r.Clock.Advance(0.2);
        await Done(t);
        Assert.Empty(r.Output.Pasted);
        Assert.Equal(new[] { "Bis morgen." }, r.Output.Copied);
        Assert.Equal("Copied – the cursor moved to another field. Paste with Ctrl+V.", r.C.Message);
        Assert.Equal("Bis morgen.", r.C.LastText);
        Assert.Empty(r.KeptFiles);
    }

    [Fact]
    public async Task SameRoleAtTheSamePositionIsTheSameField()
    {
        using var r = new DictationRig();
        r.Asr.Text = _ => "Bis morgen.";
        r.C.Press(DictationMode.Plain);
        r.Feed(DictationRig.Speech(1));
        r.Fields.Identity = FakeFields.Editor with { RuntimeId = new[] { 99 } };   // rebuilt element
        var t = r.C.ReleaseAsync();
        r.Clock.Advance(0.2);
        r.Clock.Advance(0.6);
        await Done(t);
        Assert.Equal(new[] { "Bis morgen." }, r.Output.Pasted);
    }

    [Fact]
    public async Task PasteProblemsShowTheirToastAndKeepTheRecordingWhenNothingArrived()
    {
        using var r = new DictationRig();
        r.Asr.Text = _ => "Hallo.";
        r.Output.Result = new PasteResult(false, "Copied – that window runs as administrator. Paste with Ctrl+V.");
        await Done(r.Dictate(DictationRig.Speech(1)));
        Assert.Equal("Copied – that window runs as administrator. Paste with Ctrl+V.", r.C.Message);
        Assert.Empty(r.KeptFiles);

        using var r2 = new DictationRig();
        r2.Asr.Text = _ => "Hallo.";
        r2.Output.Result = new PasteResult(false, "Could not use the clipboard – try again", Delivered: false);
        await Done(r2.Dictate(DictationRig.Speech(1)));
        Assert.Single(r2.KeptFiles);
        Assert.Single(r2.C.Unfinished);
    }

    [Fact]
    public async Task FitIsSkippedWhenOffOrBeforeUnknown()
    {
        using var r = new DictationRig();
        r.Asr.Text = _ => "Und dann";
        r.Fields.Before = null;
        var t = r.Dictate(DictationRig.Speech(1));
        r.Clock.Advance(1);
        await Done(t);
        Assert.Equal("Und dann", r.Output.Pasted.Single());

        using var r2 = new DictationRig();
        r2.Asr.Text = _ => "Und dann";
        r2.Fields.Before = "Ich gehe heim";
        r2.Prefs.FitToCursor = false;
        r2.Prefs.ScribePlusContext = false;
        var t2 = r2.Dictate(DictationRig.Speech(1));
        r2.Clock.Advance(1);
        await Done(t2);
        Assert.Equal("Und dann", r2.Output.Pasted.Single());
        Assert.Equal(new[] { false, false }, r2.Fields.Reads);      // text not read at all
    }

    // ---------------- Scribe+ ----------------

    [Fact]
    public async Task ScribePlusSendsContextAndVocabularyAndUsesTheAnswer()
    {
        using var r = new DictationRig();
        r.Asr.Text = _ => "wir sprechen mit muster morgen";
        r.C.Vocabulary = new Vocabulary { Terms = { "Claude" }, AyaTerms = { "Muster Metallbau", "Beispiel" } };
        r.Fields.App = "Mail";
        r.Fields.Before = "Hallo Anna,\n";
        r.Fields.After = "Viele Grüße";
        r.Claude.OnSend = (c, _) => c.Answer("⟦TEXT⟧Hallo Anna,\n\nwir sprechen morgen mit Muster Metallbau.⟦/TEXT⟧");
        r.C.Press(DictationMode.Plus);
        r.Feed(DictationRig.Speech(1));
        var t = r.C.ReleaseAsync();
        r.Clock.Advance(0.2);
        r.Clock.Advance(1);
        await Done(t);
        var prompt = r.Claude.All.Single().Sent.Single();
        Assert.Contains("APP: Mail\n", prompt);
        Assert.Contains("CONTEXT – text before the cursor, already written (do not repeat it):\nHallo Anna,\n", prompt);
        Assert.Contains("CONTEXT – text after the cursor (do not repeat it):\nViele Grüße\n", prompt);
        Assert.Contains("VOCABULARY: Claude, Muster Metallbau\n", prompt);
        Assert.Contains("⟦TEXT⟧\nwir sprechen mit muster morgen\n⟦/TEXT⟧", prompt);
        // Echoed context removed, then fitted (capital after a line break, space before "Viele").
        Assert.Equal("Wir sprechen morgen mit Muster Metallbau. ", r.Output.Pasted.Single());
        Assert.Equal("ok", r.C.LastSteps.Outcome);
        Assert.True(r.Claude.All.Single().Stopped);                  // one process per dictation
    }

    [Fact]
    public async Task ScribePlusWithoutContextOptionSendsNoAppOrCursorText()
    {
        using var r = new DictationRig();
        r.Asr.Text = _ => "kurzer test";
        r.Prefs.ScribePlusContext = false;
        r.Fields.Before = "Vorher";
        r.Claude.OnSend = (c, _) => c.Answer("Kurzer Test.");
        var t = r.Dictate(DictationRig.Speech(1), DictationMode.Plus);
        r.Clock.Advance(1);
        await Done(t);
        var prompt = r.Claude.All.Single().Sent.Single();
        Assert.DoesNotContain("APP:", prompt);
        Assert.DoesNotContain("CONTEXT", prompt);
        Assert.Equal(" Kurzer Test.", r.Output.Pasted.Single());     // fit still uses the field text
    }

    [Fact]
    public async Task ScribePlusDetailWhileWaiting()
    {
        using var r = new DictationRig();
        r.Asr.Text = _ => "eins zwei drei";
        r.Claude.OnSend = null;
        var t = r.Dictate(DictationRig.Speech(1), DictationMode.Plus);
        Assert.Equal("Writing with Claude…", r.C.Detail);
        r.Clock.Advance(8);
        r.Clock.Advance(4);
        await Done(t);
    }

    [Fact]
    public async Task ScribePlusTimesOutAfterEightSecondsFromSending()
    {
        using var r = new DictationRig();
        r.Asr.Text = _ => "eins zwei drei";
        r.Claude.OnSend = null;                                     // never answers
        var t = r.Dictate(DictationRig.Speech(1), DictationMode.Plus);
        r.Clock.Advance(7.99);
        Assert.Empty(r.Output.Pasted);
        r.Clock.Advance(0.01);
        await Done(t);
        Assert.Equal(new[] { "eins zwei drei" }, r.Output.Pasted);
        Assert.Equal("Scribe+ failed – pasted word for word", r.C.Message);
        Assert.Equal("Scribe+ failed – pasted word for word", r.C.LastSteps.Outcome);
        Assert.Equal(8, r.C.LastSteps.Claude, 2);
        Assert.True(r.Claude.All.Single().Stopped);
    }

    public static IEnumerable<object[]> Failures() => new[]
    {
        new object[] { (Action<FakeClaude, string>)((c, _) => c.Answer("Ich kann dir dabei nicht helfen.")), "Scribe+ answer did not fit – pasted word for word", "rejected: refusal" },
        new object[] { (Action<FakeClaude, string>)((c, _) => c.Answer("")), "Scribe+ answer did not fit – pasted word for word", "rejected: empty" },
        new object[] { (Action<FakeClaude, string>)((c, _) => c.Answer("x ⟦TEXT⟧ y")), "Scribe+ answer did not fit – pasted word for word", "rejected: markers" },
        new object[] { (Action<FakeClaude, string>)((c, _) => c.Answer(new string('a', 200))), "Scribe+ answer did not fit – pasted word for word", "rejected: answer" },
        new object[] { (Action<FakeClaude, string>)((c, _) => c.Error("Claude AI usage limit reached")), "Claude usage limit reached – pasted word for word", "Scribe+ failed" },
        new object[] { (Action<FakeClaude, string>)((c, _) => c.Exit(1, "Please run /login first")), "Claude Code is not logged in – pasted word for word", "Scribe+ failed" },
        new object[] { (Action<FakeClaude, string>)((c, _) => c.Exit(1, "boom")), "Scribe+ failed – pasted word for word", "Scribe+ failed" },
        new object[] { (Action<FakeClaude, string>)((_, _) => throw new IOException("pipe")), "Scribe+ failed – pasted word for word", "Scribe+ failed" },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task ScribePlusFallsBackToTheWordForWordText(Action<FakeClaude, string> onSend, string toast, string log)
    {
        using var r = new DictationRig();
        r.Asr.Text = _ => "eins zwei drei";
        r.Claude.OnSend = onSend;
        await Done(r.Dictate(DictationRig.Speech(1), DictationMode.Plus));
        Assert.Equal(new[] { "eins zwei drei" }, r.Output.Pasted);
        Assert.Equal(toast, r.C.Message);
        Assert.Contains(r.Logs, l => l.Contains(log));
    }

    [Fact]
    public async Task ScribePlusWithoutClaudeBinaryPastesWordForWord()
    {
        using var r = new DictationRig { NoClaudeBinary = true };
        r.Asr.Text = _ => "eins zwei drei";
        await Done(r.Dictate(DictationRig.Speech(1), DictationMode.Plus));
        Assert.Equal(new[] { "eins zwei drei" }, r.Output.Pasted);
        Assert.Equal("Scribe+ needs Claude Code (Settings › Assist) – pasted word for word", r.C.Message);
    }

    [Fact]
    public async Task ScribePlusFitIsAppliedAfterTheRewrite()
    {
        using var r = new DictationRig();
        r.Asr.Text = _ => "das angebot";
        r.Fields.Before = "Wir schicken ";
        r.Fields.After = "morgen raus";
        r.Claude.OnSend = (c, _) => c.Answer("Das Angebot.");
        var t = r.Dictate(DictationRig.Speech(1), DictationMode.Plus);
        r.Clock.Advance(1);
        await Done(t);
        Assert.Equal("das Angebot ", r.Output.Pasted.Single());
    }

    // ---------------- Spare Claude process (spec 04 §2.3) ----------------

    [Fact]
    public async Task SpareIsStartedAfterThreeSecondsAndUsedOnPress()
    {
        using var r = new DictationRig();
        r.Prefs.ScribePlusWarm = true;
        r.Asr.Text = _ => "eins zwei";
        r.C.UpdateSpare();
        r.Clock.Advance(2.9);
        Assert.Empty(r.Claude.All);
        r.Clock.Advance(0.1);
        var spare = Assert.Single(r.Claude.All);
        Assert.Equal("sonnet", spare.Model);

        r.Claude.OnSend = (c, _) => c.Answer("Eins, zwei.");
        r.C.Press(DictationMode.Plus);
        Assert.Single(r.Claude.All);                                // the spare was taken
        r.Feed(DictationRig.Speech(1));
        var t = r.C.ReleaseAsync();
        r.Clock.Advance(0.2);
        r.Clock.Advance(0.6);
        await Done(t);
        Assert.Single(spare.Sent);
        Assert.True(spare.Stopped);
        r.Clock.Advance(1);                                         // 1 s after the dictation: a new spare
        Assert.Equal(2, r.Claude.All.Count);
        Assert.True(r.Claude.All[1].Running);
    }

    [Fact]
    public void SpareIsReplacedWhenTheModelChangesOrItIsOld()
    {
        using var r = new DictationRig();
        r.Prefs.ScribePlusWarm = true;
        r.C.UpdateSpare();
        r.Clock.Advance(3);
        Assert.Single(r.Claude.All);
        r.Clock.Advance(TimeSpan.FromMinutes(10));                  // periodic check: still fine
        Assert.Single(r.Claude.All);
        r.Prefs.ScribePlusModel = "haiku";
        r.C.UpdateSpare();
        r.Clock.Advance(3);
        Assert.Equal(2, r.Claude.All.Count);
        Assert.True(r.Claude.All[0].Stopped);
        Assert.Equal("haiku", r.Claude.All[1].Model);
        r.Clock.Advance(TimeSpan.FromMinutes(20));
        Assert.Equal(2, r.Claude.All.Count);
        r.Clock.Advance(TimeSpan.FromMinutes(10));                  // 30 minutes old → replaced
        Assert.Equal(3, r.Claude.All.Count);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("noShortcut")]
    [InlineData("notWarm")]
    public void SpareIsClosedWhenNotWanted(string why)
    {
        using var r = new DictationRig();
        r.Prefs.ScribePlusWarm = true;
        r.C.UpdateSpare();
        r.Clock.Advance(3);
        var spare = Assert.Single(r.Claude.All);
        switch (why)
        {
            case "disabled": r.Prefs.DictationEnabled = false; break;
            case "noShortcut": r.Prefs.ScribePlusHotKey = null; break;
            default: r.Prefs.ScribePlusWarm = false; break;
        }
        r.C.UpdateSpare();
        Assert.True(spare.Stopped);
        r.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Single(r.Claude.All);
    }

    [Fact]
    public void ExitedSpareIsNotUsed()
    {
        using var r = new DictationRig();
        r.Prefs.ScribePlusWarm = true;
        r.C.UpdateSpare();
        r.Clock.Advance(3);
        r.Claude.All[0].Stop();
        r.C.Press(DictationMode.Plus);
        Assert.Equal(2, r.Claude.All.Count);
        r.C.Cancel();
    }

    // ---------------- Unfinished dictations (spec 04 §1.8) ----------------

    DictationText.Steps? _unused;

    static DictationRecovery.Item Keep(DictationRig r, float[] samples, bool plus)
    {
        Directory.CreateDirectory(r.Folder);
        DictationRecovery.Save(samples, plus, r.Clock.UtcNow.AddMinutes(-5), r.Folder);
        r.C.RefreshUnfinished();
        return r.C.Unfinished.Single();
    }

    [Fact]
    public async Task RetryCopiesWithoutFitAndDeletesTheFile()
    {
        using var r = new DictationRig();
        r.Asr.Text = _ => "wir treffen uns um 9 Uhr 30.";
        r.Fields.Before = "Hallo";
        var item = Keep(r, DictationRig.Speech(1), plus: false);
        var t = r.C.RetryAsync(item);
        await Done(t);
        Assert.Equal(new[] { "wir treffen uns um 9:30 Uhr." }, r.Output.Copied);
        Assert.Empty(r.Output.Pasted);
        Assert.Equal("Copied (not pasted) – paste with Ctrl+V", r.C.Message);
        Assert.Equal("wir treffen uns um 9:30 Uhr.", r.C.LastText);
        Assert.Empty(r.C.Unfinished);
        Assert.Empty(r.KeptFiles);
    }

    [Fact]
    public async Task RetryOfAPlusDictationUsesScribePlusWithoutContext()
    {
        using var r = new DictationRig();
        r.Asr.Text = _ => "eins zwei";
        r.Claude.OnSend = (c, _) => c.Answer("Eins, zwei.");
        var item = Keep(r, DictationRig.Speech(1), plus: true);
        await Done(r.C.RetryAsync(item));
        var prompt = r.Claude.All.Single().Sent.Single();
        Assert.DoesNotContain("APP:", prompt);
        Assert.Equal(new[] { "Eins, zwei." }, r.Output.Copied);

        using var r2 = new DictationRig { NoClaudeBinary = true };
        r2.Asr.Text = _ => "eins zwei";
        await Done(r2.C.RetryAsync(Keep(r2, DictationRig.Speech(1), plus: true)));
        Assert.Equal("Scribe+ needs Claude Code (Settings › Assist) – copied word for word. Paste with Ctrl+V.", r2.C.Message);
    }

    [Fact]
    public async Task RetryWithoutSpeechOrWithAnError()
    {
        using var r = new DictationRig();
        r.Asr.Text = _ => "";
        await Done(r.C.RetryAsync(Keep(r, DictationRig.Speech(1), plus: false)));
        Assert.Equal("No speech in that recording", r.C.Message);
        Assert.Empty(r.KeptFiles);

        using var r2 = new DictationRig();
        r2.Asr.Fail = new Exception("x");
        await Done(r2.C.RetryAsync(Keep(r2, DictationRig.Speech(1), plus: false)));
        Assert.Equal("Transcription failed again", r2.C.Message);
        Assert.Single(r2.KeptFiles);
    }

    [Fact]
    public async Task RetryIsIgnoredWhileDictating()
    {
        using var r = new DictationRig();
        r.Asr.Text = _ => "x";
        var item = Keep(r, DictationRig.Speech(1), plus: false);
        r.C.Press(DictationMode.Plain);
        await Done(r.C.RetryAsync(item));
        Assert.Equal(DictationPhase.Listening, r.C.Phase);
        Assert.Single(r.C.Unfinished);
        r.C.Cancel();
    }

    [Fact]
    public void DiscardAndCopyLast()
    {
        using var r = new DictationRig();
        var item = Keep(r, DictationRig.Speech(1), plus: false);
        r.C.Discard(item);
        Assert.Empty(r.C.Unfinished);
        Assert.False(r.C.CopyLast());
    }

    [Fact]
    public void KeptDictationsOlderThan24HoursArePruned()
    {
        using var r = new DictationRig();
        Directory.CreateDirectory(r.Folder);
        DictationRecovery.Save(DictationRig.Speech(1), false, r.Clock.UtcNow.AddHours(-25), r.Folder);
        r.C.RefreshUnfinished();
        Assert.Empty(r.C.Unfinished);
        Assert.Empty(r.KeptFiles);
    }

    // ---------------- Test hook / msctl ----------------

    [Fact]
    public async Task FinishWithDeliverToSavesAndPastesNothing()
    {
        using var r = new DictationRig();
        r.Asr.Text = _ => "Hallo Welt.";
        string? got = null;
        var audio = DictationRig.Speech(1);
        var st = await r.C.FinishAsync(audio, InventedText.LongestVoicedRun(audio), DictationMode.Plain, (t, _) => { got = t; return Task.CompletedTask; });
        Assert.Equal("Hallo Welt.", got);
        Assert.Empty(r.Output.Pasted);
        Assert.Empty(r.KeptFiles);
        Assert.Equal("Hallo Welt.", st.Raw);
        Assert.Equal(2.0, st.Audio, 3);
        _unused = st;
    }

    // ---------------- Helpers ----------------

    sealed class SlowRecognizer(Task<string> result) : IDictationRecognizer
    {
        public Task<string> DictateAsync(float[] samples, CancellationToken ct = default) =>
            samples.Length >= 16_000 * 2 ? result : Task.FromResult("");
    }

    /// A rig whose final ASR waits for the test.
    sealed class SlowRig : IDisposable
    {
        public FakeDictationClock Clock { get; } = new();
        public FakeMicrophone Mic { get; } = new();
        public FakeOutput Output { get; } = new();
        public DictationController C { get; }
        readonly string _folder = Path.Combine(Path.GetTempPath(), "ma-dictation-" + Guid.NewGuid().ToString("N"));

        public SlowRig(IDictationRecognizer asr)
        {
            C = new DictationController(Mic, asr, new FakeFields(), Output, _ => throw new InvalidOperationException(),
                () => new MeetingAssistant.Core.Storage.Preferences(), new FakeMuter(), Clock, new DictationRecoveryStore(_folder));
        }

        public void Feed(float[] audio)
        {
            for (int pos = 0; pos < audio.Length; pos += 1600)
            {
                Mic.Current?.Push(audio.AsSpan(pos, Math.Min(1600, audio.Length - pos)).ToArray());
                Clock.Advance(0.1);
            }
        }

        public void Dispose()
        {
            C.Dispose();
            try { Directory.Delete(_folder, true); } catch { }
        }
    }
}

/// Stored shortcuts ↔ keyboard-hook shortcuts (spec 04 §3.4).
public sealed class DictationShortcutTests
{
    [Fact]
    public void DefaultsRoundTrip()
    {
        var p = new MeetingAssistant.Core.Storage.Preferences();
        Assert.Equal(MeetingAssistant.Core.Input.HotKey.DefaultPlain, DictationShortcuts.Plain(p));
        Assert.Equal(MeetingAssistant.Core.Input.HotKey.DefaultPlus, DictationShortcuts.Plus(p));
        Assert.Equal("Right Ctrl", DictationShortcuts.Display(DictationShortcuts.Plain(p)));
        Assert.Equal("Right Ctrl + Right Shift", DictationShortcuts.Display(DictationShortcuts.Plus(p)));
        Assert.Equal(p.ScribePlusHotKey, DictationShortcuts.ToSetting(DictationShortcuts.Plus(p)));
    }

    [Fact]
    public void ComboAndNotSetAndDisabled()
    {
        var combo = MeetingAssistant.Core.Input.HotKey.Combo(0x20, MeetingAssistant.Core.Input.HotKey.ModControl | MeetingAssistant.Core.Input.HotKey.ModAlt);
        var p = new MeetingAssistant.Core.Storage.Preferences { DictationHotKey = DictationShortcuts.ToSetting(combo), ScribePlusHotKey = null };
        Assert.Equal(combo, DictationShortcuts.Plain(p));
        Assert.Equal("Ctrl+Alt+Space", DictationShortcuts.Display(DictationShortcuts.Plain(p)));
        Assert.Null(DictationShortcuts.Plus(p));
        Assert.Equal("Not set", DictationShortcuts.Display(null));
        p.DictationEnabled = false;
        Assert.Null(DictationShortcuts.Plain(p));
    }

    [Fact]
    public void MacCodedShortcutsFallBackToTheWindowsDefault()
    {
        var mac = new MeetingAssistant.Core.Storage.HotKeySetting(61, 0, true, null, Platform: null);
        Assert.Equal(MeetingAssistant.Core.Input.HotKey.DefaultPlain, DictationShortcuts.ToHotKey(mac, MeetingAssistant.Core.Input.HotKey.DefaultPlain));
    }
}
