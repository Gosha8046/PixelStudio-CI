namespace PixelStudio.Core.Animation;

/// <summary>
/// One animation frame: its own duration override and one <see cref="Canvas"/> ("cel")
/// per layer of the shared layer stack, keyed by <see cref="Layer.Id"/>.
/// </summary>
public sealed class Frame
{
    internal Frame(int id, int? durationMs, Dictionary<int, Canvas> cels)
    {
        Id = id;
        DurationMs = durationMs;
        CelsInternal = cels;
    }

    /// <summary>Document-unique, stable identifier.</summary>
    public int Id { get; }

    /// <summary>
    /// Per-frame duration in milliseconds, or <c>null</c> to use the timeline default
    /// derived from FPS (<see cref="Timeline.DefaultFrameDurationMs"/>).
    /// </summary>
    public int? DurationMs { get; internal set; }

    internal Dictionary<int, Canvas> CelsInternal { get; }

    public IReadOnlyDictionary<int, Canvas> Cels => CelsInternal;

    public Canvas GetCel(int layerId) =>
        CelsInternal.TryGetValue(layerId, out var canvas)
            ? canvas
            : throw new KeyNotFoundException($"Frame {Id} has no cel for layer {layerId}.");
}
