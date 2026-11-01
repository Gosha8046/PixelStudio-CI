namespace PixelStudio.Core.Imaging;

/// <summary>Integer rectangle in pixel coordinates (X/Y = top-left, exclusive right/bottom).</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public int Area => Width * Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>
/// Mutable accumulator for the bounding box of modified pixels. A value type so that
/// drawing code can update it through <c>ref</c> without allocating.
/// </summary>
public struct DirtyRect
{
    private int _minX;
    private int _minY;
    private int _maxX;
    private int _maxY;
    private bool _any;

    public readonly bool IsEmpty => !_any;

    public void Include(int x, int y)
    {
        if (!_any)
        {
            _minX = _maxX = x;
            _minY = _maxY = y;
            _any = true;
            return;
        }

        if (x < _minX) _minX = x;
        if (x > _maxX) _maxX = x;
        if (y < _minY) _minY = y;
        if (y > _maxY) _maxY = y;
    }

    public void IncludeSpan(int x0, int x1, int y)
    {
        Include(x0, y);
        Include(x1, y);
    }

    public void Reset() => _any = false;

    public readonly PixelRect ToRect() =>
        _any ? new PixelRect(_minX, _minY, _maxX - _minX + 1, _maxY - _minY + 1) : default;
}
