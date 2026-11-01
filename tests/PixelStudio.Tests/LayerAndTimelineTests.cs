using PixelStudio.Core.Imaging;
using PixelStudio.Tests.Support;

namespace PixelStudio.Tests;

public class LayerAndTimelineTests
{
    [Fact]
    public void DuplicateLayer_CopiesPropertiesAndPixelsIndependently()
    {
        var doc = SpriteDocument.Create(4, 4);
        doc.Timeline.AddFrame();
        var original = doc.Layers[0];
        doc.Frames[0].GetCel(original.Id).SetPixel(1, 1, TestDocs.Red);
        doc.Frames[1].GetCel(original.Id).SetPixel(2, 2, TestDocs.Green);
        doc.Layers.SetOpacity(original.Id, 70);
        doc.Layers.SetBlendMode(original.Id, BlendMode.Screen);

        var copy = doc.Layers.DuplicateLayer(original.Id);

        Assert.Equal(1, doc.IndexOfLayer(copy.Id));
        Assert.Equal((70, BlendMode.Screen), (copy.Opacity, copy.BlendMode));
        foreach (var frame in doc.Frames)
        {
            Assert.NotSame(frame.GetCel(original.Id).Pixels, frame.GetCel(copy.Id).Pixels);
            Assert.True(frame.GetCel(original.Id).ContentEquals(frame.GetCel(copy.Id)));
        }

        var before = TestDocs.SnapshotPixels(doc);
        doc.Frames[0].GetCel(copy.Id).SetPixel(1, 1, TestDocs.Blue);
        doc.Frames[1].GetCel(copy.Id).Clear();

        Assert.Equal(TestDocs.Red, doc.Frames[0].GetCel(original.Id).GetPixel(1, 1));
        Assert.Equal(TestDocs.Green, doc.Frames[1].GetCel(original.Id).GetPixel(2, 2));
        Assert.Equal(before[(doc.Frames[0].Id, original.Id)], doc.Frames[0].GetCel(original.Id).Pixels);
    }

    [Fact]
    public void DuplicateFrame_DeepCopiesEveryCel()
    {
        var doc = SpriteDocument.Create(4, 4);
        doc.Layers.AddLayer();
        var source = doc.Frames[0];
        source.GetCel(doc.Layers[0].Id).SetPixel(0, 0, TestDocs.Red);
        source.GetCel(doc.Layers[1].Id).SetPixel(3, 3, TestDocs.Blue);
        doc.Timeline.SetFrameDuration(source.Id, 250);

        var copy = doc.Timeline.DuplicateFrame(source.Id);

        Assert.Equal(1, doc.IndexOfFrame(copy.Id));
        Assert.Equal(250, copy.DurationMs);
        foreach (var layer in doc.Layers)
        {
            Assert.NotSame(source.GetCel(layer.Id), copy.GetCel(layer.Id));
            Assert.True(source.GetCel(layer.Id).ContentEquals(copy.GetCel(layer.Id)));
        }

        copy.GetCel(doc.Layers[0].Id).SetPixel(0, 0, TestDocs.Green);
        copy.GetCel(doc.Layers[1].Id).Clear();

        Assert.Equal(TestDocs.Red, source.GetCel(doc.Layers[0].Id).GetPixel(0, 0));
        Assert.Equal(TestDocs.Blue, source.GetCel(doc.Layers[1].Id).GetPixel(3, 3));
    }

    [Fact]
    public void AddRemoveMoveLayer_KeepsCelsInSyncAcrossFrames()
    {
        var doc = SpriteDocument.Create(2, 2);
        doc.Timeline.AddFrame();
        var a = doc.Layers[0];
        var b = doc.Layers.AddLayer("B");
        var c = doc.Layers.AddLayer("C", index: 0);

        Assert.Equal([c.Id, a.Id, b.Id], doc.Layers.Select(l => l.Id));
        Assert.All(doc.Frames, f => Assert.Equal(3, f.Cels.Count));

        doc.Layers.MoveLayer(c.Id, 2);
        Assert.Equal([a.Id, b.Id, c.Id], doc.Layers.Select(l => l.Id));

        doc.Layers.RemoveLayer(b.Id);
        Assert.Equal([a.Id, c.Id], doc.Layers.Select(l => l.Id));
        Assert.All(doc.Frames, f => Assert.False(f.Cels.ContainsKey(b.Id)));

        doc.Layers.Rename(c.Id, "  Тени  ");
        Assert.Equal("Тени", c.Name);
    }

    [Fact]
    public void CannotRemoveLastLayerOrFrame()
    {
        var doc = SpriteDocument.Create(2, 2);
        Assert.Throws<InvalidOperationException>(() => doc.Layers.RemoveLayer(doc.Layers[0].Id));
        Assert.Throws<InvalidOperationException>(() => doc.Timeline.RemoveFrame(doc.Frames[0].Id));
    }

