using MeetingAssistant.App.Dictation;
using MeetingAssistant.App.Shell;

namespace MeetingAssistant.App;

/// The dictation area (spec 04 §1–§3, §6.2, §6.7): global shortcuts, HUD, tray items, Scribe+.
public partial class App
{
    /// Push-to-talk dictation; see <see cref="DictationService"/>.
    public DictationService? Dictation { get; private set; }

    partial void StartDictation()
    {
        Dictation = new DictationService(Services, Hud, Shell);
        if (_tray is TrayIcon tray && tray.ContextMenu is { } menu) Dictation.AttachTray(tray.UnfinishedSlot, menu);
        ConnectDictationToAssist();
        if (DictationTestArguments(Environment.GetCommandLineArgs()) is { } test) _ = RunDictationTestAsync(test);
    }

    /// `-dictationTest <audio> -dictationTestOut <file> [-dictationTestMode plain|plus] [-dictationTestRuns N]` (spec 04 §1.9).
    static (string Audio, string Out, bool Plus, int Runs)? DictationTestArguments(string[] args)
    {
        string? Value(string flag) { var i = Array.IndexOf(args, flag); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        if (Value("-dictationTest") is not { } audio || Value("-dictationTestOut") is not { } output) return null;
        int runs = int.TryParse(Value("-dictationTestRuns"), out var n) && n > 0 ? n : 1;
        return (audio, output, Value("-dictationTestMode") != "plain", runs);
    }

    /// The test hook: per run wait 8 s (the spare Claude process gets ready), run the processing
    /// without pasting, and append the step timings as one JSON line; then write the file and quit.
    async Task RunDictationTestAsync((string Audio, string Out, bool Plus, int Runs) test)
    {
        var lines = new List<string>();
        try
        {
            var samples = Core.Audio.AudioFile.Read(test.Audio);
            var voiced = Core.Text.InventedText.LongestVoicedRun(samples);
            for (int i = 0; i < test.Runs && Dictation is not null; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(8));
                var st = await Dictation.Controller.FinishAsync(samples, voiced,
                    test.Plus ? Core.Dictation.DictationMode.Plus : Core.Dictation.DictationMode.Plain, (_, _) => Task.CompletedTask);
                lines.Add(System.Text.Json.JsonSerializer.Serialize(new
                {
                    mode = st.Mode, audio = st.Audio, asr = st.Asr, cleanup = st.Cleanup, field = st.Field, claude = st.Claude,
                    total = st.Total, outcome = st.Outcome, raw = st.Raw, text = st.Text,
                }));
            }
            await System.IO.File.WriteAllTextAsync(test.Out, string.Join("\n", lines));
        }
        catch (Exception e)
        {
            System.Diagnostics.Debug.WriteLine($"dictation test failed: {e.Message}");
        }
        Quit();
    }

    /// Hands Assist's connection (Claude binary, Aya names) to the dictation; when the Assist area
    /// is not up yet, it is looked up again when needed.
    void ConnectDictationToAssist()
    {
        if (Dictation is null) return;
        Dictation.AyaLocator = AssistConnectionForDictation;
        Dictation.Aya = AssistConnectionForDictation();
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () =>
        {
            if (Dictation is { Aya: null }) Dictation.Aya = AssistConnectionForDictation();
        });
    }

    /// Assist's connection, found by type on the app (the Assist area owns its controller; whatever
    /// property exposes an <see cref="Core.Assist.AssistController"/> or <see cref="Core.Assist.AssistConnection"/>
    /// is used), so the two areas need no shared field. Null while Assist is not up.
    Core.Assist.AssistConnection? AssistConnectionForDictation()
    {
        foreach (var prop in GetType().GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
        {
            if (prop.GetIndexParameters().Length > 0) continue;
            if (prop.PropertyType == typeof(Core.Assist.AssistConnection)) return prop.GetValue(this) as Core.Assist.AssistConnection;
            if (prop.PropertyType == typeof(Core.Assist.AssistController)) return (prop.GetValue(this) as Core.Assist.AssistController)?.Aya;
        }
        return null;
    }

    partial void StopDictation()
    {
        Dictation?.Dispose();
        Dictation = null;
    }
}
