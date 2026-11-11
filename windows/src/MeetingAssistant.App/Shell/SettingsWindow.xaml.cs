using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MeetingAssistant.App.Shell;

/// The Settings window (spec 04 §6). One instance; opening it again brings it to the front.
public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Themes.ThemeManager.ApplyTitleBar(this);
        InputBindings.Add(new KeyBinding(new RelayClose(this), Key.Escape, ModifierKeys.None));
    }

    /// Selects a tab by its title ("General", "Dictation", "Vocabulary", "Voices", "Models", "Assist").
    public void SelectTab(string? title)
    {
        if (title is null) return;
        foreach (TabItem t in Tabs.Items)
            if (string.Equals(t.Header as string, title, StringComparison.OrdinalIgnoreCase)) { Tabs.SelectedItem = t; return; }
    }

    sealed class RelayClose(Window w) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => w.Close();
    }
}
