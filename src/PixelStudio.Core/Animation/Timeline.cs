using System.Collections;

namespace PixelStudio.Core.Animation;

/// <summary>
/// Frame order, frame durations and playback settings. Every operation is one undo step.
/// </summary>
/// <remarks>
/// Durations: each frame either has its own <see cref="Frame.DurationMs"/> or uses
/// <see cref="DefaultFrameDurationMs"/> = round(1000 / FPS). The exporter always writes
/// the effective duration in milliseconds.
/// </remarks>
public sealed class Timeline : IReadOnlyList<Frame>
{
    private readonly SpriteDocument _doc;

    internal Timeline(SpriteDocument doc)
    {
        _doc = doc;
    }

    public int Count => _doc.FramesInternal.Count;

    public Frame this[int index] => _doc.FramesInternal[index];

    public int Fps => FpsInternal;

    /// <summary>Whether playback (and exported animation metadata) loops.</summary>
    public bool Loop => LoopInternal;

    internal int FpsInternal { get; set; } = 8;

    internal bool LoopInternal { get; set; } = true;

    public int DefaultFrameDurationMs => (int)Math.Round(1000.0 / Fps, MidpointRounding.AwayFromZero);

    public int GetDurationMs(Frame frame) => frame.DurationMs ?? DefaultFrameDurationMs;

    public int TotalDurationMs => _doc.FramesInternal.Sum(GetDurationMs);

    public IEnumerator<Frame> GetEnumerator() => _doc.FramesInternal.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Adds an empty frame (transparent cels for every layer).</summary>
    public Frame AddFrame(int? index = null)
    {
        EnsureCanAdd();
        var cels = _doc.LayersInternal.ToDictionary(l => l.Id, _ => _doc.NewCanvas());
        var frame = new Frame(_doc.AllocateId(), null, cels);
        int insertAt = Math.Clamp(index ?? Count, 0, Count);
        _doc.ApplyStructuralChange("Новый кадр", () => _doc.FramesInternal.Insert(insertAt, frame));
        return frame;
    }

    /// <summary>
    /// Inserts a deep copy right after the source frame. The copy owns new pixel arrays,
    /// so drawing on it never changes the original.
    /// </summary>
    public Frame DuplicateFrame(int frameId)
    {
        EnsureCanAdd();
        var source = _doc.FindFrame(frameId);
        int index = _doc.IndexOfFrame(frameId);
        var cels = source.CelsInternal.ToDictionary(kv => kv.Key, kv => kv.Value.Clone());
        var copy = new Frame(_doc.AllocateId(), source.DurationMs, cels);
        _doc.ApplyStructuralChange("Дублировать кадр", () => _doc.FramesInternal.Insert(index + 1, copy));
        return copy;
    }

    public void RemoveFrame(int frameId)
    {
        if (Count <= 1)
        {
            throw new InvalidOperationException("Нельзя удалить единственный кадр.");
        }

        int index = _doc.IndexOfFrame(frameId);
        if (index < 0)
        {
            throw new KeyNotFoundException($"Frame {frameId} not found.");
        }

        _doc.ApplyStructuralChange("Удалить кадр", () => _doc.FramesInternal.RemoveAt(index));
    }

    public void MoveFrame(int frameId, int newIndex)
    {
        int index = _doc.IndexOfFrame(frameId);
        if (index < 0)
        {
            throw new KeyNotFoundException($"Frame {frameId} not found.");
        }

        newIndex = Math.Clamp(newIndex, 0, Count - 1);
        if (newIndex == index)
        {
            return;
        }

        _doc.ApplyStructuralChange("Переместить кадр", () =>
        {
            var frame = _doc.FramesInternal[index];
            _doc.FramesInternal.RemoveAt(index);
            _doc.FramesInternal.Insert(newIndex, frame);
        });
    }

    /// <summary>Sets a per-frame duration, or <c>null</c> to follow the FPS default.</summary>
    public void SetFrameDuration(int frameId, int? durationMs)
    {
        if (durationMs is { } ms && ms is < DocumentLimits.MinFrameDurationMs or > DocumentLimits.MaxFrameDurationMs)
        {
            throw new ArgumentOutOfRangeException(
                nameof(durationMs),
                $"Длительность кадра: {DocumentLimits.MinFrameDurationMs}–{DocumentLimits.MaxFrameDurationMs} мс.");
        }

        var frame = _doc.FindFrame(frameId);
        _doc.ApplyStructuralChange("Длительность кадра", () => frame.DurationMs = durationMs, $"frame-duration:{frameId}");
    }

    public void SetFps(int fps)
    {
        fps = Math.Clamp(fps, DocumentLimits.MinFps, DocumentLimits.MaxFps);
        _doc.ApplyStructuralChange("FPS", () => FpsInternal = fps, "fps");
    }

    public void SetLoop(bool loop) =>
        _doc.ApplyStructuralChange(loop ? "Зациклить" : "Без зацикливания", () => LoopInternal = loop);

    /// <summary>Effective duration of every frame, in playback order.</summary>
    public int[] GetDurations() => _doc.FramesInternal.Select(GetDurationMs).ToArray();

    private void EnsureCanAdd()
    {
        if (Count >= DocumentLimits.MaxFrames)
        {
            throw new InvalidOperationException($"Достигнут предел: {DocumentLimits.MaxFrames} кадров.");
        }
    }
}
