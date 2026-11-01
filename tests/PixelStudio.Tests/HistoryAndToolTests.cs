using PixelStudio.Core.Editing;
using PixelStudio.Core.Imaging;
using PixelStudio.Tests.Support;

namespace PixelStudio.Tests;

public class HistoryAndToolTests
{
    [Fact]
    public void FastPointerGesture_DrawsContinuousLineAndIsOneUndoStep()
    {
        var doc = SpriteDocument.Create(32, 32);
        var tools = new ToolController { Tool = ToolKind.Pencil, PrimaryColor = TestDocs.Red };
        int layer = doc.Layers[0].Id;

        // Simulates a fast drag where the browser delivered only three far-apart samples.
        tools.PointerDown(doc, 0, layer, 1, 1);
        tools.PointerMove(25, 9);
        tools.PointerMove(4, 30);
        var result = tools.PointerUp();

        Assert.True(result.HasFlag(ToolResult.GestureCommitted));
        Assert.Equal(1, doc.History.UndoCount);
        var cel = doc.Frames[0].GetCel(layer);
        int painted = cel.Pixels.Count(p => p == TestDocs.Red);
        Assert.Equal((Math.Max(24, 8) + 1) + (Math.Max(21, 21) + 1) - 1, painted); // two segments sharing a vertex

        Assert.True(doc.Undo());
        Assert.True(cel.IsFullyTransparent());
        Assert.True(doc.Redo());
        Assert.Equal(painted, cel.Pixels.Count(p => p == TestDocs.Red));
    }

    [Fact]
    public void PixelEdit_StoresOnlyTheBoundingRectangle()
    {
        var doc = SpriteDocument.Create(64, 64);
        var tools = new ToolController { PrimaryColor = TestDocs.Red };
        tools.PointerDown(doc, 0, doc.Layers[0].Id, 10, 10);
        tools.PointerMove(12, 11);
        tools.PointerUp();

        Assert.Equal(1, doc.History.UndoCount);
        Assert.Equal("Карандаш", doc.History.UndoLabel);
    }

    [Fact]
    public void Eraser_FillAndEyedropper()
    {
        var doc = SpriteDocument.Create(8, 8);
        int layer = doc.Layers[0].Id;
        var tools = new ToolController { Tool = ToolKind.Fill, PrimaryColor = TestDocs.Blue };

        tools.PointerDown(doc, 0, layer, 3, 3);
        var cel = doc.Frames[0].GetCel(layer);
        Assert.All(cel.Pixels, p => Assert.Equal(TestDocs.Blue, p));
        Assert.Equal("Заливка", doc.History.UndoLabel);

        tools.Tool = ToolKind.Eraser;
        tools.PointerDown(doc, 0, layer, 0, 0);
        tools.PointerMove(7, 0);
        tools.PointerUp();
        Assert.All(Enumerable.Range(0, 8), x => Assert.Equal(Color32.Transparent, cel.GetPixel(x, 0)));
        Assert.Equal(2, doc.History.UndoCount);

        tools.Tool = ToolKind.Eyedropper;
        var picked = tools.PointerDown(doc, 0, layer, 4, 4);
        Assert.True(picked.HasFlag(ToolResult.ColorPicked));
        Assert.Equal(TestDocs.Blue, tools.PrimaryColor);
        Assert.Equal(2, doc.History.UndoCount); // picking is not an edit
    }

    [Fact]
    public void DrawingOnHiddenLayerIsRefused()
    {
        var doc = SpriteDocument.Create(4, 4);
        doc.Layers.SetVisible(doc.Layers[0].Id, false);
        var tools = new ToolController { PrimaryColor = TestDocs.Red };

        Assert.Equal(ToolResult.LayerHidden, tools.PointerDown(doc, 0, doc.Layers[0].Id, 1, 1));
        Assert.True(doc.Frames[0].GetCel(doc.Layers[0].Id).IsFullyTransparent());
    }

    [Fact]
    public void CancelGesture_RestoresPixelsAndRecordsNothing()
    {
        var doc = SpriteDocument.Create(8, 8);
        var tools = new ToolController { PrimaryColor = TestDocs.Red };
        tools.PointerDown(doc, 0, doc.Layers[0].Id, 0, 0);
        tools.PointerMove(7, 7);
        tools.CancelGesture();

        Assert.True(doc.Frames[0].GetCel(doc.Layers[0].Id).IsFullyTransparent());
        Assert.Equal(0, doc.History.UndoCount);
    }

