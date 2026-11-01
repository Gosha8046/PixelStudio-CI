using PixelStudio.Core.Imaging;

namespace PixelStudio.Core.History;

/// <summary>An already-applied, reversible change to a <see cref="SpriteDocument"/>.</summary>
public interface IUndoableCommand
{
    /// <summary>Short user-facing description, e.g. "Карандаш" or "Новый слой".</summary>
    string Label { get; }

    void Undo(SpriteDocument document);

    void Redo(SpriteDocument document);
}

/// <summary>
/// Pixel change inside one cel. Stores only the bounding rectangle of the change
/// (before and after), so a one-pixel dot costs a few bytes regardless of canvas size.
/// One drawing gesture (pointer down → up) produces exactly one such command.
/// </summary>
public sealed class PixelEditCommand : IUndoableCommand
{
    private readonly Color32[] _before;
    private readonly Color32[] _after;

    public PixelEditCommand(string label, int frameId, int layerId, PixelRect rect, Color32[] before, Color32[] after)
    {
        if (before.Length != rect.Area || after.Length != rect.Area)
        {
            throw new ArgumentException("Snapshot sizes must match the rectangle area.");
        }

        Label = label;
        FrameId = frameId;
        LayerId = layerId;
        Rect = rect;
        _before = before;
        _after = after;
    }

    public string Label { get; }
    public int FrameId { get; }
    public int LayerId { get; }
    public PixelRect Rect { get; }

    public void Undo(SpriteDocument document) => document.GetCel(FrameId, LayerId).WriteRect(Rect, _before);

    public void Redo(SpriteDocument document) => document.GetCel(FrameId, LayerId).WriteRect(Rect, _after);
}

/// <summary>
/// Any change to the document structure (layers, frames, properties, FPS, palette),
/// stored as before/after <see cref="DocumentStructure"/> snapshots.
/// </summary>
internal sealed class StructureCommand : IUndoableCommand
{
    private DocumentStructure _after;

    public StructureCommand(string label, DocumentStructure before, DocumentStructure after, string? mergeKey)
    {
        Label = label;
        Before = before;
        _after = after;
        MergeKey = mergeKey;
    }

    public string Label { get; }
    public DocumentStructure Before { get; }

    /// <summary>
    /// Consecutive commands with the same non-null key collapse into one history entry
    /// (e.g. dragging an opacity slider produces one undo step, not hundreds).
    /// </summary>
    public string? MergeKey { get; }

    public bool TryMerge(StructureCommand next)
    {
        if (MergeKey is null || MergeKey != next.MergeKey)
        {
            return false;
        }

        _after = next._after;
        return true;
    }

    public void Undo(SpriteDocument document) => Before.Restore(document);

    public void Redo(SpriteDocument document) => _after.Restore(document);
}
