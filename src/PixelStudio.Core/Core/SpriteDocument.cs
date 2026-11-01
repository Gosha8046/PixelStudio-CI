using PixelStudio.Core.Animation;
using PixelStudio.Core.History;
using PixelStudio.Core.Imaging;

namespace PixelStudio.Core;

/// <summary>
/// The sprite document: fixed canvas size, a layer stack shared by all frames, the frame
/// list with per-(frame, layer) pixel data, playback settings, palette and undo history.
/// </summary>
/// <remarks>
/// Layer order: index 0 is the <b>bottom</b> layer; compositing goes from index 0 upward.
/// All mutations go through <see cref="Layers"/> (<see cref="LayerManager"/>),
/// <see cref="Timeline"/>, palette methods or pixel commands, so each is undoable.
/// </remarks>
public sealed class SpriteDocument
{
    private int _nextId = 1;

    private SpriteDocument(int width, int height)
    {
        if (!DocumentLimits.IsValidSize(width, height))
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                $"Размер холста должен быть от {DocumentLimits.MinSize} до {DocumentLimits.MaxSize} пикселей по каждой стороне.");
        }

        Width = width;
        Height = height;
        Layers = new LayerManager(this);
        Timeline = new Timeline(this);
        Palette = new Palette(Palette.Default16);
    }

    public int Width { get; }
    public int Height { get; }

    public LayerManager Layers { get; }
    public Timeline Timeline { get; }
    public Palette Palette { get; }
    public UndoHistory History { get; } = new();

    public IReadOnlyList<Frame> Frames => FramesInternal;

    internal List<Layer> LayersInternal { get; } = [];
    internal List<Frame> FramesInternal { get; } = [];

    /// <summary>Raised after any change (pixels, structure, undo/redo).</summary>
    public event Action? Changed;

    /// <summary>Creates a document with one transparent layer and one frame.</summary>
    public static SpriteDocument Create(int width, int height)
    {
        var doc = new SpriteDocument(width, height);
        var layer = new Layer(doc.AllocateId(), "Слой 1");
        doc.LayersInternal.Add(layer);
        var frame = new Frame(doc.AllocateId(), null, new Dictionary<int, Canvas> { [layer.Id] = doc.NewCanvas() });
        doc.FramesInternal.Add(frame);
        return doc;
    }

    /// <summary>Used by the project loader: builds a document from already-validated parts.</summary>
    internal static SpriteDocument CreateFromParts(
        int width,
        int height,
        IEnumerable<Layer> layers,
        IEnumerable<Frame> frames,
        int fps,
        bool loop,
        IEnumerable<Color32> palette)
    {
        var doc = new SpriteDocument(width, height);
        doc.LayersInternal.AddRange(layers);
        doc.FramesInternal.AddRange(frames);
        doc.Timeline.FpsInternal = fps;
        doc.Timeline.LoopInternal = loop;
        doc.Palette.ColorsInternal.Clear();
        doc.Palette.ColorsInternal.AddRange(palette.Take(DocumentLimits.MaxPaletteColors));
        int maxId = doc.LayersInternal.Select(l => l.Id).Concat(doc.FramesInternal.Select(f => f.Id)).DefaultIfEmpty(0).Max();
        doc._nextId = maxId + 1;
        return doc;
    }

    public Canvas GetCel(int frameId, int layerId) => FindFrame(frameId).GetCel(layerId);

    public Frame FindFrame(int frameId) =>
        FramesInternal.Find(f => f.Id == frameId) ?? throw new KeyNotFoundException($"Frame {frameId} not found.");

    public Layer FindLayer(int layerId) =>
        LayersInternal.Find(l => l.Id == layerId) ?? throw new KeyNotFoundException($"Layer {layerId} not found.");

    public int IndexOfLayer(int layerId) => LayersInternal.FindIndex(l => l.Id == layerId);

    public int IndexOfFrame(int frameId) => FramesInternal.FindIndex(f => f.Id == frameId);

    public bool Undo()
    {
        if (!History.Undo(this))
        {
            return false;
        }

        RaiseChanged();
        return true;
    }

    public bool Redo()
    {
        if (!History.Redo(this))
        {
            return false;
        }

        RaiseChanged();
        return true;
    }

    // ----- Palette (undoable like any other structural change) -----

    public void AddPaletteColor(Color32 color)
    {
        if (Palette.ColorsInternal.Count >= DocumentLimits.MaxPaletteColors || Palette.ColorsInternal.Contains(color))
        {
            return;
        }

        ApplyStructuralChange("Цвет в палитру", () => Palette.ColorsInternal.Add(color));
    }

    public void RemovePaletteColorAt(int index)
    {
        if ((uint)index >= (uint)Palette.ColorsInternal.Count)
        {
            return;
        }

        ApplyStructuralChange("Удалить цвет палитры", () => Palette.ColorsInternal.RemoveAt(index));
    }

    // ----- Internal helpers used by LayerManager / Timeline / commands -----

    internal int AllocateId() => _nextId++;

    internal Canvas NewCanvas() => new(Width, Height);

    /// <summary>
    /// Runs a structural mutation and records it as one undo step. The mutation must not
    /// modify pixels of existing canvases (see <see cref="DocumentStructure"/>).
    /// </summary>
    internal void ApplyStructuralChange(string label, Action mutate, string? mergeKey = null)
    {
        var before = DocumentStructure.Capture(this);
        mutate();
        var after = DocumentStructure.Capture(this);
        if (before.StructurallyEquals(after))
        {
            return;
        }

        History.Push(new StructureCommand(label, before, after, mergeKey));
        RaiseChanged();
    }

    /// <summary>Records an already-applied pixel edit.</summary>
    public void PushPixelEdit(PixelEditCommand command)
    {
        History.Push(command);
        RaiseChanged();
    }

    internal void RaiseChanged() => Changed?.Invoke();
}
