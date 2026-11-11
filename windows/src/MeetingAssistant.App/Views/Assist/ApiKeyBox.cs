using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using MeetingAssistant.App.Controls;
using MeetingAssistant.App.ViewModels;

namespace MeetingAssistant.App.Views;

/// <summary>
/// The Aya API key field (spec 03 §4, §11): a PasswordBox with a key icon and the placeholder "stored securely"
/// or "aya_key_…". Its DataContext is an <see cref="AyaConnectionForm"/>; the typed key goes to
/// <see cref="AyaConnectionForm.TypedKey"/> and the box is cleared after every connect attempt.
/// </summary>
public sealed class ApiKeyBox : UserControl
{
    readonly PasswordBox _box = new();
    readonly TextBlock _placeholder = new() { FontSize = 13, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(29, 0, 10, 0) };
    AyaConnectionForm? _form;

    public ApiKeyBox()
    {
        Ui.SetIcon(_box, "\uE8D7");   // key
        _placeholder.SetResourceReference(TextBlock.ForegroundProperty, "TextSubtleBrush");
        AutomationProperties.SetName(_box, "API key");
        var grid = new Grid();
        grid.Children.Add(_box);
        grid.Children.Add(_placeholder);
        Content = grid;
        _box.PasswordChanged += (_, _) =>
        {
            if (_form is not null && _form.TypedKey != _box.Password) _form.TypedKey = _box.Password;
            Refresh();
        };
        _box.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Return && _form?.ConnectCommand.CanExecute(null) == true) _form.ConnectCommand.Execute(null);
        };
        DataContextChanged += (_, _) => Attach(DataContext as AyaConnectionForm);
    }

    void Attach(AyaConnectionForm? form)
    {
        if (_form is not null) { _form.KeyCleared -= Clear; _form.PropertyChanged -= FormChanged; }
        _form = form;
        if (form is null) return;
        form.KeyCleared += Clear;
        form.PropertyChanged += FormChanged;
        Refresh();
    }

    void FormChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AyaConnectionForm.KeyPlaceholder)) Refresh();
    }

    void Clear() => _box.Password = "";

    void Refresh()
    {
        _placeholder.Text = _form?.KeyPlaceholder ?? "aya_key_…";
        _placeholder.Visibility = _box.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
