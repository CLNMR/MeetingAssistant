using CommunityToolkit.Mvvm.ComponentModel;
using ObservableObject = CommunityToolkit.Mvvm.ComponentModel.ObservableObject;
using CommunityToolkit.Mvvm.Input;
using MeetingAssistant.Core.Assist;
using MeetingAssistant.Core.Aya;
using MeetingAssistant.Core.Claude;

namespace MeetingAssistant.App.ViewModels;

/// A list slot row in Settings › Assist (spec 03 §11 "Lists").
public sealed partial class ListSlotViewModel : ObservableObject
{
    readonly AssistConnection _aya;
    public ListSlotViewModel(AssistConnection aya, AyaSlot slot) { _aya = aya; Slot = slot; }

    public AyaSlot Slot { get; }
    public string Title => Slot.Title();
    public string Explanation => Slot.Explanation();

    /// Before the catalogue loaded: the current value as plain text.
    public bool HasCatalog => _aya.Specs.Count > 0;
    public string CurrentText => _aya.Config.List(Slot) is { } r ? $"{r.Label} · {r.WorkspaceLabel}" : "—";

    /// "—" plus every record type, keyed "workspace/techName".
    public IReadOnlyList<Choice> Choices =>
        new[] { new Choice("", "—") }.Concat(AssistUi.ListChoices(_aya.Specs, _aya.Workspaces).Select(x => new Choice(Key(x.Spec), x.Title))).ToList();

    static string Key(AyaRecordSpec s) => $"{s.WorkspaceId ?? ""}/{s.TechName}";

    public object? Selected
    {
        get => _aya.Config.List(Slot) is { } r ? $"{r.WorkspaceId}/{r.TechName}" : "";
        set
        {
            if (value is not string k || _aya.Specs.FirstOrDefault(s => Key(s) == k) is not { } spec) return;
            AssistUiHelpers.Fire(() => _aya.SetListAsync(Slot, spec));
        }
    }

    public void Changed()
    {
        OnPropertyChanged(nameof(HasCatalog)); OnPropertyChanged(nameof(CurrentText)); OnPropertyChanged(nameof(Choices)); OnPropertyChanged(nameof(Selected));
    }
}

/// A link picker row (relation field of a slot, or None).
public sealed partial class LinkViewModel : ObservableObject
{
    readonly AssistConnection _aya;
    readonly AyaSlot _slot;
    readonly Func<AyaConfig, string?> _get;
    readonly Action<AyaConfig, string?> _set;

    public LinkViewModel(AssistConnection aya, string title, AyaSlot slot, Func<AyaConfig, string?> get, Action<AyaConfig, string?> set, string? noneHint = null)
    {
        _aya = aya; Title = title; _slot = slot; _get = get; _set = set; NoneHint = noneHint;
    }

    public string Title { get; }
    public string? NoneHint { get; }
    public bool ShowHint => NoneHint is not null && _get(_aya.Config) is null;

    public IReadOnlyList<Choice> Choices =>
        new[] { new Choice(null, "None") }.Concat((_aya.Schema(_slot)?.Relations ?? new()).Select(f => new Choice(f.Key, f.Label))).ToList();

    public object? Selected
    {
        get => _get(_aya.Config);
        set { var v = value as string; if (v != _get(_aya.Config)) _aya.UpdateConfig(c => _set(c, v)); }
    }

    public void Changed() { OnPropertyChanged(nameof(Choices)); OnPropertyChanged(nameof(Selected)); OnPropertyChanged(nameof(ShowHint)); }
}

/// <summary>
/// Settings › Assist (spec 03 §11, 04 §6.6): the Aya connection, the lists and links, Claude Code, model and
/// update frequency. Everything saves on change (through <see cref="AssistConnection"/>).
/// </summary>
public sealed partial class AssistSettingsViewModel : ObservableObject
{
    readonly AssistConnection _aya;

