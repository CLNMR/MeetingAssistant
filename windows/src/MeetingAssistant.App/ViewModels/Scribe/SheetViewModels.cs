using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeetingAssistant.App.Services;
using MeetingAssistant.Core.Model;
using MeetingAssistant.Core.Scribe;
using MeetingAssistant.Core.Storage;

namespace MeetingAssistant.App.ViewModels;

/// A modal sheet (Stop Earlier, Trim, Split) shown by Views.SheetHost.
public abstract partial class SheetViewModel : ObservableObject
{
    /// Window title.
    public abstract string Title { get; }
    /// Width in DIPs (Mac sheet widths: 520 / 540).
    public virtual double Width => 540;
    /// The user confirmed (pressed the default button).
    public bool Confirmed { get; private set; }
    /// Asks the host window to close.
    public event Action? CloseRequested;

    [RelayCommand] protected void Cancel() => CloseRequested?.Invoke();

    protected void Confirm()
    {
        Confirmed = true;
        CloseRequested?.Invoke();
    }
}

/// A time in a recording: slider plus an editable "h:mm:ss" / "mm:ss" / seconds field (Mac TimeChooser).
public sealed partial class TimeChooserViewModel : ObservableObject
{
    public TimeChooserViewModel(double value, double maximum, Action? changed = null)
    {
        _maximum = Math.Max(0, maximum);
        _value = Math.Clamp(value, 0, _maximum);
        _text = Exporter.Clock(_value);
        _changed = changed;
    }

    readonly Action? _changed;

    [ObservableProperty] double _maximum;

    /// Seconds (slider).
    [ObservableProperty] double _value;

    /// The field text; applied with Enter or when the field loses focus.
    [ObservableProperty] string _text;

    partial void OnValueChanged(double value)
    {
        Text = Exporter.Clock(value);
        _changed?.Invoke();
    }

    /// Parses the field; an invalid entry shows the current value again.
    [RelayCommand]
    public void Apply()
    {
        if (ScribeRules.ParseTime(Text) is { } t) Value = Math.Clamp(t, 0, Maximum);
        Text = Exporter.Clock(Value);
    }

    /// Sets the value (quick buttons, Playhead).
    public void Set(double seconds) => Value = Math.Clamp(seconds, 0, Maximum);
}

/// "Stop and Remove the End" (spec 01 "Stop Earlier… sheet").
public sealed partial class StopEarlierViewModel : SheetViewModel
{
    public override string Title => "Stop and Remove the End";
    public override double Width => 520;

    public StopEarlierViewModel(ScribeController scribe)
    {
        MaxTime = Math.Max(1, scribe.Elapsed);
        LastSpeech = ScribeRules.LastSpeech(scribe.LiveLines, MaxTime);
        Chooser = new TimeChooserViewModel(LastSpeech ?? MaxTime, MaxTime, Changed);
        Quick = ScribeRules.StopEarlierOptions(MaxTime)
            .Select(o => new QuickTime(o.Title, new RelayCommand(() => Chooser.Set(o.KeepUntil)))).ToList();
        if (LastSpeech is { } t)
            Quick.Insert(0, new QuickTime($"Last speech ({Exporter.Clock(t)})", new RelayCommand(() => Chooser.Set(t))));
    }

    /// One quick button.
    public sealed record QuickTime(string Title, IRelayCommand Command);

    public double MaxTime { get; }
    public double? LastSpeech { get; }
    public TimeChooserViewModel Chooser { get; }
    public List<QuickTime> Quick { get; }
    public double KeepUntil => Chooser.Value;

    public string Message => "Forgot to stop? Choose where the meeting ended. The recording stops now and everything after that point is deleted.";
    public string KeepsText => $"Keeps {Exporter.Clock(KeepUntil)} of {Exporter.Clock(MaxTime)}.";
    public string StopTitle => $"Stop and Keep {Exporter.Clock(KeepUntil)}";

    void Changed()
    {
        OnPropertyChanged(nameof(KeepsText));
        OnPropertyChanged(nameof(StopTitle));
        StopCommand.NotifyCanExecuteChanged();
    }

    bool CanStop() => KeepUntil >= 1;

    [RelayCommand(CanExecute = nameof(CanStop))]
    void Stop() => Confirm();
}

