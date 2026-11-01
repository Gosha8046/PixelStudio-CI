using System.Collections;
using PixelStudio.Core.Imaging;

namespace PixelStudio.Core;

/// <summary>Result of <see cref="LayerManager.MergeDown"/>.</summary>
/// <param name="MergedLayer">The surviving (lower) layer that now holds the merged pixels.</param>
/// <param name="VisuallyIdentical">
/// True when the full composite of every frame is unchanged (within ±1 per channel of
/// rounding). It can be false when the upper layer uses Multiply/Screen over transparent
/// parts of the lower layer while other layers lie below, or when the lower layer itself is
/// not Normal: a single Normal layer cannot express that interaction.
/// </param>
/// <param name="MaxChannelDifference">Largest per-channel difference observed in the composite.</param>
public readonly record struct MergeDownResult(Layer MergedLayer, bool VisuallyIdentical, int MaxChannelDifference);

/// <summary>
/// Operations on the layer stack shared by all frames. Index 0 is the bottom layer.
/// Every operation is recorded as one undo step.
/// </summary>
public sealed class LayerManager : IReadOnlyList<Layer>
{
    private readonly SpriteDocument _doc;

    internal LayerManager(SpriteDocument doc)
    {
        _doc = doc;
    }

    public int Count => _doc.LayersInternal.Count;

    public Layer this[int index] => _doc.LayersInternal[index];

    public IEnumerator<Layer> GetEnumerator() => _doc.LayersInternal.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Adds an empty layer (a transparent cel in every frame).</summary>
    /// <param name="index">Stack position; defaults to the top.</param>
    public Layer AddLayer(string? name = null, int? index = null)
    {
        EnsureCanAdd();
        var layer = new Layer(_doc.AllocateId(), name ?? NextDefaultName());
        int insertAt = Math.Clamp(index ?? Count, 0, Count);
        _doc.ApplyStructuralChange("Новый слой", () =>
        {
            _doc.LayersInternal.Insert(insertAt, layer);
            foreach (var frame in _doc.FramesInternal)
            {
                frame.CelsInternal[layer.Id] = _doc.NewCanvas();
            }
        });
        return layer;
    }

    /// <summary>
    /// Duplicates a layer directly above the original. Every cel is deep-copied, so editing
    /// the copy never affects the original.
    /// </summary>
    public Layer DuplicateLayer(int layerId)
    {
        EnsureCanAdd();
        var source = _doc.FindLayer(layerId);
        int index = _doc.IndexOfLayer(layerId);
        var copy = new Layer(_doc.AllocateId(), source.Name + " копия")
        {
            Visible = source.Visible,
            Opacity = source.Opacity,
            BlendMode = source.BlendMode,
        };

        _doc.ApplyStructuralChange("Дублировать слой", () =>
        {
            _doc.LayersInternal.Insert(index + 1, copy);
            foreach (var frame in _doc.FramesInternal)
            {
                frame.CelsInternal[copy.Id] = frame.GetCel(layerId).Clone();
            }
        });
        return copy;
    }

    public void RemoveLayer(int layerId)
    {
        if (Count <= 1)
        {
            throw new InvalidOperationException("Нельзя удалить единственный слой.");
        }

        int index = _doc.IndexOfLayer(layerId);
        if (index < 0)
        {
            throw new KeyNotFoundException($"Layer {layerId} not found.");
        }

        _doc.ApplyStructuralChange("Удалить слой", () =>
        {
            _doc.LayersInternal.RemoveAt(index);
            foreach (var frame in _doc.FramesInternal)
            {
                frame.CelsInternal.Remove(layerId);
            }
        });
    }

    public void MoveLayer(int layerId, int newIndex)
    {
        int index = _doc.IndexOfLayer(layerId);
        if (index < 0)
        {
            throw new KeyNotFoundException($"Layer {layerId} not found.");
        }

        newIndex = Math.Clamp(newIndex, 0, Count - 1);
        if (newIndex == index)
        {
            return;
        }

        _doc.ApplyStructuralChange("Переместить слой", () =>
        {
            var layer = _doc.LayersInternal[index];
            _doc.LayersInternal.RemoveAt(index);
            _doc.LayersInternal.Insert(newIndex, layer);
        });
    }

    public void Rename(int layerId, string name)
    {
        var layer = _doc.FindLayer(layerId);
        _doc.ApplyStructuralChange("Переименовать слой", () => layer.Name = name);
    }

    public void SetVisible(int layerId, bool visible)
    {
        var layer = _doc.FindLayer(layerId);
        _doc.ApplyStructuralChange(visible ? "Показать слой" : "Скрыть слой", () => layer.Visible = visible);
    }

