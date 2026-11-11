using CommunityToolkit.Mvvm.ComponentModel;
using ObservableObject = CommunityToolkit.Mvvm.ComponentModel.ObservableObject;
using MeetingAssistant.Core.Assist;

namespace MeetingAssistant.App.ViewModels;

/// How a table cell is shown (spec 03 §9.3).
public enum CellKind { Answer, OpenKey, Text }

/// One cell. Answer and open-key cells are edited in place; <see cref="Commit"/> reports a changed value once.
public sealed partial class TableCellViewModel : ObservableObject
{
    readonly Action<string>? _commit;

    public TableCellViewModel(string column, CellKind kind, bool isFirstKey, Action<string>? commit)
    {
        Column = column; Kind = kind; IsFirstKey = isFirstKey; _commit = commit;
    }

    public string Column { get; }
    public CellKind Kind { get; }
    public bool IsFirstKey { get; }
    public bool IsEditable => Kind != CellKind.Text;

    /// The editable value (for open keys: the part after the fixed prefix).
    [ObservableProperty] string _value = "";
    /// The fixed prefix of an open key ("Own proposal:").
    [ObservableProperty] string _prefix = "";
    /// A value from Claude (non-empty and not user-set): agent 10 % fill.
    [ObservableProperty] bool _fromClaude;
    /// Claude's evidence quote as tooltip, or null.
    [ObservableProperty] string? _evidence;

    /// Called by the cell field on Return or focus loss; commits only when the trimmed value changed.
    public void Commit(string text)
    {
        var t = text.Trim();
        if (t == Value) return;
        _commit?.Invoke(t);
    }
}

/// One row of the grid.
public sealed partial class TableRowViewModel : ObservableObject
{
    public TableRowViewModel(string id, IReadOnlyList<TableCellViewModel> cells) { Id = id; Cells = cells; }
    public string Id { get; }
    public IReadOnlyList<TableCellViewModel> Cells { get; }
    /// Changed by the latest live update: agent 10 % row background.
    [ObservableProperty] bool _isChanged;
}

/// A column header: name and whether it is an answer column (max 180 px wide).
public sealed record TableColumn(string Name, bool IsAnswer);

/// <summary>
/// The grid of one template table for the live sheet and the review (spec 03 §6.5, §8.3, §9.3). Row and cell
/// view models stay the same across updates so a field being typed in keeps its text.
/// </summary>
public sealed partial class TableGridViewModel : ObservableObject
{
    readonly Func<TemplateTable?> _table;
    readonly Func<ISet<string>> _changed;
    readonly Action<string, string, string> _set;
    readonly Dictionary<string, TableRowViewModel> _rows = new();

    /// <param name="table">The current table (re-read on every update).</param>
    /// <param name="changed">Ids the latest update changed ("t:&lt;row&gt;").</param>
    /// <param name="set">Writes a cell (row id, column name, value).</param>
    /// <param name="subscribe">Registers a callback for table updates.</param>
    public TableGridViewModel(Func<TemplateTable?> table, Func<ISet<string>> changed, Action<string, string, string> set, bool onlyAnswered,
                              Action<Action>? subscribe = null)
    {
        _table = table; _changed = changed; _set = set; _onlyAnswered = onlyAnswered;
        subscribe?.Invoke(() => AssistUiHelpers.Ui(Refresh));
        Refresh();
    }

    [ObservableProperty] string _name = "";
    [ObservableProperty] string _answeredText = "";
    [ObservableProperty] string _prompt = "";
    [ObservableProperty] IReadOnlyList<TableColumn> _columns = Array.Empty<TableColumn>();
    [ObservableProperty] IReadOnlyList<TableRowViewModel> _visibleRows = Array.Empty<TableRowViewModel>();
    /// "Only answered rows".
    [ObservableProperty] bool _onlyAnswered;
    public bool IsEmpty => VisibleRows.Count == 0;

    partial void OnOnlyAnsweredChanged(bool value) => Refresh();
    partial void OnVisibleRowsChanged(IReadOnlyList<TableRowViewModel> value) => OnPropertyChanged(nameof(IsEmpty));

    /// Re-reads the table and updates the rows in place.
    public void Refresh()
    {
        if (_table() is not { } t) return;
        Name = AssistUi.TableName(t);
        AnsweredText = $"{t.FilledCount}/{t.Rows.Count} answered";
        Prompt = t.Prompt;
        var cols = t.Columns.Select((c, i) => new TableColumn(c, t.AnswerColumns.Contains(i))).ToList();
        if (!cols.SequenceEqual(Columns)) { Columns = cols; _rows.Clear(); }
        var changed = _changed();
        var firstKey = t.KeyColumns.Count > 0 ? t.KeyColumns[0] : -1;
        var visible = new List<TableRowViewModel>();
        foreach (var r in t.Rows)
        {
            if (!_rows.TryGetValue(r.Id, out var vm))
            {
                var rowId = r.Id;
                var cells = t.Columns.Select((c, i) =>
                {
                    var kind = t.AnswerColumns.Contains(i) ? CellKind.Answer : r.OpenKeys.ContainsKey(i) ? CellKind.OpenKey : CellKind.Text;
                    return new TableCellViewModel(c, kind, i == firstKey, kind == CellKind.Text ? null : v => _set(rowId, c, v));
                }).ToList();
                vm = _rows[r.Id] = new TableRowViewModel(r.Id, cells);
            }
            for (int i = 0; i < vm.Cells.Count; i++)
            {
                var cell = vm.Cells[i];
                var text = i < r.Cells.Count ? r.Cells[i] : "";
                if (cell.Kind == CellKind.OpenKey && r.OpenKeys.TryGetValue(i, out var prefix))
                {
                    cell.Prefix = prefix;
                    cell.Value = (text.StartsWith(prefix, StringComparison.Ordinal) ? text[prefix.Length..] : text).Trim();
                }
                else cell.Value = text;
                cell.FromClaude = cell.Kind == CellKind.Answer && text.Length > 0 && !r.UserSet.Contains(cell.Column);
                cell.Evidence = cell.Kind == CellKind.Answer && r.Evidence.TryGetValue(cell.Column, out var ev) ? $"“{ev}”" : null;
            }
            vm.IsChanged = changed.Contains($"t:{r.Id}");
            if (!OnlyAnswered || t.IsFilled(r)) visible.Add(vm);
        }
        if (!visible.SequenceEqual(VisibleRows)) VisibleRows = visible;
    }
}
