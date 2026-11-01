namespace PixelStudio.Core.Animation;

/// <summary>
/// Deterministic playback state machine: feed it elapsed time, it tells which frame to
/// show. Kept free of timers so the host (browser or desktop) can drive it with any clock
/// and so it can be unit-tested.
/// </summary>
public sealed class PlaybackClock
{
    private double _elapsedInFrame;

    public int FrameIndex { get; private set; }

    public bool IsPlaying { get; private set; }

    public void Start(int fromFrame)
    {
        FrameIndex = Math.Max(0, fromFrame);
        _elapsedInFrame = 0;
        IsPlaying = true;
    }

    public void Pause() => IsPlaying = false;

    /// <summary>
    /// Advances playback by <paramref name="elapsedMs"/>. Large steps skip as many frames as
    /// needed (the clock never drifts). Without looping playback stops on the last frame.
    /// </summary>
    /// <returns><c>true</c> if <see cref="FrameIndex"/> changed.</returns>
    public bool Advance(double elapsedMs, IReadOnlyList<int> durationsMs, bool loop)
    {
        if (!IsPlaying || durationsMs.Count == 0 || elapsedMs <= 0)
        {
            return false;
        }

        int start = FrameIndex;
        if (FrameIndex >= durationsMs.Count)
        {
            FrameIndex = 0;
        }

        // Bound the work for pathological inputs (e.g. the tab was suspended for hours).
        double cycle = 0;
        foreach (var d in durationsMs)
        {
            cycle += Math.Max(1, d);
        }

        // Whole cycles land on the same frame, so only the remainder matters when looping.
        _elapsedInFrame += loop ? elapsedMs % cycle : elapsedMs;
        while (_elapsedInFrame >= Math.Max(1, durationsMs[FrameIndex]))
        {
            _elapsedInFrame -= Math.Max(1, durationsMs[FrameIndex]);
            if (FrameIndex + 1 < durationsMs.Count)
            {
                FrameIndex++;
            }
            else if (loop)
            {
                FrameIndex = 0;
            }
            else
            {
                _elapsedInFrame = 0;
                IsPlaying = false;
                break;
            }
        }

        return FrameIndex != start;
    }

    /// <summary>Milliseconds until the next frame change (for scheduling timers).</summary>
    public double MillisecondsUntilNextFrame(IReadOnlyList<int> durationsMs)
    {
        if (durationsMs.Count == 0)
        {
            return 16;
        }

        int index = Math.Min(FrameIndex, durationsMs.Count - 1);
        return Math.Max(1, durationsMs[index] - _elapsedInFrame);
    }
}
