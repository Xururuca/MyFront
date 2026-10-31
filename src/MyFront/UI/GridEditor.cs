using System.Globalization;
using MyFront.Core.Db;

namespace MyFront.UI;

internal enum EditState { Clean, Dirty, Saving, Error, New }

internal sealed class EditRow
{
    public EditRow(object?[] values, bool isNew = false)
    {
        Values = values;
        IsNew = isNew;
        State = isNew ? EditState.New : EditState.Clean;
    }

    /// <summary>The values the grid shows; edits and saved values are written into this array.</summary>
    public object?[] Values;
    /// <summary>Values as loaded, kept from the first edit until the row is posted (used in the WHERE clause).</summary>
    public object?[]? Original;
    public EditState State;
    public string? Error;
    public bool IsNew;
}

/// <summary>Rows a <see cref="GridEditor"/> edits.</summary>
internal interface IEditRows : IRowSource
{
    /// <summary>The edit state of a row, or null while it has none (a result row that was never edited).</summary>
    EditRow? Find(int index);
    /// <summary>The edit state of a row, made at its first edit.</summary>
    EditRow Edit(int index);
    /// <summary>Where the row is shown now; -1 = not known (repaint everything visible).</summary>
    int IndexOf(EditRow row);
    /// <summary>The row is still one of these (not deleted meanwhile).</summary>
    bool Contains(EditRow row);
}

/// <summary>The Data Browser's page: every row has its edit state.</summary>
internal sealed class EditRowSource : IEditRows
{
    public readonly List<EditRow> Rows = new();
    public int Count => Rows.Count;
    public object?[] GetRow(int index) => Rows[index].Values;
    public EditRow? Find(int index) => Rows[index];
    public EditRow Edit(int index) => Rows[index];
    public int IndexOf(EditRow row) => Rows.IndexOf(row);
    public bool Contains(EditRow row) => Rows.Contains(row);
}

/// <summary>
/// A result set's rows, edited in place in its <see cref="RowStore"/>: only rows that were edited get an
/// <see cref="EditRow"/>, found by the row's values array (a sort moves rows around), so untouched rows cost nothing.
/// </summary>
internal sealed class ResultEditRows : IEditRows
{
    private readonly RowStore _store;
    private readonly Dictionary<object?[], EditRow> _edits = new(ReferenceEqualityComparer.Instance);

    public ResultEditRows(RowStore store) => _store = store;

    public int Count => _store.Count;
    public object?[] GetRow(int index) => _store.GetRow(index);
    public EditRow? Find(int index) => _edits.Count == 0 ? null : _edits.GetValueOrDefault(_store.GetRow(index));

    public EditRow Edit(int index)
    {
        var values = _store.GetRow(index);
        if (!_edits.TryGetValue(values, out var row)) _edits[values] = row = new EditRow(values);
        return row;
    }

    public int IndexOf(EditRow row) => -1;
    public bool Contains(EditRow row) => _edits.TryGetValue(row.Values, out var r) && r == row;

    /// <summary>Rows changed and not saved yet, or that failed to save.</summary>
    public bool HasPendingChanges => _edits.Values.Any(r => r.State is EditState.Dirty or EditState.Error);
}

/// <summary>
/// Cell editing for a <see cref="ResultGrid"/>, shared by the Data Browser and the editable result grids of the SQL
/// editor: typed text goes into the row and the row is marked; Delete / Ctrl+0 set the selected cells to NULL; Ctrl+Z
/// puts back what the last change replaced; Esc reverts the row; the row is saved (<see cref="Save"/>) when you leave it
/// or press Enter. A row that fails keeps its mark and shows the error.
/// </summary>
internal sealed class GridEditor
{
    /// <summary>One change to undo: the cells it changed and what they held before.</summary>
    private sealed record UndoStep(IEditRows Rows, List<(EditRow Row, int Column, object? Before)> Cells);

    private const int MaxUndo = 100;
    private readonly ResultGrid _grid;
    private readonly Func<IEditRows> _rows;
    private readonly Func<bool> _suspended;
    private readonly List<UndoStep> _undo = new(); // the newest last
    private List<(EditRow Row, int Column, object? Before)>? _step;
    private bool _enabled;

