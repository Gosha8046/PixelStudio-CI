using System.Text.Json;
using PixelStudio.Core.Imaging;
using PixelStudio.Core.IO;
using PixelStudio.Core.Rendering;
using PixelStudio.Tests.Support;

namespace PixelStudio.Tests;

public class ExporterTests
{
    private static SpriteDocument BuildAnimation(int frames)
    {
        var doc = SpriteDocument.Create(5, 3);
        var top = doc.Layers.AddLayer();
        doc.Layers.SetBlendMode(top.Id, BlendMode.Multiply);
        for (int i = 1; i < frames; i++)
        {
            doc.Timeline.AddFrame();
        }

        doc.Timeline.SetFps(10);
        doc.Timeline.SetFrameDuration(doc.Frames[frames - 1].Id, 400);
        for (int i = 0; i < frames; i++)
        {
            var bottom = doc.Frames[i].GetCel(doc.Layers[0].Id);
            bottom.SetPixel(i % 5, 1, new Color32((byte)(40 * i), 200, 100, 255));
            bottom.SetPixel(0, 0, new Color32(255, 255, 255, 128));
            doc.Frames[i].GetCel(top.Id).SetPixel(i % 5, 1, new Color32(128, 128, 128, 255));
        }

        return doc;
    }

    [Fact]
    public void FramePng_MatchesCompositeIncludingTransparency()
    {
        var doc = BuildAnimation(2);
        var image = PngTestDecoder.Decode(Exporter.ExportFramePng(doc, 1));

        Assert.Equal((5, 3), (image.Width, image.Height));
        Assert.Equal(Compositor.CompositeFrame(doc, doc.Frames[1]).Pixels, image.Pixels);
        Assert.Equal(new Color32(255, 255, 255, 128), image[0, 0]);
        Assert.Equal(Color32.Transparent, image[4, 2]);
    }

    [Theory]
    [InlineData(7, 3, 2)]
    [InlineData(7, 7, 0)]
    [InlineData(7, 1, 5)]
    [InlineData(4, 10, 1)] // columns clamp to frame count
    public void SpriteSheet_PngAndJsonAgreeOnCoordinatesSizesAndDurations(int frames, int columns, int spacing)
    {
        var doc = BuildAnimation(frames);
        var export = Exporter.ExportSpriteSheet(doc, new SpriteSheetOptions(columns, spacing, "hero walk"));
        var image = PngTestDecoder.Decode(export.Png);
        using var json = JsonDocument.Parse(export.Json);
        var root = json.RootElement;

        int expectedColumns = Math.Min(columns, frames);
        int expectedRows = (frames + expectedColumns - 1) / expectedColumns;
        Assert.Equal("pixelstudio-spritesheet", root.GetProperty("format").GetString());
        Assert.Equal("hero walk.png", root.GetProperty("image").GetString());
        Assert.Equal(image.Width, root.GetProperty("atlas").GetProperty("width").GetInt32());
        Assert.Equal(image.Height, root.GetProperty("atlas").GetProperty("height").GetInt32());
        Assert.Equal(expectedColumns * 5 + (expectedColumns - 1) * spacing, image.Width);
        Assert.Equal(expectedRows * 3 + (expectedRows - 1) * spacing, image.Height);
        Assert.Equal(expectedColumns, root.GetProperty("layout").GetProperty("columns").GetInt32());

        var entries = root.GetProperty("frames").EnumerateArray().ToList();
        Assert.Equal(frames, entries.Count);
        var covered = new bool[image.Width * image.Height];
        for (int i = 0; i < frames; i++)
        {
            var e = entries[i];
            int x = e.GetProperty("x").GetInt32(), y = e.GetProperty("y").GetInt32();
            int w = e.GetProperty("width").GetInt32(), h = e.GetProperty("height").GetInt32();
            Assert.Equal(i, e.GetProperty("index").GetInt32());
            Assert.Equal((5, 3), (w, h));
            Assert.Equal(doc.Timeline.GetDurationMs(doc.Frames[i]), e.GetProperty("durationMs").GetInt32());

            // The rectangle named in JSON contains exactly the composite of frame i.
            var expected = Compositor.CompositeFrame(doc, doc.Frames[i]);
            for (int py = 0; py < h; py++)
            {
                for (int px = 0; px < w; px++)
                {
                    Assert.Equal(expected.GetPixel(px, py), image[x + px, y + py]);
                    covered[(y + py) * image.Width + x + px] = true;
                }
            }
        }

        // Spacing and unused cells stay fully transparent.
        for (int p = 0; p < covered.Length; p++)
        {
            if (!covered[p])
            {
                Assert.Equal(Color32.Transparent, image.Pixels[p]);
            }
        }

        var playback = root.GetProperty("playback");
        Assert.Equal(Enumerable.Range(0, frames), playback.GetProperty("order").EnumerateArray().Select(v => v.GetInt32()));
        Assert.Equal(doc.Timeline.TotalDurationMs, playback.GetProperty("totalDurationMs").GetInt32());
        Assert.True(playback.GetProperty("loop").GetBoolean());
        Assert.Equal(400, entries[^1].GetProperty("durationMs").GetInt32());
        Assert.Equal(100, entries[0].GetProperty("durationMs").GetInt32());
    }

