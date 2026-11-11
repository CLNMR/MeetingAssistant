using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MeetingAssistant.App.ViewModels;

namespace MeetingAssistant.App.Views;

/// Shows a <see cref="SheetViewModel"/> as a modal window over the main window (the Mac's sheets).
/// The sheet's view comes from the DataTemplates in ScribeSheets.xaml.
public static class SheetHost
{
    /// Shows the sheet; returns true when it closed normally (check <see cref="SheetViewModel.Confirmed"/>).
    public static bool Show(SheetViewModel vm)
    {
        var templates = new ResourceDictionary { Source = new Uri("/MeetingAssistant;component/Views/Scribe/ScribeSheets.xaml", UriKind.Relative) };
        var w = new Window
        {
            Title = vm.Title,
            Width = vm.Width,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Style = (Style)Application.Current.FindResource("Aya.Window"),
            Content = new ContentControl { Content = vm, Margin = new Thickness(20), Focusable = false },
        };
        w.Resources.MergedDictionaries.Add(templates);
        var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(x => x.IsActive) ?? Application.Current.MainWindow;
        if (owner is { IsVisible: true } && owner != w) w.Owner = owner; else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        w.InputBindings.Add(new KeyBinding(new CommunityToolkit.Mvvm.Input.RelayCommand(w.Close), Key.Escape, ModifierKeys.None));
        vm.CloseRequested += w.Close;
        w.SourceInitialized += (_, _) => Themes.ThemeManager.ApplyTitleBar(w);
        w.ShowDialog();
        vm.CloseRequested -= w.Close;
        return true;
    }
}

/// A small modal prompt with one text field ("Rename speaker").
public static class PromptWindow
{
    /// Returns the entered text, or null when cancelled.
    public static string? Ask(string heading, string message, string initial, string placeholder)
    {
        string? result = null;
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 12, 0, 0) };
        Controls.Ui.SetPlaceholder(box, placeholder);
        var save = new Button { Content = "Save", IsDefault = true, MinWidth = 80, Margin = new Thickness(8, 0, 0, 0), Style = (Style)Application.Current.FindResource("Aya.Button.Primary") };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = heading, FontSize = 15, FontWeight = FontWeights.SemiBold, Style = (Style)Application.Current.FindResource("Aya.Text") });
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Style = (Style)Application.Current.FindResource("Aya.Text.Muted") });
        panel.Children.Add(box);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        panel.Children.Add(buttons);
        var w = new Window
        {
            Title = "MeetingAssistant", Width = 420, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Style = (Style)Application.Current.FindResource("Aya.Window"), Content = panel,
        };
        var owner = Application.Current.MainWindow;
        if (owner is { IsVisible: true }) w.Owner = owner; else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        save.Click += (_, _) => { result = box.Text; w.DialogResult = true; };
        w.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        w.SourceInitialized += (_, _) => Themes.ThemeManager.ApplyTitleBar(w);
        w.ShowDialog();
        return result;
    }
}
