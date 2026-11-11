using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MeetingAssistant.App.ViewModels;
using MeetingAssistant.Core.Assist;

namespace MeetingAssistant.App.Views;

/// <summary>
/// Spec 03 §6 · Live screen. The view applies the layout rules of <see cref="LiveLayout"/>: panel width shares
/// (15 % clamps), the panels' height (the upper area scrolls only when too small), the transcript height, and
/// opens the person popovers and the live table sheet.
/// </summary>
public partial class AssistLiveView : UserControl
{
    AssistLiveViewModel? _vm;
    double _dragStartX, _dragStartY, _dragStartHeight;
    (double Summary, double Open) _dragStartShares;

    public AssistLiveView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as AssistLiveViewModel);
        Loaded += (_, _) =>
        {
            _vm?.Appeared();
            Dispatcher.BeginInvoke(() => TranscriptScroll.ScrollToEnd(), DispatcherPriority.Loaded);
        };
        Unloaded += (_, _) => _vm?.Disappeared();
        SizeChanged += (_, _) => { ApplyTranscriptHeight(); ApplyPanelHeight(); };
        Upper.SizeChanged += (_, _) => ApplyPanelHeight();
        QuestionCard.SizeChanged += (_, _) => ApplyPanelHeight();
        TablesCard.SizeChanged += (_, _) => ApplyPanelHeight();
        Panels.SizeChanged += (_, _) => ApplyWidths();

        Handle1.DragStarted += (_, _) => { _dragStartX = Mouse.GetPosition(Panels).X; _dragStartShares = _vm?.Shares ?? default; };
        Handle1.DragDelta += (_, _) => DragShares(afterSummary: true);
        Handle2.DragStarted += (_, _) => { _dragStartX = Mouse.GetPosition(Panels).X; _dragStartShares = _vm?.Shares ?? default; };
        Handle2.DragDelta += (_, _) => DragShares(afterSummary: false);
        HeightHandle.DragStarted += (_, _) => { _dragStartY = Mouse.GetPosition(this).Y; _dragStartHeight = TranscriptRow.Height.Value; };
        HeightHandle.DragDelta += (_, _) =>
        {
            if (_vm is null) return;
            var h = LiveLayout.TranscriptHeight(_dragStartHeight - (Mouse.GetPosition(this).Y - _dragStartY), ActualHeight);
            _vm.SetTranscriptHeight(h);
            ApplyTranscriptHeight();
        };
        AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(ButtonClicked));
    }

    void Attach(AssistLiveViewModel? vm)
    {
        if (_vm is not null) _vm.PropertyChanged -= VmChanged;
        _vm = vm;
        if (vm is null) return;
        vm.PropertyChanged += VmChanged;
        ApplyWidths();
        ApplyTranscriptHeight();
    }

    void VmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AssistLiveViewModel.Shares):
            case nameof(AssistLiveViewModel.HasChecklist): ApplyWidths(); break;
            case nameof(AssistLiveViewModel.HasTables): Dispatcher.BeginInvoke(ApplyPanelHeight, DispatcherPriority.Loaded); break;
            case nameof(AssistLiveViewModel.Lines):
                // Follow the newest line.
                Dispatcher.BeginInvoke(() => TranscriptScroll.ScrollToEnd(), DispatcherPriority.Loaded);
                break;
            case nameof(AssistLiveViewModel.ScrollToParagraph):
                if (_vm?.ScrollToParagraph is { } i) Dispatcher.BeginInvoke(() => CenterParagraph(i), DispatcherPriority.Loaded);
                break;
        }
    }

    // MARK: Layout

    void ApplyWidths()
    {
        if (_vm is null || Panels.ActualWidth <= 0) return;
        var hasChecklist = _vm.HasChecklist;
        var avail = LiveLayout.Available(Panels.ActualWidth, hasChecklist);
        var (sh, oh) = _vm.Shares;
        SummaryCol.Width = new GridLength(avail * sh);
        if (hasChecklist)
        {
            OpenCol.Width = new GridLength(avail * oh);
            HandleCol2.Width = new GridLength(LiveLayout.HandleThickness);
            ChecklistCol.Width = new GridLength(1, GridUnitType.Star);
        }
        else
        {
            OpenCol.Width = new GridLength(1, GridUnitType.Star);
            HandleCol2.Width = new GridLength(0);
            ChecklistCol.Width = new GridLength(0);
        }
    }

    void DragShares(bool afterSummary)
    {
        if (_vm is null) return;
        var avail = LiveLayout.Available(Panels.ActualWidth, _vm.HasChecklist);
        var delta = (Mouse.GetPosition(Panels).X - _dragStartX) / avail;
        _vm.SetShares(afterSummary
            ? LiveLayout.DragAfterSummary(_dragStartShares, _dragStartShares.Summary, delta)
            : LiveLayout.DragAfterOpen(_dragStartShares, _dragStartShares.Open, delta));
    }

    void ApplyPanelHeight()
    {
        if (_vm is null) return;
        var hasTables = _vm.HasTables;
        var h = LiveLayout.PanelHeight(Upper.ActualHeight, QuestionCard.ActualHeight, hasTables ? TablesCard.ActualHeight : 0, hasTables);
        if (Math.Abs(Panels.Height - h) > 0.5) Panels.Height = h;
        var rest = Upper.ActualHeight - QuestionCard.ActualHeight - 8 - (hasTables ? TablesCard.ActualHeight + 8 : 0);
        Upper.VerticalScrollBarVisibility = rest < LiveLayout.MinPanelHeight ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden;
    }

    void ApplyTranscriptHeight()
    {
        if (_vm is null || ActualHeight <= 0) return;
        TranscriptRow.Height = new GridLength(LiveLayout.TranscriptHeight(_vm.StoredTranscriptHeight, ActualHeight));
    }

    void CenterParagraph(int index)
    {
        if (SummaryList.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement el || SummaryScroll.Content is not Visual content) return;
        var y = el.TransformToAncestor(content).Transform(new Point(0, 0)).Y;
        SummaryScroll.ScrollToVerticalOffset(Math.Max(0, y - (SummaryScroll.ViewportHeight - el.ActualHeight) / 2));
    }

    // MARK: Clicks: popovers, the table sheet, "What did I miss?"

    void ButtonClicked(object sender, RoutedEventArgs e)
    {
        if (_vm is null || e.OriginalSource is not Button b) return;
        switch (b.Tag as string, b.DataContext)
        {
            case ("person", AttendeeChip chip):
                PersonPopup.Show(b, _vm.PersonFor(chip), PlacementMode.Bottom);
                break;
            case ("speaker", LiveLineItem line) when _vm.PersonFor(line) is { } p:
                PersonPopup.Show(b, p, PlacementMode.Right);
                break;
            case ("table", TableTile tile):
                new LiveTableWindow(_vm.TableGrid(tile.Id)) { Owner = Window.GetWindow(this) }.ShowDialog();
                break;
        }
    }

    void Miss_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var menu = new ContextMenu { PlacementTarget = MissButton, Placement = PlacementMode.Bottom };
        foreach (var m in new[] { "2", "5", "10" })
            menu.Items.Add(new MenuItem { Header = $"Last {m} minutes", Command = _vm.WhatDidIMissCommand, CommandParameter = m });
        menu.IsOpen = true;
    }
}
