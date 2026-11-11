using System.Windows;
using System.Windows.Controls;
using MeetingAssistant.App.ViewModels;

namespace MeetingAssistant.App.Shell;

/// A modal alert with a title, a selectable message and Aya-styled buttons. Returns the clicked
/// button's index, or -1 when closed with Escape / the window's close button.
public partial class AlertWindow : Window
{
    public int Result { get; private set; } = -1;

    public AlertWindow(string windowTitle, string heading, string message, IReadOnlyList<AlertButton> buttons)
    {
        InitializeComponent();
        Title = windowTitle;
        Heading.Text = heading;
        Heading.Visibility = string.IsNullOrEmpty(heading) ? Visibility.Collapsed : Visibility.Visible;
        Message.Text = message;
        SourceInitialized += (_, _) => Themes.ThemeManager.ApplyTitleBar(this);
        // Right-aligned like the Mac: the default (primary) button last.
        for (int i = buttons.Count - 1; i >= 0; i--)
        {
            var b = buttons[i];
            int index = i;
            var btn = new Button
            {
                Content = b.Text,
                IsDefault = b.IsDefault,
                IsCancel = b.IsCancel,
                MinWidth = 80,
                Margin = new Thickness(8, 0, 0, 0),
                Style = (Style)FindResource(b.Kind switch
                {
                    AlertButtonKind.Primary => "Aya.Button.Primary",
                    AlertButtonKind.Destructive => "Aya.Button.Destructive",
                    _ => "Aya.Button.Secondary",
                }),
            };
            btn.Click += (_, _) => { Result = index; DialogResult = true; };
            Buttons.Children.Add(btn);
        }
    }
}