    [Fact]
    public void Export_IsUnaffectedByViewOverlays()
    {
        var doc = BuildAnimation(3);
        var before = Exporter.ExportSpriteSheet(doc, new SpriteSheetOptions(3, 1, "a.png"));

        // Onion skin is rendered by the view renderer into its own buffers only.
        var renderer = new ViewRenderer();
        renderer.Render(doc, 1, new OnionSkinSettings { Enabled = true, Opacity = 100 });
        Assert.False(renderer.ViewBytes.AsSpan().SequenceEqual(renderer.CompositeBytes), "onion skin should change the view");

        var after = Exporter.ExportSpriteSheet(doc, new SpriteSheetOptions(3, 1, "a.png"));
        Assert.Equal(before.Png, after.Png);
        Assert.Equal(before.Json, after.Json);
    }

    [Fact]
    public void ViewRenderer_WithoutOnionSkinEqualsComposite()
    {
        var doc = BuildAnimation(3);
        var renderer = new ViewRenderer();
        renderer.Render(doc, 2, new OnionSkinSettings { Enabled = false });
        Assert.True(renderer.ViewBytes.AsSpan().SequenceEqual(renderer.CompositeBytes));
    }

    [Theory]
    [InlineData("hero", "hero.png")]
    [InlineData("hero.PNG", "hero.PNG")]
    [InlineData("../../etc/passwd", "passwd.png")]
    [InlineData("C:\\temp\\run.png", "run.png")]
    [InlineData("", "sprite.png")]
    public void ImageFileName_IsSanitized(string input, string expected) =>
        Assert.Equal(expected, Exporter.NormalizeImageFileName(input));

    [Fact]
    public void SpriteSheet_RejectsOversizedAtlasAndBadSpacing()
    {
        Assert.Throws<InvalidOperationException>(() => SpriteSheetLayout.Create(256, 512, 512, 256, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SpriteSheetLayout.Create(2, 8, 8, 2, -1));
    }

    [Fact]
    public void PngEncoder_ProducesValidFileForAllFilterPaths()
    {
        // A gradient + noise image makes the adaptive filter pick different filters per row.
        var random = new Random(1);
        var pixels = new Color32[37 * 23];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = i % 3 == 0
                ? new Color32((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256))
                : new Color32((byte)(i % 37 * 7), (byte)(i / 37 * 11), 99, 255);
        }

        var decoded = PngTestDecoder.Decode(PngEncoder.Encode(37, 23, pixels));
        Assert.Equal(pixels, decoded.Pixels);
    }
}

public class IncrementalRenderTests
{
    [Fact]
    public void RenderRegion_MatchesFullRenderAfterEdits()
    {
        var doc = SpriteDocument.Create(24, 20);
        var top = doc.Layers.AddLayer();
        doc.Layers.SetBlendMode(top.Id, BlendMode.Screen);
        doc.Layers.SetOpacity(top.Id, 70);
        doc.Timeline.AddFrame();
        doc.Timeline.AddFrame();
        TestDocs.Fill(doc.Frames[0].GetCel(doc.Layers[0].Id), new Color32(200, 30, 30));
        TestDocs.Fill(doc.Frames[2].GetCel(top.Id), new Color32(10, 200, 90, 180));
        var onion = new OnionSkinSettings { Enabled = true, Opacity = 60 };

        var incremental = new ViewRenderer();
        incremental.Render(doc, 1, onion);

        var tools = new PixelStudio.Core.Editing.ToolController { PrimaryColor = new Color32(30, 60, 250, 200) };
        tools.PointerDown(doc, 1, top.Id, 2, 3);
        tools.PointerMove(20, 15);
        var rect = tools.TakeChangedRect();
        Assert.Equal(new PixelRect(2, 3, 19, 13), rect);
        incremental.RenderRegion(doc, 1, rect);
        tools.PointerMove(4, 18);
        incremental.RenderRegion(doc, 1, tools.TakeChangedRect());
        tools.PointerUp();
        Assert.True(tools.TakeChangedRect().IsEmpty, "nothing changed since the last take");

        var full = new ViewRenderer();
        full.Render(doc, 1, onion);
        Assert.Equal(full.ViewBytes, incremental.ViewBytes);
        Assert.Equal(full.CompositeBytes, incremental.CompositeBytes);
    }

    [Fact]
    public void RenderRegion_RequiresAFullRenderOfTheSameFrame()
    {
        var doc = SpriteDocument.Create(4, 4);
        doc.Timeline.AddFrame();
        var renderer = new ViewRenderer();
        Assert.False(renderer.CanRenderRegion(doc, 0));
        renderer.Render(doc, 0, new OnionSkinSettings());
        Assert.True(renderer.CanRenderRegion(doc, 0));
        Assert.False(renderer.CanRenderRegion(doc, 1));
        Assert.Throws<InvalidOperationException>(() => renderer.RenderRegion(doc, 1, new PixelRect(0, 0, 1, 1)));
    }
}