/// "Trim Recording" (spec 01 "Editing recordings: trim and split").
public sealed partial class TrimSheetViewModel : SheetViewModel
{
    public override string Title => "Trim Recording";

    public TrimSheetViewModel(SessionInfo info, double playhead, double? lastSpeech, double? initialStart, double? initialEnd)
    {
        Duration = info.DurationSec;
        Playhead = playhead;
        LastSpeech = lastSpeech;
        Start = new TimeChooserViewModel(initialStart ?? 0, Duration, Changed);
        End = new TimeChooserViewModel(initialEnd ?? Duration, Duration, Changed);
    }

    public double Duration { get; }
    public double Playhead { get; }
    public double? LastSpeech { get; }
    public TimeChooserViewModel Start { get; }
    public TimeChooserViewModel End { get; }

    public string Message => "Keep only the part between start and end. The rest of the audio is deleted; the transcript is cut to match.";
    public string PlayheadHelp => $"Use the current playback position ({Exporter.Clock(Playhead)})";
    public string? LastSpeechTitle => LastSpeech is { } t ? $"End after last speech ({Exporter.Clock(t)})" : null;
    public string KeepsText => $"Keeps {Exporter.Clock(Math.Max(0, End.Value - Start.Value))} of {Exporter.Clock(Duration)}.";

    void Changed()
    {
        OnPropertyChanged(nameof(KeepsText));
        TrimCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand] void StartAtPlayhead() => Start.Set(Playhead);
    [RelayCommand] void EndAtPlayhead() => End.Set(Playhead);
    [RelayCommand] void EndAfterLastSpeech() { if (LastSpeech is { } t) End.Set(t); }

    bool CanTrim() => ScribeRules.CanTrim(Start.Value, End.Value, Duration);

    [RelayCommand(CanExecute = nameof(CanTrim))]
    void Trim() => Confirm();
}

/// "Split Recording" (spec 01 "Editing recordings: trim and split", "Split at a pause").
public sealed partial class SplitSheetViewModel : SheetViewModel
{
    readonly string _title;
    public override string Title => "Split Recording";

    public SplitSheetViewModel(SessionInfo info, double playhead, Pause? pause)
    {
        _title = info.Title;
        Duration = info.DurationSec;
        Playhead = playhead;
        Pause = pause;
        At = new TimeChooserViewModel(ScribeRules.InitialSplit(pause, playhead, Duration), Duration, Changed);
    }

    public double Duration { get; }
    public double Playhead { get; }
    public Pause? Pause { get; }
    public TimeChooserViewModel At { get; }

    /// "Remove the pause … from part 1" (default on).
    [ObservableProperty] bool _removePause = true;
    partial void OnRemovePauseChanged(bool value) => OnPropertyChanged(nameof(PartsText));

    public string? PauseText => Pause is { } p
        ? $"No conversation from {Exporter.Clock(p.Start)} to {Exporter.Clock(p.End)}. Part 2 starts just before speech resumes."
        : null;
    public string Message => $"Everything after the split point becomes a separate recording, “{_title} (Part 2)”. The transcript is split too.";
    public string PlayheadHelp => $"Use the current playback position ({Exporter.Clock(Playhead)})";

    /// The pause the split uses (time within 0.5 s of its split point).
    public Pause? AtPause => ScribeRules.SplitAtPause(Pause, At.Value);
    public bool ShowRemovePause => AtPause is { } p && ScribeRules.CanRemovePause(p);
    public string RemovePauseText => AtPause is { } p ? $"Remove the pause ({Pauses.LengthText(p.SplitPoint - p.KeepUntil)}) from part 1" : "";
    public string PartsText =>
        $"Part 1: {Exporter.Clock(ScribeRules.SplitPart1(AtPause, RemovePause, At.Value))} · Part 2: {Exporter.Clock(Math.Max(0, Duration - At.Value))}";

    void Changed()
    {
        OnPropertyChanged(nameof(AtPause)); OnPropertyChanged(nameof(ShowRemovePause));
        OnPropertyChanged(nameof(RemovePauseText)); OnPropertyChanged(nameof(PartsText));
        SplitCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand] void AtPlayhead() => At.Set(Playhead);

    bool CanSplit() => ScribeRules.CanSplit(At.Value, Duration);

    [RelayCommand(CanExecute = nameof(CanSplit))]
    void Split() => Confirm();
}
