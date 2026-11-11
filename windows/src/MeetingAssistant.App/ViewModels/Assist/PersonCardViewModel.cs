using CommunityToolkit.Mvvm.ComponentModel;
using ObservableObject = CommunityToolkit.Mvvm.ComponentModel.ObservableObject;
using CommunityToolkit.Mvvm.Input;
using MeetingAssistant.Core.Assist;
using MeetingAssistant.Core.Aya;

namespace MeetingAssistant.App.ViewModels;

/// <summary>
/// The person popover (spec 03 §6.7): what Aya knows about a person (the "important" fields), and for speakers
/// a rename box with suggestions. Generic names never look anything up.
/// </summary>
public sealed partial class PersonCardViewModel : ScreenViewModel
{
    readonly Func<List<string>> _suggestions;
    readonly Action<string>? _rename;
    PersonCard? _card;

    /// <param name="lookup">Meeting contacts, then team members (AssistPeople.Card).</param>
    /// <param name="rename">Null when renaming is not offered.</param>
    public PersonCardViewModel(string name, string? contactID, string? label, Func<string, string?, PersonCard?> lookup, AssistConnection aya,
                               Func<List<string>> suggestions, Action<string>? rename)
    {
        Name = name;
        Label = label;
        _suggestions = suggestions;
        _rename = rename;
        if (!SpeakerNames.IsGeneric(name))
        {
            _card = lookup(name, contactID);
            if (_card is null && contactID is not null) _ = LoadAsync(aya, contactID);
        }
    }

    public override string ScreenName => "Person";

    /// The name as clicked.
    public string Name { get; }
    /// The transcript label ("Speaker 2"), when known.
    public string? Label { get; }

    /// Raised after a save so the popup closes.
    public event Action? Closed;

    async Task LoadAsync(AssistConnection aya, string id)
    {
        var c = await AssistPeople.LoadContactAsync(aya.Service, id, aya.Schema(AyaSlot.Contacts));
        AssistUiHelpers.Ui(() => { _card = c; CardChanged(); });
    }

    void CardChanged()
    {
        OnPropertyChanged(nameof(DisplayName)); OnPropertyChanged(nameof(Initial)); OnPropertyChanged(nameof(IsKnown));
        OnPropertyChanged(nameof(Subtitle)); OnPropertyChanged(nameof(Details)); OnPropertyChanged(nameof(HasDetails));
    }

    public string DisplayName => _card?.Name ?? Name;
    public string Initial => AssistUi.Initial(DisplayName);
    /// Aya knows the person (teal avatar).
    public bool IsKnown => _card is not null;
    public string Subtitle => AssistPeople.Subtitle(Name, Label, _card);
    public IReadOnlyList<LabeledValue> Details => _card?.Details ?? new List<LabeledValue>();
    public bool HasDetails => Details.Count > 0;

    // MARK: Rename

    public bool CanRename => _rename is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Hits))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    string _newName = "";

    /// Up to 8 suggestions, filtered by what is typed.
    public IReadOnlyList<string> Hits => AssistUi.FilterSuggestions(_suggestions(), Name, NewName);

    bool CanSave() => NewName.Trim().Length > 0;

    /// Save (or Return in the field): trims, renames and closes.
    [RelayCommand(CanExecute = nameof(CanSave))]
    void Save() => Commit(NewName);

    /// A suggestion chip saves immediately.
    [RelayCommand]
    void Pick(string name) => Commit(name);

    void Commit(string n)
    {
        var t = n.Trim();
        if (t.Length == 0 || _rename is null) return;
        _rename(t);
        Closed?.Invoke();
    }
}
