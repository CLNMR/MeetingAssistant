using System.Windows;
using System.Windows.Controls;
using MeetingAssistant.App.ViewModels;
using MeetingAssistant.Core.Assist;

namespace MeetingAssistant.App.Views;

/// Spec 03 §9.2 · Import Recording sheet, shown as a modal window over the main window.
public partial class ImportRecordingView : UserControl
{
    public ImportRecordingView() => InitializeComponent();

    void Cancel_Click(object sender, RoutedEventArgs e) => Window.GetWindow(this)?.Close();

    /// Opens the sheet; it closes when the import finished (the review then opens) or on Cancel.
    public static void Show(AssistController assist)
    {
        var vm = new ImportRecordingViewModel(assist);
        var w = new Window
        {
            Title = "Import Recording",
            Content = new ImportRecordingView { DataContext = vm },
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current.MainWindow is { IsVisible: true } m ? m : null,
        };
        w.SetResourceReference(FrameworkElement.StyleProperty, "Aya.Window");
        vm.Finished += w.Close;
        w.ShowDialog();
    }
}
