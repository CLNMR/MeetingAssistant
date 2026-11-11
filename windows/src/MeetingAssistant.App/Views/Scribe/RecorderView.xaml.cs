using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using MeetingAssistant.App.ViewModels;

namespace MeetingAssistant.App.Views;

/// Recorder screen (spec 01): settings before recording, timer, meters and live transcript while recording.
public partial class RecorderView : UserControl
{
    RecorderViewModel? _vm;

    public RecorderView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
    }

    void Attach()
    {
        if (_vm is not null) ((INotifyCollectionChanged)_vm.LiveRows).CollectionChanged -= OnRows;
        _vm = DataContext as RecorderViewModel;
        if (_vm is not null) ((INotifyCollectionChanged)_vm.LiveRows).CollectionChanged += OnRows;
    }

    // Auto-scrolls to the newest line.
    void OnRows(object? sender, NotifyCollectionChangedEventArgs e) => Dispatcher.BeginInvoke(() => LiveScroller.ScrollToEnd());

    void OnLoaded(object sender, RoutedEventArgs e) => _vm?.RefreshDevices();

    void Devices_DropDownOpened(object? sender, EventArgs e) => _vm?.RefreshDevices();
}
