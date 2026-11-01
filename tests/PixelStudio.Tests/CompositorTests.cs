using PixelStudio.Core.Imaging;

namespace PixelStudio.Tests;

/// <summary>
/// Expected values were computed independently with exact rational arithmetic
/// (Python fractions) from the W3C formulas documented on <see cref="Compositor"/>.
/// </summary>
public class CompositorTests
{
    public static TheoryData<Color32, Color32, int, BlendMode, Color32> KnownValues => new()
    {
        // backdrop, source, opacity %, mode, expected
        { new(0, 0, 255, 255), new(255, 0, 0, 128), 100, BlendMode.Normal, new(128, 0, 127, 255) },
        { new(200, 100, 50, 255), new(128, 128, 128, 255), 100, BlendMode.Multiply, new(100, 50, 25, 255) },
        { new(200, 100, 50, 255), new(128, 128, 128, 255), 100, BlendMode.Screen, new(228, 178, 153, 255) },
        { new(0, 0, 0, 255), new(255, 255, 255, 255), 50, BlendMode.Normal, new(128, 128, 128, 255) },
        { new(0, 0, 255, 128), new(255, 0, 0, 128), 100, BlendMode.Normal, new(170, 0, 85, 192) },
        { new(0, 0, 255, 128), new(255, 0, 0, 128), 100, BlendMode.Multiply, new(85, 0, 85, 192) },
        { new(0, 0, 255, 128), new(255, 0, 0, 128), 100, BlendMode.Screen, new(170, 0, 170, 192) },
        { new(255, 255, 255, 255), new(255, 0, 0, 255), 100, BlendMode.Multiply, new(255, 0, 0, 255) },
        { new(0, 0, 0, 255), new(0, 128, 255, 255), 100, BlendMode.Screen, new(0, 128, 255, 255) },
        { new(40, 80, 120, 255), new(200, 150, 100, 200), 75, BlendMode.Multiply, new(35, 61, 77, 255) },
        { new(40, 80, 120, 255), new(200, 150, 100, 200), 75, BlendMode.Screen, new(139, 141, 151, 255) },
    };

    [Theory]
    [MemberData(nameof(KnownValues))]
    public void Blend_MatchesReferenceValues(Color32 backdrop, Color32 source, int opacity, BlendMode mode, Color32 expected)
    {
        Assert.Equal(expected, Compositor.Blend(backdrop, source, opacity, mode));

        // The bulk path (with its fast paths) must agree with the scalar path.
        Span<Color32> buffer = [backdrop];
        Compositor.BlendLayer(buffer, [source], opacity, mode);
        Assert.Equal(expected, buffer[0]);
    }

    [Theory]
    [InlineData(BlendMode.Normal)]
    [InlineData(BlendMode.Multiply)]
    [InlineData(BlendMode.Screen)]
    public void Blend_OverTransparentBackdropReturnsSourceWithEffectiveAlpha(BlendMode mode)
    {
        var result = Compositor.Blend(Color32.Transparent, new Color32(10, 20, 30, 255), 50, mode);
        Assert.Equal(new Color32(10, 20, 30, 128), result);
    }

    [Fact]
    public void Blend_TransparentSourceOrZeroOpacityLeavesBackdrop()
    {
        var backdrop = new Color32(1, 2, 3, 4);
        Assert.Equal(backdrop, Compositor.Blend(backdrop, new Color32(255, 255, 255, 0), 100, BlendMode.Screen));
        Assert.Equal(backdrop, Compositor.Blend(backdrop, new Color32(255, 255, 255, 255), 0, BlendMode.Normal));
    }

    [Fact]
    public void CompositeFrame_GoesBottomToTopAndSkipsHiddenLayers()
    {
        var doc = SpriteDocument.Create(1, 1);
        var bottom = doc.Layers[0];
        var middle = doc.Layers.AddLayer("middle");
        var top = doc.Layers.AddLayer("top");
        var frame = doc.Frames[0];
        frame.GetCel(bottom.Id).Pixels[0] = new Color32(200, 100, 50);
        frame.GetCel(middle.Id).Pixels[0] = new Color32(128, 128, 128);
        frame.GetCel(top.Id).Pixels[0] = new Color32(0, 255, 0, 255);
        doc.Layers.SetBlendMode(middle.Id, BlendMode.Multiply);

        doc.Layers.SetVisible(top.Id, false);
        Assert.Equal(new Color32(100, 50, 25), Compositor.CompositeFrame(doc, frame).Pixels[0]);

        doc.Layers.SetVisible(top.Id, true);
        Assert.Equal(new Color32(0, 255, 0), Compositor.CompositeFrame(doc, frame).Pixels[0]);

        // Order matters: moving the opaque green layer to the bottom lets multiply act on it.
        doc.Layers.MoveLayer(top.Id, 0);
        Assert.Equal(new Color32(100, 50, 25), Compositor.CompositeFrame(doc, frame).Pixels[0]);
    }
}