    /// <summary>Sets opacity (0..100 %). Consecutive calls for the same layer merge into one undo step.</summary>
    public void SetOpacity(int layerId, int opacityPercent)
    {
        var layer = _doc.FindLayer(layerId);
        _doc.ApplyStructuralChange("Прозрачность слоя", () => layer.Opacity = opacityPercent, $"layer-opacity:{layerId}");
    }

    public void SetBlendMode(int layerId, BlendMode mode)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        var layer = _doc.FindLayer(layerId);
        _doc.ApplyStructuralChange("Режим смешивания", () => layer.BlendMode = mode);
    }

    public bool CanMergeDown(int layerId)
    {
        int index = _doc.IndexOfLayer(layerId);
        return index > 0 && _doc.LayersInternal[index].Visible && _doc.LayersInternal[index - 1].Visible;
    }

    /// <summary>
    /// Merges a layer into the layer below it, in every frame. Both layers must be visible.
    /// For each frame the merged cel is the isolated composite of [lower (its opacity), upper
    /// (its opacity and blend mode)] over transparency; the surviving layer keeps the lower
    /// layer's id and name and gets Normal / 100 %. This preserves the visual result exactly
    /// for Normal layers and whenever nothing visible lies below; otherwise the result
    /// reports <see cref="MergeDownResult.VisuallyIdentical"/> = false so the UI can warn.
    /// </summary>
    public MergeDownResult MergeDown(int layerId)
    {
        int upperIndex = _doc.IndexOfLayer(layerId);
        if (upperIndex < 0)
        {
            throw new KeyNotFoundException($"Layer {layerId} not found.");
        }

        if (upperIndex == 0)
        {
            throw new InvalidOperationException("Под нижним слоем нет слоя для объединения.");
        }

        var upper = _doc.LayersInternal[upperIndex];
        var lower = _doc.LayersInternal[upperIndex - 1];
        if (!upper.Visible || !lower.Visible)
        {
            throw new InvalidOperationException("Для объединения оба слоя должны быть видимыми.");
        }

        var before = _doc.FramesInternal.Select(f => Compositor.CompositeFrame(_doc, f)).ToArray();

        _doc.ApplyStructuralChange("Объединить с нижним", () =>
        {
            foreach (var frame in _doc.FramesInternal)
            {
                // New canvas: structural operations never mutate existing pixel buffers.
                var merged = _doc.NewCanvas();
                Compositor.BlendLayer(merged.Pixels, frame.GetCel(lower.Id).Pixels, lower.Opacity, lower.BlendMode);
                Compositor.BlendLayer(merged.Pixels, frame.GetCel(upper.Id).Pixels, upper.Opacity, upper.BlendMode);
                frame.CelsInternal[lower.Id] = merged;
                frame.CelsInternal.Remove(upper.Id);
            }

            lower.Opacity = 100;
            lower.BlendMode = BlendMode.Normal;
            _doc.LayersInternal.Remove(upper);
        });

        // ApplyStructuralChange restores fresh Layer objects only on undo/redo; here `lower` is live.
        int maxDiff = 0;
        for (int i = 0; i < before.Length; i++)
        {
            var after = Compositor.CompositeFrame(_doc, _doc.FramesInternal[i]);
            maxDiff = Math.Max(maxDiff, MaxChannelDifference(before[i].Pixels, after.Pixels));
        }

        return new MergeDownResult(lower, maxDiff <= 1, maxDiff);
    }

    /// <summary>
    /// Largest difference between two images measured on premultiplied channels (0..255),
    /// i.e. weighted by coverage: color noise in nearly transparent pixels is invisible
    /// and must not count as a visual change.
    /// </summary>
    internal static int MaxChannelDifference(ReadOnlySpan<Color32> a, ReadOnlySpan<Color32> b)
    {
        int max = 0;
        for (int i = 0; i < a.Length; i++)
        {
            var p = a[i];
            var q = b[i];
            if (p.Equals(q) || (p.A == 0 && q.A == 0))
            {
                continue;
            }

            max = Math.Max(max, Math.Abs(p.A - q.A));
            max = Math.Max(max, Premultiplied(p.R, p.A, q.R, q.A));
            max = Math.Max(max, Premultiplied(p.G, p.A, q.G, q.A));
            max = Math.Max(max, Premultiplied(p.B, p.A, q.B, q.A));
        }

        return max;

        static int Premultiplied(byte c1, byte a1, byte c2, byte a2) =>
            (int)Math.Round(Math.Abs(c1 * a1 - c2 * a2) / 255.0);
    }

    private void EnsureCanAdd()
    {
        if (Count >= DocumentLimits.MaxLayers)
        {
            throw new InvalidOperationException($"Достигнут предел: {DocumentLimits.MaxLayers} слоя.");
        }
    }

    private string NextDefaultName()
    {
        int n = Count + 1;
        while (_doc.LayersInternal.Any(l => l.Name == $"Слой {n}"))
        {
            n++;
        }

        return $"Слой {n}";
    }
}
