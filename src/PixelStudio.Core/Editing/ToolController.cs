using PixelStudio.Core.History;
using PixelStudio.Core.Imaging;

namespace PixelStudio.Core.Editing;

public enum ToolKind
{
    Pencil,
    Eraser,
    Fill,
    Eyedropper,
    Hand,
}

/// <summary>What a pointer event did, so the host knows what to refresh.</summary>
[Flags]
public enum ToolResult
{
    None = 0,
    PixelsChanged = 1,
    ColorPicked = 2,
    GestureCommitted = 4,
    LayerHidden = 8,
}

/// <summary>
/// Turns pointer events (already converted to pixel coordinates) into edits.
/// One gesture — pointer down, any number of moves, pointer up — becomes exactly one
/// undo entry. Pencil and eraser connect consecutive points with Bresenham lines, so fast
/// pointer movement never leaves gaps. The per-move path performs no heap allocations.
/// </summary>
public sealed class ToolController
{
    private SpriteDocument? _doc;
    private Canvas? _canvas;
    private int _frameId;
    private int _layerId;
    private int _lastX;
    private int _lastY;
    private DirtyRect _dirty;
    private DirtyRect _changedSinceLastTake;
    private Color32[] _before = [];
    private Color32 _strokeColor;

    public ToolKind Tool { get; set; } = ToolKind.Pencil;

    /// <summary>Straight-alpha color written by pencil and fill (replace mode, no blending).</summary>
    public Color32 PrimaryColor { get; set; } = Color32.Black;

    /// <summary>Eyedropper samples the visible composite (true) or only the active layer (false).</summary>
    public bool SampleMergedLayers { get; set; } = true;

    public bool IsGestureActive => _canvas is not null;

    /// <summary>
    /// Bounding box of pixels changed since the previous call (across pointer events and
    /// gestures), so the view can redraw just that region. Resets on read.
    /// </summary>
    public PixelRect TakeChangedRect()
    {
        var rect = _changedSinceLastTake.ToRect();
        _changedSinceLastTake.Reset();
        return rect;
    }

    public ToolResult PointerDown(SpriteDocument document, int frameIndex, int layerId, int x, int y)
    {
        CancelGesture();
        var frame = document.Frames[frameIndex];

        switch (Tool)
        {
            case ToolKind.Eyedropper:
                return Pick(document, frame, layerId, x, y);
            case ToolKind.Hand:
                return ToolResult.None;
        }

        var layer = document.FindLayer(layerId);
        if (!layer.Visible)
        {
            return ToolResult.LayerHidden;
        }

        BeginGesture(document, frame.Id, layerId, frame.GetCel(layerId));

        if (Tool == ToolKind.Fill)
        {
            var fillDirty = new DirtyRect();
            _canvas!.FloodFill(x, y, PrimaryColor, ref fillDirty);
            MergeInto(ref _dirty, fillDirty);
            MergeInto(ref _changedSinceLastTake, fillDirty);
            return Commit("Заливка");
        }

        _strokeColor = Tool == ToolKind.Eraser ? Color32.Transparent : PrimaryColor;
        _lastX = x;
        _lastY = y;
        return DrawSegment(x, y, x, y);
    }

    public ToolResult PointerMove(int x, int y)
    {
        if (_canvas is null || (x == _lastX && y == _lastY))
        {
            return ToolResult.None;
        }

        var result = DrawSegment(_lastX, _lastY, x, y);
        _lastX = x;
        _lastY = y;
        return result;
    }

    public ToolResult PointerUp()
    {
        if (_canvas is null)
        {
            return ToolResult.None;
        }

        return Commit(Tool == ToolKind.Eraser ? "Ластик" : "Карандаш");
    }

    /// <summary>Aborts an unfinished gesture and restores the pixels it touched.</summary>
    public void CancelGesture()
    {
        if (_canvas is null)
        {
            return;
        }

        if (!_dirty.IsEmpty)
        {
            var rect = _dirty.ToRect();
            var data = new Color32[rect.Area];
            Canvas.CopyRect(_before, _canvas.Width, rect, data);
            _canvas.WriteRect(rect, data);
            MergeInto(ref _changedSinceLastTake, _dirty);
        }

        EndGesture();
    }

    private ToolResult DrawSegment(int x0, int y0, int x1, int y1)
    {
        var segment = new DirtyRect();
        int changed = _canvas!.DrawLine(x0, y0, x1, y1, _strokeColor, ref segment);
        if (changed == 0)
        {
            return ToolResult.None;
        }

        MergeInto(ref _dirty, segment);
        MergeInto(ref _changedSinceLastTake, segment);
        return ToolResult.PixelsChanged;
    }

    private static void MergeInto(ref DirtyRect target, DirtyRect source)
    {
        if (source.IsEmpty)
        {
            return;
        }

        var r = source.ToRect();
        target.Include(r.X, r.Y);
        target.Include(r.Right - 1, r.Bottom - 1);
    }

    private void BeginGesture(SpriteDocument document, int frameId, int layerId, Canvas canvas)
    {
        _doc = document;
        _frameId = frameId;
        _layerId = layerId;
        _canvas = canvas;
        _dirty.Reset();

        // Snapshot buffer is reused between gestures; it is only reallocated when the size changes.
        if (_before.Length != canvas.Pixels.Length)
        {
            _before = new Color32[canvas.Pixels.Length];
        }

        canvas.Pixels.AsSpan().CopyTo(_before);
    }

    private ToolResult Commit(string label)
    {
        var canvas = _canvas!;
        var doc = _doc!;
        var result = ToolResult.None;
        if (!_dirty.IsEmpty)
        {
            var rect = _dirty.ToRect();
            var before = new Color32[rect.Area];
            Canvas.CopyRect(_before, canvas.Width, rect, before);
            var after = canvas.ReadRect(rect);
            if (!before.AsSpan().SequenceEqual(after))
            {
                doc.PushPixelEdit(new PixelEditCommand(label, _frameId, _layerId, rect, before, after));
                result = ToolResult.PixelsChanged | ToolResult.GestureCommitted;
            }
        }

        EndGesture();
        return result;
    }

    private void EndGesture()
    {
        _canvas = null;
        _doc = null;
        _dirty.Reset();
    }

    private ToolResult Pick(SpriteDocument document, Animation.Frame frame, int layerId, int x, int y)
    {
        if (!(x >= 0 && y >= 0 && x < document.Width && y < document.Height))
        {
            return ToolResult.None;
        }

        Color32 color;
        if (SampleMergedLayers)
        {
            color = Compositor.CompositeFrame(document, frame).GetPixel(x, y);
        }
        else
        {
            color = frame.GetCel(layerId).GetPixel(x, y);
        }

        PrimaryColor = color.A == 0 ? Color32.Transparent : color;
        return ToolResult.ColorPicked;
    }
}
