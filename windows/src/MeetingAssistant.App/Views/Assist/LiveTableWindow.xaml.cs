using System.Windows;
using MeetingAssistant.App.ViewModels;

namespace MeetingAssistant.App.Views;

/// Spec 03 §6.5 · Live table sheet: the full table during the meeting; corrections go to Claude, which keeps them.
public partial class LiveTableWindow : Window
{
    public LiveTableWindow(TableGridViewModel grid)
    {
        InitializeComponent();
        DataContext = grid;
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