    [Fact]
    public void PointerMove_DoesNotAllocate()
    {
        var doc = SpriteDocument.Create(256, 256);
        var tools = new ToolController { PrimaryColor = TestDocs.Red };
        tools.PointerDown(doc, 0, doc.Layers[0].Id, 0, 0);
        tools.PointerMove(1, 1); // warm-up

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            tools.PointerMove(i % 256, (i * 7) % 256);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        tools.PointerUp();
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void UndoRedo_RestoresStructuralAndPixelStateInOrder()
    {
        var doc = SpriteDocument.Create(4, 4);
        var tools = new ToolController { PrimaryColor = TestDocs.Red };
        var states = new List<string> { TestDocs.Describe(doc) };

        var layer2 = doc.Layers.AddLayer();
        states.Add(TestDocs.Describe(doc));
        tools.PointerDown(doc, 0, layer2.Id, 1, 1);
        tools.PointerUp();
        states.Add(TestDocs.Describe(doc));
        var frame2 = doc.Timeline.DuplicateFrame(doc.Frames[0].Id);
        states.Add(TestDocs.Describe(doc));
        tools.PointerDown(doc, 1, layer2.Id, 2, 2);
        tools.PointerUp();
        states.Add(TestDocs.Describe(doc));
        doc.Layers.SetBlendMode(layer2.Id, BlendMode.Screen);
        states.Add(TestDocs.Describe(doc));
        doc.Layers.MergeDown(layer2.Id);
        states.Add(TestDocs.Describe(doc));
        doc.Timeline.MoveFrame(frame2.Id, 0);
        states.Add(TestDocs.Describe(doc));
        doc.AddPaletteColor(new Color32(1, 2, 3));
        states.Add(TestDocs.Describe(doc));

        for (int i = states.Count - 2; i >= 0; i--)
        {
            Assert.True(doc.Undo());
            Assert.Equal(states[i], TestDocs.Describe(doc));
        }

        Assert.False(doc.Undo());
        for (int i = 1; i < states.Count; i++)
        {
            Assert.True(doc.Redo());
            Assert.Equal(states[i], TestDocs.Describe(doc));
        }

        Assert.False(doc.Redo());
    }

    [Fact]
    public void NewEditAfterUndo_ClearsRedo()
    {
        var doc = SpriteDocument.Create(2, 2);
        doc.Layers.AddLayer();
        doc.Undo();
        Assert.True(doc.History.CanRedo);
        doc.Timeline.AddFrame();
        Assert.False(doc.History.CanRedo);
    }

    [Fact]
    public void ConsecutiveOpacityChanges_MergeIntoOneStepUntilBroken()
    {
        var doc = SpriteDocument.Create(2, 2);
        int id = doc.Layers[0].Id;
        for (int v = 99; v >= 50; v--)
        {
            doc.Layers.SetOpacity(id, v);
        }

        Assert.Equal(1, doc.History.UndoCount);
        doc.History.BreakMerge();
        doc.Layers.SetOpacity(id, 10);
        Assert.Equal(2, doc.History.UndoCount);

        doc.Undo();
        Assert.Equal(50, doc.Layers[0].Opacity);
        doc.Undo();
        Assert.Equal(100, doc.Layers[0].Opacity);
    }

    [Fact]
    public void Dirty_TracksSavePointAcrossUndoRedo()
    {
        var doc = SpriteDocument.Create(2, 2);
        Assert.False(doc.History.IsDirty);

        doc.Layers.AddLayer();
        Assert.True(doc.History.IsDirty);
        doc.History.MarkSaved();
        Assert.False(doc.History.IsDirty);

        doc.Timeline.AddFrame();
        Assert.True(doc.History.IsDirty);
        doc.Undo();
        Assert.False(doc.History.IsDirty);
        doc.Undo();
        Assert.True(doc.History.IsDirty);
        doc.Redo();
        Assert.False(doc.History.IsDirty);

        // Merging into the saved entry is forbidden, otherwise "clean" would lie.
        doc.Layers.SetOpacity(doc.Layers[1].Id, 40);
        doc.History.MarkSaved();
        doc.Layers.SetOpacity(doc.Layers[1].Id, 30);
        Assert.True(doc.History.IsDirty);
    }

    [Fact]
    public void HistoryLimit_DropsOldestAndKeepsDirtyHonest()
    {
        var history = new UndoHistory(limit: 3);
        var doc = SpriteDocument.Create(1, 1);
        for (int i = 0; i < 5; i++)
        {
            history.Push(new PixelEditCommand("x", doc.Frames[0].Id, doc.Layers[0].Id, new PixelRect(0, 0, 1, 1), [default], [default]));
        }

        Assert.Equal(3, history.UndoCount);
        while (history.Undo(doc))
        {
        }

        Assert.True(history.IsDirty, "Base state was trimmed away, so it can no longer equal the saved state.");
    }
}