    public AssistSettingsViewModel(AssistController assist)
    {
        _aya = assist.Aya;
        Form = new AyaConnectionForm(_aya);
        Slots = AyaSlots.All.Select(s => new ListSlotViewModel(_aya, s)).ToList();
        Links = new[]
        {
            new LinkViewModel(_aya, "Contact → client", AyaSlot.Contacts, c => c.ContactsClientField, (c, v) => c.ContactsClientField = v),
            new LinkViewModel(_aya, "Meeting → client", AyaSlot.Meetings, c => c.MeetingClientField, (c, v) => c.MeetingClientField = v,
                "Without a link, earlier meetings are found by the client's name."),
            new LinkViewModel(_aya, "Meeting → project", AyaSlot.Meetings, c => c.MeetingProjectField, (c, v) => c.MeetingProjectField = v),
            new LinkViewModel(_aya, "Meeting → template", AyaSlot.Meetings, c => c.MeetingTemplateField, (c, v) => c.MeetingTemplateField = v),
            new LinkViewModel(_aya, "Meeting → team members", AyaSlot.Meetings, c => c.MeetingParticipantsField, (c, v) => c.MeetingParticipantsField = v,
                "Team members who attended are added here, matched by name."),
        };
        _claudePath = _aya.Settings.ClaudePath;
        AssistUiHelpers.Observe(_aya, p =>
        {
            switch (p)
            {
                case nameof(AssistConnection.Config):
                case nameof(AssistConnection.Schemas):
                case nameof(AssistConnection.Specs):
                case nameof(AssistConnection.Workspaces):
                case nameof(AssistConnection.HasApiKey):
                    foreach (var s in Slots) s.Changed();
                    foreach (var l in Links) l.Changed();
                    OnPropertyChanged(nameof(ShowLists)); OnPropertyChanged(nameof(TablesChoices)); OnPropertyChanged(nameof(TablesField));
                    OnPropertyChanged(nameof(ShowAllListsVisible));
                    break;
                case nameof(AssistConnection.ClaudeBinary): OnPropertyChanged(nameof(ClaudeBinaryText)); break;
                case nameof(AssistConnection.Settings): OnPropertyChanged(nameof(Model)); OnPropertyChanged(nameof(Frequency)); OnPropertyChanged(nameof(FrequencyText)); break;
                case nameof(AssistConnection.ConnectState): RefreshFieldsCommand.NotifyCanExecuteChanged(); break;
            }
        });
    }

    public AssistConnection Aya => _aya;
    public AyaConnectionForm Form { get; }

    /// On appear: check Claude if unknown, load the catalogue if a key and org exist and it is empty.
    public void Appeared()
    {
        if (_aya.ClaudeBinary is null && !_aya.ClaudeState.IsWorking) _ = _aya.CheckClaudeAsync();
        if (_aya.HasApiKey && _aya.Specs.Count == 0 && _aya.Config.OrgId.Length > 0) _ = _aya.LoadCatalogAsync();
    }

    // MARK: Lists and links

    public bool ShowLists => _aya.HasApiKey && _aya.Config.OrgId.Length > 0;
    public IReadOnlyList<ListSlotViewModel> Slots { get; }
    public IReadOnlyList<LinkViewModel> Links { get; }
    public bool ShowAllListsVisible => _aya.Specs.Count == 0;

    [RelayCommand(CanExecute = nameof(NotConnecting))]
    Task RefreshFields() => _aya.RefreshSchemasAsync();
    bool NotConnecting() => !_aya.ConnectState.IsWorking;

    [RelayCommand] Task ShowAllLists() => _aya.LoadCatalogAsync();

    public IReadOnlyList<Choice> TablesChoices =>
        AssistUi.TablesFieldChoices(_aya.Schema(AyaSlot.Meetings), _aya.Config).Select(x => new Choice(x.Value, x.Title)).ToList();

    public object? TablesField
    {
        get => _aya.Config.MeetingTablesField;
        set { var v = value as string; if (v != _aya.Config.MeetingTablesField) _aya.UpdateConfig(c => c.MeetingTablesField = v); }
    }

    // MARK: Claude

    public string ClaudeBinaryText => _aya.ClaudeBinary ?? "not found";

    /// Path (optional): overrides detection; saved on change.
    [ObservableProperty] string _claudePath;
    partial void OnClaudePathChanged(string value) => _aya.Settings = _aya.Settings with { ClaudePath = value.Trim() };

    [RelayCommand] Task Check() => _aya.CheckClaudeAsync();

    public IReadOnlyList<Choice> Models { get; } = ClaudeCli.Models.Select(m => new Choice(m.Id, m.Title)).ToList();
    public object? Model
    {
        get => _aya.Settings.Model;
        set { if (value is string m && m != _aya.Settings.Model) _aya.Settings = _aya.Settings with { Model = m }; }
    }

    public IReadOnlyList<Choice> Frequencies { get; } =
        Enum.GetValues<AssistFrequency>().Select(f => new Choice(f, AssistSettings.FrequencyTitle(f))).ToList();
    public object? Frequency
    {
        get => _aya.Settings.Frequency;
        set
        {
            if (value is not AssistFrequency f || f == _aya.Settings.Frequency) return;
            var s = _aya.Settings with { };
            s.Frequency = f;
            _aya.Settings = s;
        }
    }

    public string FrequencyText => AssistSettings.FrequencyExplanation(_aya.Settings.Frequency) +
        " Assist uses your Claude subscription through Claude Code, not the API: each update counts against your plan's usage limits.";
}
