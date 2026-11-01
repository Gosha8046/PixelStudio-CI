using PixelStudio.Core.Imaging;

namespace PixelStudio.Core;

/// <summary>
/// A pixel buffer: a flat, row-major <see cref="Color32"/> array (index = y * Width + x)
/// plus the drawing primitives that operate on it. Each (frame, layer) pair owns exactly
/// one Canvas; canvases are never shared between cels.
/// </summary>
/// <remarks>
/// Drawing methods never allocate per pixel or per call (except <see cref="FloodFill"/>,
/// which allocates one explicit work stack per call instead of recursing).
/// </remarks>
public sealed class Canvas
{
    public Canvas(int width, int height)
        : this(width, height, new Color32[checked(width * height)])
    {
    }

    /// <summary>Wraps an existing buffer (ownership is transferred to the canvas).</summary>
    public Canvas(int width, int height, Color32[] pixels)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Canvas dimensions must be positive.");
        }

        ArgumentNullException.ThrowIfNull(pixels);
        if (pixels.Length != width * height)
        {
            throw new ArgumentException($"Expected {width * height} pixels, got {pixels.Length}.", nameof(pixels));
        }

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>Row-major RGBA pixels. Exposed for zero-copy compositing and encoding.</summary>
    public Color32[] Pixels { get; }

    public bool Contains(int x, int y) => (uint)x < (uint)Width && (uint)y < (uint)Height;

    public Color32 GetPixel(int x, int y) => Pixels[y * Width + x];

    /// <summary>Sets a pixel; out-of-bounds coordinates are ignored.</summary>
    /// <returns><c>true</c> if the stored value actually changed.</returns>
    public bool SetPixel(int x, int y, Color32 color)
    {
        if (!Contains(x, y))
        {
            return false;
        }

        ref var slot = ref Pixels[y * Width + x];
        if (slot.Equals(color))
        {
            return false;
        }

        slot = color;
        return true;
    }

    /// <summary>
    /// Draws a 1-pixel, 8-connected line from (x0, y0) to (x1, y1) inclusive using
    /// Bresenham's algorithm. Used by pencil/eraser to keep strokes continuous no matter
    /// how far the pointer moved between two input events.
    /// </summary>
    /// <returns>Number of pixels whose value changed.</returns>
    public int DrawLine(int x0, int y0, int x1, int y1, Color32 color, ref DirtyRect dirty)
    {
        int changed = 0;
        int dx = Math.Abs(x1 - x0);
        int sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0);
        int sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;

        while (true)
        {
            if (SetPixel(x0, y0, color))
            {
                dirty.Include(x0, y0);
                changed++;
            }

            if (x0 == x1 && y0 == y1)
            {
                return changed;
            }

            int e2 = 2 * err;
            if (e2 >= dy)
            {
                err += dy;
                x0 += sx;
            }

            if (e2 <= dx)
            {
                err += dx;
                y0 += sy;
            }
        }
    }

    /// <summary>
    /// 4-connected scanline flood fill with an explicit stack (no recursion, so a
    /// 512x512 open area or a maze cannot overflow the call stack). Pixels are matched
    /// with <see cref="Color32.SameVisible"/>.
    /// </summary>
    /// <returns>Number of pixels written.</returns>
    public int FloodFill(int x, int y, Color32 fill, ref DirtyRect dirty)
    {
        if (!Contains(x, y))
        {
            return 0;
        }

        var pixels = Pixels;
        int width = Width;
        int height = Height;
        var target = pixels[y * width + x];
        if (Color32.SameVisible(target, fill))
        {
            return 0; // Filling a region with its own color is a no-op (also prevents endless loops).
        }

        int written = 0;
        var stack = new Stack<int>(64);
        stack.Push(y * width + x);

        while (stack.Count > 0)
        {
            int seed = stack.Pop();
            int sy = seed / width;
            int sx = seed - sy * width;
            int row = sy * width;
            if (!Color32.SameVisible(pixels[row + sx], target))
            {
                continue; // Already filled via another seed.
            }

            int left = sx;
            while (left > 0 && Color32.SameVisible(pixels[row + left - 1], target))
            {
                left--;
            }

            int right = sx;
            while (right < width - 1 && Color32.SameVisible(pixels[row + right + 1], target))
            {
                right++;
            }

            for (int i = left; i <= right; i++)
            {
                pixels[row + i] = fill;
            }

            written += right - left + 1;
            dirty.IncludeSpan(left, right, sy);

            if (sy > 0)
            {
                PushSeeds(pixels, stack, row - width, left, right, target);
            }

            if (sy < height - 1)
            {
                PushSeeds(pixels, stack, row + width, left, right, target);
            }
        }

        return written;

        static void PushSeeds(Color32[] pixels, Stack<int> stack, int rowStart, int left, int right, Color32 target)
        {
            bool inSpan = false;
            for (int i = left; i <= right; i++)
            {
                if (Color32.SameVisible(pixels[rowStart + i], target))
                {
                    if (!inSpan)
                    {
                        stack.Push(rowStart + i);
                        inSpan = true;
                    }
                }
                else
                {
                    inSpan = false;
                }
            }
        }
    }

    public void Clear() => Array.Clear(Pixels);

    public bool IsFullyTransparent()
    {
        foreach (var p in Pixels)
        {
            if (p.A != 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Deep copy with its own pixel array.</summary>
    public Canvas Clone() => new(Width, Height, (Color32[])Pixels.Clone());

    /// <summary>Copies the pixels of <paramref name="rect"/> into a new array (row-major).</summary>
    public Color32[] ReadRect(PixelRect rect)
    {
        var result = new Color32[rect.Area];
        CopyRect(Pixels, Width, rect, result);
        return result;
    }

    /// <summary>Copies a rectangle out of an arbitrary buffer with the same width as this canvas.</summary>
    public static void CopyRect(ReadOnlySpan<Color32> source, int sourceWidth, PixelRect rect, Span<Color32> destination)
    {
        for (int row = 0; row < rect.Height; row++)
        {
            source.Slice((rect.Y + row) * sourceWidth + rect.X, rect.Width)
                .CopyTo(destination.Slice(row * rect.Width, rect.Width));
        }
    }

    public void WriteRect(PixelRect rect, ReadOnlySpan<Color32> data)
    {
        if (data.Length != rect.Area)
        {
            throw new ArgumentException("Data length does not match the rectangle.", nameof(data));
        }

        for (int row = 0; row < rect.Height; row++)
        {
            data.Slice(row * rect.Width, rect.Width)
                .CopyTo(Pixels.AsSpan((rect.Y + row) * Width + rect.X, rect.Width));
        }
    }

    public bool ContentEquals(Canvas other) =>
        Width == other.Width && Height == other.Height && Pixels.AsSpan().SequenceEqual(other.Pixels);
}
