using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MeetingAssistant.App.Views;

/// Assist layout and router (spec 03 §2).
public partial class AssistView : UserControl
{
    public AssistView()
    {
        InitializeComponent();
        // ClipToBounds clips to the rectangle; round it to radius 16 like the Mac's clipShape.
        RouterHost.SizeChanged += (_, e) =>
            RouterHost.Clip = new RectangleGeometry(new Rect(e.NewSize), 16, 16);
    }
}