    /// <param name="rows">The rows the grid shows now (the Data Browser swaps them per page).</param>
    /// <param name="suspended">True while the owner rebinds the grid: its events are ignored then.</param>
    public GridEditor(ResultGrid grid, Func<IEditRows> rows, Func<bool>? suspended = null)
    {
        _grid = grid;
        _rows = rows;
        _suspended = suspended ?? (() => false);
        grid.CellValuePushed += OnCellValuePushed;
        grid.CellBeginEdit += (_, e) => { if (!IsEditableColumn(e.ColumnIndex)) e.Cancel = true; };
        grid.RowValidated += OnRowValidated;
        grid.RowErrorTextNeeded += (_, e) =>
        {
            if (!_suspended() && e.RowIndex < _rows().Count) e.ErrorText = _rows().Find(e.RowIndex)?.Error ?? "";
        };
        grid.EditingControlShowing += (_, e) =>
        {
            // a NULL cell starts empty, not with the "(NULL)" placeholder
            if (grid.CurrentCell is { } cell && cell.RowIndex < _rows().Count && _rows().GetRow(cell.RowIndex)[cell.ColumnIndex] is null)
                e.Control!.Text = "";
        };
        grid.RowPostPaint += PaintRowState;
        grid.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.D0 && e.Control) SetSelectedNull();
            else if (grid.IsCurrentCellInEditMode) return; // the text box has its own Delete and Ctrl+Z
            else if (e.KeyCode == Keys.Escape) RevertCurrent();
            else if (e.KeyData == Keys.Delete) DeleteKey();
            else if (e.KeyData == (Keys.Control | Keys.Z)) Undo();
            else return;
            e.Handled = true;
        };
        grid.EnterPressed += SaveCurrentIfChanged;
        grid.PasteRequested += Paste;
    }

    /// <summary>Saves a changed row (UPDATE, or INSERT for a new Data Browser row); the bool asks for a message box on
    /// an error. Returns false when it failed (the row stays marked with the error).</summary>
    public Func<EditRow, bool, Task<bool>>? Save { get; set; }

    /// <summary>Cells can be typed in. Off: the grid is read-only.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (_grid.ReadOnly == value) _grid.ReadOnly = !value;
        }
    }

    /// <summary>Grid columns that can be typed in; null = every column the grid doesn't mark read-only.</summary>
    public bool[]? EditableColumns { get; set; }

    /// <summary>Why the grid can't be edited, e.g. "read-only connection" (said when you paste into it).</summary>
    public string ReadOnlyReason { get; set; } = "read-only";

    /// <summary>Esc on a new row that was never saved (the Data Browser removes it).</summary>
    public event Action<int>? NewRowReverted;

    /// <summary>Delete with whole rows selected by their headers: the Data Browser deletes them (after asking). null = no rows are deleted here.</summary>
    public Func<Task>? DeleteRows { get; set; }

    public bool IsEditableColumn(int column) =>
        _enabled && column >= 0 && column < _grid.ColumnCount && !_grid.Columns[column].ReadOnly
        && (EditableColumns is null || column < EditableColumns.Length && EditableColumns[column]);

    private ColumnKind Kind(int column) =>
        column < _grid.ColumnInfos.Length ? _grid.ColumnInfos[column].Kind : ColumnKind.Text;

    private void OnCellValuePushed(object? sender, DataGridViewCellValueEventArgs e)
    {
        if (_suspended() || e.RowIndex >= _rows().Count) return;
        var text = e.Value as string ?? Convert.ToString(e.Value, CultureInfo.InvariantCulture);
        SetValue(e.RowIndex, e.ColumnIndex, text);
    }

    /// <summary>
    /// Puts typed (or pasted) text into a cell; null = NULL. Text equal to what the cell shows changes nothing, and an
    /// empty edit of a NULL cell keeps NULL (Ctrl+0 sets NULL). True when the cell changed.
    /// </summary>
    public bool SetValue(int index, int column, string? text)
    {
        var rows = _rows();
        var old = rows.GetRow(index)[column];
        if (text is null && old is null) return false;
        if (text is not null && old is null && text.Length == 0) return false;
        if (text is not null && old is not null && text == CellFormat.ToDisplay(old, Kind(column))) return false;
        var row = rows.Edit(index);
        if (!row.IsNew && row.Original is null) row.Original = (object?[])row.Values.Clone();
        if (_step is not null) _step.Add((row, column, old));
        else PushUndo(new UndoStep(rows, new() { (row, column, old) }));
        row.Values[column] = text;
        row.State = row.IsNew ? EditState.New : EditState.Dirty;
        row.Error = null;
        _grid.InvalidateRow(index);
        return true;
    }

    /// <summary>Delete: whole rows selected by their headers go to <see cref="DeleteRows"/>; anything else is Set NULL.</summary>
    private void DeleteKey()
    {
        if (DeleteRows is { } delete && _grid.Rows.GetFirstRow(DataGridViewElementStates.Selected) >= 0) Ui.Fire(_grid, delete);
        else SetSelectedNull();
    }

    /// <summary>
    /// Set NULL (Delete, Ctrl+0, right-click), without asking: the selected cells — else the current one — in editable
    /// columns become NULL and each changed row is saved right away (a new row when you leave it, as typing does).
    /// Ctrl+Z puts the values back. Whole rows picked by their headers are left alone.
    /// </summary>
    public void SetSelectedNull()
    {
        if (!_enabled)
        {
            _grid.ReportStatus("Can't set NULL: " + (ReadOnlyReason.Length == 0 ? "read-only" : ReadOnlyReason));
            return;
        }
        if (_grid.IsCurrentCellInEditMode) _grid.CancelEdit();
        _grid.EndEdit();
        if (_grid.Rows.GetFirstRow(DataGridViewElementStates.Selected) >= 0)
        {
            _grid.ReportStatus("Whole rows are selected: select cells to set them to NULL");
            return;
        }
        var cells = _grid.SelectedCells.Cast<DataGridViewCell>().Select(c => (Row: c.RowIndex, Column: c.ColumnIndex)).ToList();
        // a selection is only taken when the current cell is in it (moving the current cell in code leaves the old one)
        if (_grid.CurrentCell is { } current && !cells.Contains((current.RowIndex, current.ColumnIndex))) cells = new() { (current.RowIndex, current.ColumnIndex) };
        var changed = new List<EditRow>();
        int count = 0, readOnly = 0;
        _step = new();
        try
        {
            foreach (var (r, column) in cells.OrderBy(c => c.Row).ThenBy(c => c.Column))
            {
                if (r < 0 || r >= _rows().Count) continue;
                if (!IsEditableColumn(column)) { readOnly++; continue; }
                if (!SetValue(r, column, null)) continue;
                count++;
                var row = _rows().Find(r)!;
                if (!changed.Contains(row)) changed.Add(row);
            }
        }
        finally { EndStep(); }
        var skipped = readOnly == 0 ? "" : $"{readOnly:N0} read-only {(readOnly == 1 ? "cell" : "cells")} left out";
        var text = count == 0 ? (readOnly > 0 ? "Nothing set to NULL: " + skipped : "Already NULL")
            : $"Set {count:N0} {(count == 1 ? "cell" : "cells")} to NULL{(changed.Count > 1 ? $" in {changed.Count:N0} rows" : "")}" +
              (readOnly > 0 ? ", " + skipped : "") + " — Ctrl+Z puts back";
        Ui.Fire(_grid, () => SaveChangedAsync(changed, text));
    }

    // ---- undo ---------------------------------------------------------------------------------

    private void PushUndo(UndoStep step)
    {
        _undo.Add(step);
        if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
    }

    private void EndStep()
    {
        if (_step is { Count: > 0 } cells) PushUndo(new UndoStep(_rows(), cells));
        _step = null;
    }

    /// <summary>There is a change Ctrl+Z can put back (on these rows: a reloaded page has none).</summary>
    public bool CanUndo => _enabled && _undo.Count > 0 && _undo[^1].Rows == _rows();

    /// <summary>
    /// Ctrl+Z: puts back what the last change (typing, paste, Set NULL) replaced and saves those rows again. A change
    /// that is already undone (Esc) or whose rows are gone is skipped; a reloaded page or query has nothing to undo.
    /// </summary>
    public void Undo()
    {
        if (!_enabled || _grid.IsCurrentCellInEditMode) return;
        var rows = _rows();
        while (_undo.Count > 0)
        {
            var step = _undo[^1];
            if (step.Rows != rows) { _undo.Clear(); break; }
            var live = step.Cells.Where(c => rows.Contains(c.Row) && !Equals(c.Row.Values[c.Column], c.Before)).ToList();
            if (live.Count == 0) { _undo.RemoveAt(_undo.Count - 1); continue; }
            if (live.Any(c => c.Row.State == EditState.Saving))
            {
                _grid.ReportStatus("Still saving — press Ctrl+Z again in a moment");
                return;
            }
            _undo.RemoveAt(_undo.Count - 1);
            var changed = new List<EditRow>();
            foreach (var (row, column, before) in Enumerable.Reverse(live))
            {
                if (!row.IsNew && row.Original is null) row.Original = (object?[])row.Values.Clone();
                row.Values[column] = before;
                row.Error = null;
                if (!changed.Contains(row)) changed.Add(row);
            }
            foreach (var row in changed)
            {
                if (row.IsNew) row.State = EditState.New;
                else if (row.Original!.SequenceEqual(row.Values)) { row.Original = null; row.State = EditState.Clean; }
                else row.State = EditState.Dirty;
                Repaint(row);
            }
            Ui.Fire(_grid, () => SaveChangedAsync(changed, $"Undone: {live.Count:N0} {(live.Count == 1 ? "value" : "values")} put back"));
            return;
        }
        _grid.ReportStatus("Nothing to undo");
    }

    /// <summary>
    /// Saves each changed row on its own (a new row waits until you leave it), then says what happened; one row's error
    /// is shown in a message box, as when you leave a typed row.
    /// </summary>
    private async Task SaveChangedAsync(List<EditRow> rows, string text)
    {
        int failed = 0;
        foreach (var row in rows)
            if (!row.IsNew && NeedsSave(row) && Save is { } save && !await save(row, rows.Count == 1)) failed++;
        if (failed > 0) text += $"; {failed:N0} {(failed == 1 ? "row" : "rows")} not saved (marked red, the row header shows the error)";
        _grid.ReportStatus(text);
    }

    /// <summary>Esc: cancels the cell being edited, else puts the row back as it was loaded.</summary>
    public void RevertCurrent()
    {
        if (_grid.CurrentCell is not { } cell || cell.RowIndex >= _rows().Count) return;
        if (_grid.IsCurrentCellInEditMode) { _grid.CancelEdit(); _grid.EndEdit(); return; }
        var row = _rows().Find(cell.RowIndex);
        if (row is null || row.State == EditState.Saving) return;
        if (row.IsNew) { NewRowReverted?.Invoke(cell.RowIndex); return; }
        if (row.Original is not null) Array.Copy(row.Original, row.Values, row.Values.Length);
        row.Original = null;
        row.State = EditState.Clean;
        row.Error = null;
        _grid.InvalidateRow(cell.RowIndex);
    }

    private static bool NeedsSave(EditRow row) =>
        row.State is EditState.Dirty || row.State == EditState.New && row.Values.Any(v => v is not null);

    private void OnRowValidated(object? sender, DataGridViewCellEventArgs e)
    {
        if (_suspended() || !_enabled || e.RowIndex < 0 || e.RowIndex >= _rows().Count) return;
        if (_rows().Find(e.RowIndex) is { } row && NeedsSave(row)) SaveLater(row);
    }

    /// <summary>Enter: the grid commits the cell and moves down (which saves the row); on the last row it can't move.</summary>
    private void SaveCurrentIfChanged()
    {
        if (_suspended() || !_enabled || _grid.IsCurrentCellInEditMode || _grid.CurrentCell is not { } cell || cell.RowIndex >= _rows().Count) return;
        if (_rows().Find(cell.RowIndex) is { } row && NeedsSave(row)) SaveLater(row);
    }

    /// <summary>Ends the cell edit and saves the current row if it changed (before something moves the rows around).</summary>
    public async Task CommitCurrentAsync()
    {
        if (_grid.IsCurrentCellInEditMode) _grid.EndEdit();
        if (_suspended() || !_enabled || Save is null || _grid.CurrentCell is not { } cell || cell.RowIndex >= _rows().Count) return;
        if (_rows().Find(cell.RowIndex) is { } row && NeedsSave(row)) await Save(row, true);
    }

    private void SaveLater(EditRow row)
    {
        if (Save is { } save) Ui.Fire(_grid, () => save(row, true));
    }

    // ---- paste --------------------------------------------------------------------------------

    /// <summary>
    /// Pasted cells go in from the current cell on, left to right and top down (a single value fills every selected
    /// cell), only into existing rows and editable columns, clipped at the grid's edge. They are typed input: the same
    /// conversion, the rows marked, then each changed row saved on its own — a row that fails stays marked, the others
    /// are saved. Read-only grids say so instead.
    /// </summary>
    public void Paste(List<string[]> block)
    {
        if (!_enabled)
        {
            _grid.ReportStatus("Can't paste: " + (ReadOnlyReason.Length == 0 ? "read-only" : ReadOnlyReason));
            return;
        }
        if (_grid.CurrentCell is not { } current || block.Count == 0) return;
        if (_grid.IsCurrentCellInEditMode) _grid.EndEdit();
        var order = _grid.DisplayColumnOrder();
        int start = Array.IndexOf(order, current.ColumnIndex);
        if (start < 0) return;
        var single = block.Count == 1 && block[0].Length == 1;
        var targets = GridClipboard.PasteTargets(block, current.RowIndex, start, _rows().Count, order.Length, single ? SelectedPositions(order) : null);

        var changed = new List<EditRow>();
        var seen = new HashSet<EditRow>(ReferenceEqualityComparer.Instance);
        int cells = 0, readOnly = 0;
        _step = new(); // one Ctrl+Z puts the whole paste back
        try
        {
            foreach (var (r, position, value) in targets)
            {
                int column = order[position];
                if (!IsEditableColumn(column)) { readOnly++; continue; }
                if (!SetValue(r, column, value)) continue;
                cells++;
                var row = _rows().Find(r)!;
                if (seen.Add(row)) changed.Add(row);
            }
        }
        finally { EndStep(); }
        Ui.Fire(_grid, () => SavePastedAsync(changed, cells, readOnly));
    }

    /// <summary>The selected cells as (row, display position): whole selected rows, else the selected cells.</summary>
    private List<(int Row, int Column)> SelectedPositions(int[] order)
    {
        var cells = new List<(int, int)>();
        int r = _grid.Rows.GetFirstRow(DataGridViewElementStates.Selected);
        if (r >= 0)
        {
            for (; r >= 0; r = _grid.Rows.GetNextRow(r, DataGridViewElementStates.Selected))
                for (int c = 0; c < order.Length; c++) cells.Add((r, c));
            return cells;
        }
        foreach (DataGridViewCell cell in _grid.SelectedCells)
            if (Array.IndexOf(order, cell.ColumnIndex) is var c and >= 0) cells.Add((cell.RowIndex, c));
        return cells;
    }

    private async Task SavePastedAsync(List<EditRow> rows, int cells, int readOnly)
    {
        int failed = 0;
        foreach (var row in rows)
            if (NeedsSave(row) && Save is { } save && !await save(row, false)) failed++;
        var skipped = readOnly == 0 ? "" : $"{readOnly:N0} read-only {(readOnly == 1 ? "cell" : "cells")} left out";
        if (cells == 0)
        {
            _grid.ReportStatus(readOnly > 0 ? "Nothing pasted: " + skipped : "Pasted: nothing changed");
            return;
        }
        var text = $"Pasted {cells:N0} {(cells == 1 ? "value" : "values")} into {rows.Count:N0} {(rows.Count == 1 ? "row" : "rows")}";
        if (readOnly > 0) text += ", " + skipped;
        if (failed > 0) text += $"; {failed:N0} {(failed == 1 ? "row" : "rows")} not saved (marked red, the row header shows the error)";
        _grid.ReportStatus(text);
    }

    /// <summary>Repaints a row after its state changed (wherever it is shown now).</summary>
    public void Repaint(EditRow row)
    {
        int i = _rows().IndexOf(row);
        if (i < 0) _grid.Invalidate();
        else if (i < _grid.RowCount) _grid.InvalidateRow(i);
    }

    private void PaintRowState(object? sender, DataGridViewRowPostPaintEventArgs e)
    {
        if (_suspended() || e.RowIndex >= _rows().Count) return;
        var color = _rows().Find(e.RowIndex)?.State switch
        {
            EditState.Dirty => Color.FromArgb(230, 150, 0),
            EditState.New => Icons.Green,
            EditState.Saving => Icons.Blue,
            EditState.Error => Icons.Red,
            _ => Color.Empty,
        };
        if (color.IsEmpty) return;
        using var b = new SolidBrush(color);
        e.Graphics.FillRectangle(b, e.RowBounds.Left, e.RowBounds.Top + 1, _grid.LogicalToDeviceUnits(4), e.RowBounds.Height - 2);
    }
}
