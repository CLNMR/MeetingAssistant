using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;
using MeetingAssistant.App.ViewModels;

namespace MeetingAssistant.App.Views;

/// Spec 03 §6.7 · Person popover. The avatar is teal when Aya knows the person.
public partial class PersonCard : UserControl
{
    public PersonCard()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Update();
    }

    void Update()
    {
        if (DataContext is not PersonCardViewModel vm) return;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PersonCardViewModel.IsKnown)) Paint(vm); };
        Paint(vm);
    }

    void Paint(PersonCardViewModel vm)
    {
        Avatar.SetResourceReference(Border.BackgroundProperty, vm.IsKnown ? "AgentBrush" : "PrimaryTint12Brush");
        Ini.SetResourceReference(TextBlock.ForegroundProperty, vm.IsKnown ? "AgentForegroundBrush" : "PrimaryBrush");
    }
}

/// Shows a <see cref="PersonCard"/> as a light-dismiss popup next to an element (spec 03 §6.7; Windows: Flyout-like Popup).
public static class PersonPopup
{
    /// Opens the popover below (attendee chips) or to the right (speakers) of <paramref name="target"/>.
    public static void Show(UIElement target, PersonCardViewModel vm, PlacementMode placement)
    {
        var card = new PersonCard { DataContext = vm };
        var box = new Border
        {
            Child = card, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Margin = new Thickness(8, 0, 8, 12),
            Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 4, Direction = 270, Opacity = 0.14 },
        };
        box.SetResourceReference(Border.BackgroundProperty, "PopoverBrush");
        box.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        TextElement.SetForeground(box, (Brush?)Application.Current.TryFindResource("TextBrush") ?? Brushes.Black);
        var popup = new Popup
        {
            Child = box, PlacementTarget = target, Placement = placement, StaysOpen = false, AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade, VerticalOffset = placement == PlacementMode.Bottom ? 4 : 0,
            HorizontalOffset = placement == PlacementMode.Right ? 4 : 0,
        };
        vm.Closed += () => popup.IsOpen = false;
        popup.Opened += (_, _) => { if (vm.CanRename) card.NameBox.Focus(); };
        popup.IsOpen = true;
    }
}