    [Fact]
    public void MergeDown_NormalLayersPreservesCompositeExactly()
    {
        var doc = SpriteDocument.Create(3, 1);
        var background = doc.Layers[0];
        var lower = doc.Layers.AddLayer("lower");
        var upper = doc.Layers.AddLayer("upper");
        var frame = doc.Frames[0];
        TestDocs.Fill(frame.GetCel(background.Id), new Color32(20, 40, 60));
        frame.GetCel(lower.Id).Pixels[0] = new Color32(255, 0, 0, 128);
        frame.GetCel(lower.Id).Pixels[1] = new Color32(0, 255, 0, 255);
        frame.GetCel(upper.Id).Pixels[1] = new Color32(0, 0, 255, 100);
        frame.GetCel(upper.Id).Pixels[2] = new Color32(255, 255, 255, 200);
        doc.Layers.SetOpacity(lower.Id, 60);
        doc.Layers.SetOpacity(upper.Id, 80);
        var expected = Compositor.CompositeFrame(doc, frame).Pixels;

        var result = doc.Layers.MergeDown(upper.Id);

        Assert.True(result.VisuallyIdentical, $"max diff {result.MaxChannelDifference}");
        Assert.Equal(2, doc.Layers.Count);
        Assert.Equal(lower.Id, result.MergedLayer.Id);
        Assert.Equal((100, BlendMode.Normal), (result.MergedLayer.Opacity, result.MergedLayer.BlendMode));
        var actual = Compositor.CompositeFrame(doc, frame).Pixels;
        Assert.True(LayerManager.MaxChannelDifference(expected, actual) <= 1);
    }

    [Theory]
    [InlineData(BlendMode.Multiply)]
    [InlineData(BlendMode.Screen)]
    public void MergeDown_OntoBottomLayerPreservesBlendModes(BlendMode mode)
    {
        var doc = SpriteDocument.Create(2, 1);
        var lower = doc.Layers[0];
        var upper = doc.Layers.AddLayer();
        var frame = doc.Frames[0];
        frame.GetCel(lower.Id).Pixels[0] = new Color32(200, 100, 50);
        frame.GetCel(upper.Id).Pixels[0] = new Color32(128, 128, 128);
        frame.GetCel(upper.Id).Pixels[1] = new Color32(10, 20, 30, 255); // over transparency
        doc.Layers.SetBlendMode(upper.Id, mode);
        var expected = Compositor.CompositeFrame(doc, frame).Pixels;

        var result = doc.Layers.MergeDown(upper.Id);

        Assert.True(result.VisuallyIdentical);
        Assert.Equal(expected, Compositor.CompositeFrame(doc, frame).Pixels);
    }

    [Fact]
    public void MergeDown_ReportsWhenASingleLayerCannotReproduceTheBlend()
    {
        // Multiply over a transparent part of the lower layer, with a background below:
        // originally it multiplies with the background; after merging it cannot.
        var doc = SpriteDocument.Create(1, 1);
        var background = doc.Layers[0];
        var lower = doc.Layers.AddLayer();
        var upper = doc.Layers.AddLayer();
        doc.Frames[0].GetCel(background.Id).Pixels[0] = new Color32(200, 100, 50);
        doc.Frames[0].GetCel(upper.Id).Pixels[0] = new Color32(128, 128, 128);
        doc.Layers.SetBlendMode(upper.Id, BlendMode.Multiply);

        var result = doc.Layers.MergeDown(upper.Id);

        Assert.False(result.VisuallyIdentical);
        Assert.True(doc.Undo());
        Assert.Equal(3, doc.Layers.Count);
        Assert.Equal(new Color32(100, 50, 25), Compositor.CompositeFrame(doc, doc.Frames[0]).Pixels[0]);
    }

    [Fact]
    public void MergeDown_RequiresVisibleLayerBelow()
    {
        var doc = SpriteDocument.Create(1, 1);
        var upper = doc.Layers.AddLayer();
        Assert.Throws<InvalidOperationException>(() => doc.Layers.MergeDown(doc.Layers[0].Id));
        doc.Layers.SetVisible(doc.Layers[0].Id, false);
        Assert.False(doc.Layers.CanMergeDown(upper.Id));
        Assert.Throws<InvalidOperationException>(() => doc.Layers.MergeDown(upper.Id));
    }

    [Fact]
    public void Durations_UseFpsDefaultOrOverride()
    {
        var doc = SpriteDocument.Create(1, 1);
        var second = doc.Timeline.AddFrame();
        doc.Timeline.SetFps(12);
        doc.Timeline.SetFrameDuration(second.Id, 250);

        Assert.Equal(83, doc.Timeline.DefaultFrameDurationMs);
        Assert.Equal([83, 250], doc.Timeline.GetDurations());
        Assert.Equal(333, doc.Timeline.TotalDurationMs);

        doc.Timeline.SetFrameDuration(second.Id, null);
        Assert.Equal([83, 83], doc.Timeline.GetDurations());
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.Timeline.SetFrameDuration(second.Id, 0));
    }

    [Fact]
    public void MoveAndRemoveFrame()
    {
        var doc = SpriteDocument.Create(1, 1);
        var f0 = doc.Frames[0];
        var f1 = doc.Timeline.AddFrame();
        var f2 = doc.Timeline.AddFrame();

        doc.Timeline.MoveFrame(f2.Id, 0);
        Assert.Equal([f2.Id, f0.Id, f1.Id], doc.Frames.Select(f => f.Id));

        doc.Timeline.RemoveFrame(f0.Id);
        Assert.Equal([f2.Id, f1.Id], doc.Frames.Select(f => f.Id));
    }
}
