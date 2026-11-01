namespace PixelStudio.Core.History;

/// <summary>
/// Linear undo/redo stack with a size limit and "save point" tracking for the
/// unsaved-changes indicator.
/// </summary>
public sealed class UndoHistory
{
    private readonly List<Entry> _undo = [];
    private readonly List<Entry> _redo = [];
    private long _nextId = 1;

    /// <summary>Id of the top undo entry at the last save; 0 = empty stack; -1 = unreachable.</summary>
    private long _savedTopId;
    private bool _topMergeable;

    public UndoHistory(int limit = 256)
    {
        Limit = Math.Max(1, limit);
    }

    public int Limit { get; }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public int UndoCount => _undo.Count;
    public int RedoCount => _redo.Count;
    public string? UndoLabel => CanUndo ? _undo[^1].Command.Label : null;
    public string? RedoLabel => CanRedo ? _redo[^1].Command.Label : null;

    /// <summary>True when the document differs from the last saved (or freshly created/opened) state.</summary>
    public bool IsDirty => TopId != _savedTopId;

    private long TopId => _undo.Count > 0 ? _undo[^1].Id : 0;

    /// <summary>Records a command that has already been applied to the document.</summary>
    public void Push(IUndoableCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        _redo.Clear();

        // Merge only into the entry pushed immediately before (no undo/redo/save/BreakMerge
        // in between) and never into the saved entry: the merged state would differ from it.
        if (_topMergeable && command is StructureCommand next && _undo.Count > 0 &&
            _undo[^1].Command is StructureCommand top && _undo[^1].Id != _savedTopId &&
            top.TryMerge(next))
        {
            return;
        }

        _topMergeable = command is StructureCommand { MergeKey: not null };
        _undo.Add(new Entry(_nextId++, command));
        if (_undo.Count > Limit)
        {
            var removed = _undo[0];
            _undo.RemoveAt(0);

            // The oldest state is gone for good: adjust the save point accordingly.
            if (_savedTopId == 0)
            {
                _savedTopId = -1;
            }
            else if (_savedTopId == removed.Id)
            {
                _savedTopId = 0;
            }
        }
    }

    public bool Undo(SpriteDocument document)
    {
        if (_undo.Count == 0)
        {
            return false;
        }

        _topMergeable = false;
        var entry = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        entry.Command.Undo(document);
        _redo.Add(entry);
        return true;
    }

    public bool Redo(SpriteDocument document)
    {
        if (_redo.Count == 0)
        {
            return false;
        }

        _topMergeable = false;
        var entry = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        entry.Command.Redo(document);
        _undo.Add(entry);
        return true;
    }

    public void MarkSaved()
    {
        _savedTopId = TopId;
        _topMergeable = false;
    }

    /// <summary>Ends the current merge group (e.g. when a slider drag is released).</summary>
    public void BreakMerge() => _topMergeable = false;

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        _savedTopId = 0;
        _topMergeable = false;
    }

    private readonly record struct Entry(long Id, IUndoableCommand Command);
}
