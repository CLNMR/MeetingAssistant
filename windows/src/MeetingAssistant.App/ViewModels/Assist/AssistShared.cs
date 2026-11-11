using System.ComponentModel;
using System.Windows;
using MeetingAssistant.Core.Assist;

namespace MeetingAssistant.App.ViewModels;

/// One option of an Aya select (spec 03 §10): the stored value (null for "None"/"New meeting") and the title.
public sealed record Choice(object? Value, string Title)
{
    public override string ToString() => Title;
}

/// Helpers shared by the Assist view models: UI-thread dispatch, confirmations, Core change subscriptions.
public static class AssistUiHelpers
{
    /// Runs on the UI thread (directly when already there).
    public static void Ui(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) a(); else d.BeginInvoke(a);
    }

    /// Subscribes to a Core object's changes and calls <paramref name="changed"/> with the property name on the UI thread.
    public static void Observe(INotifyPropertyChanged source, Action<string?> changed) =>
        source.PropertyChanged += (_, e) => Ui(() => changed(e.PropertyName));

    /// Asks before replacing edits ("Draft the recap again?" …): true when the destructive button was chosen.
    public static bool ConfirmDestructive(string heading, string message, string action)
    {
        if (Application.Current is not App app) return true;
        var r = app.ShowConfirm(heading, message,
            new AlertButton(action, AlertButtonKind.Destructive, IsDefault: true),
            new AlertButton("Cancel", AlertButtonKind.Secondary, IsCancel: true));
        return r == 0;
    }

    /// Starts a task and reports exceptions as the app's global error instead of losing them.
    public static async void Fire(Func<Task> work)
    {
        try { await work(); }
        catch (Exception e)
        {
            if (Application.Current is App app && app.Shell is not null) app.Shell.ErrorMessage = e.Message;
        }
    }

    /// The StepLine kind of a Core step state.
    public static Controls.StepKind Kind(StepState? s) => s switch
    {
        StepState.Working => Controls.StepKind.Working,
        StepState.Done => Controls.StepKind.Done,
        StepState.Failed => Controls.StepKind.Failed,
        _ => Controls.StepKind.Idle,
    };
}
