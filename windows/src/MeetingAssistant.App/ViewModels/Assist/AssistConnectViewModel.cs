using CommunityToolkit.Mvvm.ComponentModel;
using ObservableObject = CommunityToolkit.Mvvm.ComponentModel.ObservableObject;
using CommunityToolkit.Mvvm.Input;
using MeetingAssistant.Core.Assist;

namespace MeetingAssistant.App.ViewModels;

/// <summary>
/// Organization id + API key + Connect, shared by the Connect screen (spec 03 §4) and Settings › Assist (§11).
/// The key lives in a PasswordBox; the view reports it through <see cref="TypedKey"/> and clears the box on
/// <see cref="KeyCleared"/> (after every attempt).
/// </summary>
public sealed partial class AyaConnectionForm : ObservableObject
{
    readonly AssistConnection _aya;

    public AyaConnectionForm(AssistConnection aya)
    {
        _aya = aya;
        _orgId = aya.Config.OrgId;
        AssistUiHelpers.Observe(aya, p =>
        {
            if (p is nameof(AssistConnection.HasApiKey) or nameof(AssistConnection.Config) or nameof(AssistConnection.ConnectState))
            {
                OnPropertyChanged(nameof(KeyPlaceholder));
                OnPropertyChanged(nameof(IsConnected));
                OnPropertyChanged(nameof(ConnectTitle));
                OnPropertyChanged(nameof(StatusText));
                ConnectCommand.NotifyCanExecuteChanged();
            }
        });
    }

    /// The connection (bind ConnectState, HasApiKey through it).
    public AssistConnection Aya => _aya;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    string _orgId;

    /// What the user typed into the key field (never stored here beyond the attempt).
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    string _typedKey = "";

    /// Raised after each connect attempt so the view clears the key field.
    public event Action? KeyCleared;

    /// "stored securely" when a key exists, else "aya_key_…" (Windows wording; the Mac says "stored in the Keychain").
    public string KeyPlaceholder => _aya.HasApiKey ? "stored securely" : "aya_key_…";
    public bool IsConnected => _aya.IsConnected;
    /// "Connect", or "Reconnect" when connected (Settings).
    public string ConnectTitle => _aya.IsConnected ? "Reconnect" : "Connect";
    /// Settings status line.
    public string StatusText => _aya.IsConnected ? "Connected · lists, schemas and recaps come from your workspaces" : "Not connected";

    bool CanConnect() => OrgId.Trim().Length > 0 && (TypedKey.Length > 0 || _aya.HasApiKey) && !_aya.ConnectState.IsWorking;

    /// Connect flow (spec 03 §4).
    [RelayCommand(CanExecute = nameof(CanConnect))]
    async Task Connect()
    {
        var key = TypedKey;
        TypedKey = "";
        KeyCleared?.Invoke();
        await _aya.ConnectAsync(OrgId, key);
    }

    /// Remove Key: deletes the key, clears caches and lists; the org id is kept.
    [RelayCommand]
    void RemoveKey() => _aya.Disconnect();
}

/// Assist "Connect Aya" screen (spec 03 §4): a centred card on the backdrop.
public sealed class AssistConnectViewModel(AssistController assist) : ScreenViewModel
{
    public override string ScreenName => "Connect";
    public AyaConnectionForm Form { get; } = new(assist.Aya);
}
