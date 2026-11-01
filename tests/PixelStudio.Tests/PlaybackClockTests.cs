using PixelStudio.Core.Animation;

namespace PixelStudio.Tests;

public class PlaybackClockTests
{
    private static readonly int[] Durations = [100, 50, 200];

    [Fact]
    public void Advance_RespectsPerFrameDurations()
    {
        var clock = new PlaybackClock();
        clock.Start(0);

        Assert.False(clock.Advance(99, Durations, loop: true));
        Assert.Equal(0, clock.FrameIndex);
        Assert.True(clock.Advance(1, Durations, loop: true));
        Assert.Equal(1, clock.FrameIndex);
        Assert.True(clock.Advance(50, Durations, loop: true));
        Assert.Equal(2, clock.FrameIndex);
        Assert.Equal(200, clock.MillisecondsUntilNextFrame(Durations));
    }

    [Fact]
    public void Advance_LoopsAndSkipsFramesOnLongSteps()
    {
        var clock = new PlaybackClock();
        clock.Start(0);

        clock.Advance(350 * 1000 + 120, Durations, loop: true); // 1000 full cycles + 120 ms
        Assert.Equal(1, clock.FrameIndex);
        Assert.True(clock.IsPlaying);
    }

    [Fact]
    public void Advance_WithoutLoopStopsOnLastFrame()
    {
        var clock = new PlaybackClock();
        clock.Start(1);

        clock.Advance(10_000, Durations, loop: false);

        Assert.Equal(2, clock.FrameIndex);
        Assert.False(clock.IsPlaying);
    }

    [Fact]
    public void Pause_FreezesTheFrame()
    {
        var clock = new PlaybackClock();
        clock.Start(0);
        clock.Pause();
        Assert.False(clock.Advance(1000, Durations, loop: true));
        Assert.Equal(0, clock.FrameIndex);
    }
}
