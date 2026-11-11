using System.Windows.Controls;
using MeetingAssistant.App.ViewModels;

namespace MeetingAssistant.App.Views;

/// Spec 03 §5 · Prepare screen. Each appearance reloads the planned meetings (and checks Claude when unknown).
public partial class AssistPrepareView : UserControl
{
    public AssistPrepareView()
    {
        InitializeComponent();
        Loaded += (_, _) => (DataContext as AssistPrepareViewModel)?.Appeared();
        Unloaded += (_, _) => (DataContext as AssistPrepareViewModel)?.Disappeared();
    }
}
