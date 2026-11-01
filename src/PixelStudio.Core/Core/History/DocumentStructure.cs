using PixelStudio.Core.Animation;
using PixelStudio.Core.Imaging;

namespace PixelStudio.Core.History;

/// <summary>
/// Cheap snapshot of everything except pixel contents: layer properties and order,
/// frame order/durations, the mapping (frame, layer) → canvas <i>reference</i>, FPS, loop
/// flag and palette. Capturing it is O(layers × frames) and copies no pixels.
/// </summary>
/// <remarks>
/// This is sound because of an invariant enforced across the core: structural operations
/// never mutate an existing <see cref="Canvas"/>; they only add/remove canvases or create
/// new ones (duplicate, merge). Only <see cref="PixelEditCommand"/> mutates pixels in place,
/// and the linear undo history guarantees those edits are undone before any structural
/// snapshot that references the same canvas is restored.
/// </remarks>
internal sealed class DocumentStructure
{
    private DocumentStructure(
        LayerState[] layers,
        FrameState[] frames,
        int fps,
        bool loop,
        Color32[] palette)
    {
        Layers = layers;
        Frames = frames;
        Fps = fps;
        Loop = loop;
        Palette = palette;
    }

    public LayerState[] Layers { get; }
    public FrameState[] Frames { get; }
    public int Fps { get; }
    public bool Loop { get; }
    public Color32[] Palette { get; }

    public static DocumentStructure Capture(SpriteDocument doc) => new(
        doc.LayersInternal.Select(l => l.ToState()).ToArray(),
        doc.FramesInternal.Select(f => new FrameState(f.Id, f.DurationMs, new Dictionary<int, Canvas>(f.CelsInternal))).ToArray(),
        doc.Timeline.Fps,
        doc.Timeline.Loop,
        doc.Palette.ColorsInternal.ToArray());

    public void Restore(SpriteDocument doc)
    {
        doc.LayersInternal.Clear();
        doc.LayersInternal.AddRange(Layers.Select(Layer.FromState));

        doc.FramesInternal.Clear();
        doc.FramesInternal.AddRange(Frames.Select(f => new Frame(f.Id, f.DurationMs, new Dictionary<int, Canvas>(f.Cels))));

        doc.Timeline.FpsInternal = Fps;
        doc.Timeline.LoopInternal = Loop;

        doc.Palette.ColorsInternal.Clear();
        doc.Palette.ColorsInternal.AddRange(Palette);
    }

    public bool StructurallyEquals(DocumentStructure other)
    {
        if (Fps != other.Fps || Loop != other.Loop ||
            !Layers.AsSpan().SequenceEqual(other.Layers) ||
            !Palette.AsSpan().SequenceEqual(other.Palette) ||
            Frames.Length != other.Frames.Length)
        {
            return false;
        }

        for (int i = 0; i < Frames.Length; i++)
        {
            var a = Frames[i];
            var b = other.Frames[i];
            if (a.Id != b.Id || a.DurationMs != b.DurationMs || a.Cels.Count != b.Cels.Count)
            {
                return false;
            }

            foreach (var (layerId, canvas) in a.Cels)
            {
                if (!b.Cels.TryGetValue(layerId, out var otherCanvas) || !ReferenceEquals(canvas, otherCanvas))
                {
                    return false;
                }
            }
        }

        return true;
    }

    internal readonly record struct FrameState(int Id, int? DurationMs, Dictionary<int, Canvas> Cels);
}
