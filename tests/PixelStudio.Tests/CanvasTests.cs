using PixelStudio.Core.Imaging;
using PixelStudio.Tests.Support;

namespace PixelStudio.Tests;

public class CanvasTests
{
    [Theory]
    [InlineData(0, 0, 15, 3)]
    [InlineData(15, 3, 0, 0)]
    [InlineData(2, 14, 5, 0)]
    [InlineData(0, 0, 0, 15)]
    [InlineData(10, 2, 1, 13)]
    [InlineData(4, 4, 4, 4)]
    public void DrawLine_IsContinuousAndIncludesEndpoints(int x0, int y0, int x1, int y1)
    {
        var canvas = new Canvas(16, 16);
        var dirty = new DirtyRect();

        canvas.DrawLine(x0, y0, x1, y1, TestDocs.Red, ref dirty);

        Assert.Equal(TestDocs.Red, canvas.GetPixel(x0, y0));
        Assert.Equal(TestDocs.Red, canvas.GetPixel(x1, y1));

        // Walk from the start: every painted pixel must be 8-connected to the path, and the
        // number of pixels equals the Chebyshev distance + 1 (no gaps, no doubled steps).
        int expected = Math.Max(Math.Abs(x1 - x0), Math.Abs(y1 - y0)) + 1;
        var painted = Enumerable.Range(0, 256).Where(i => canvas.Pixels[i] == TestDocs.Red)
            .Select(i => (X: i % 16, Y: i / 16)).ToList();
        Assert.Equal(expected, painted.Count);
        var visited = new HashSet<(int, int)> { (x0, y0) };
        var queue = new Queue<(int X, int Y)>(visited);
        while (queue.Count > 0)
        {
            var (cx, cy) = queue.Dequeue();
            foreach (var p in painted.Where(p => Math.Abs(p.X - cx) <= 1 && Math.Abs(p.Y - cy) <= 1))
            {
                if (visited.Add(p))
                {
                    queue.Enqueue(p);
                }
            }
        }

        Assert.Equal(painted.Count, visited.Count);
        var rect = dirty.ToRect();
        Assert.Equal(new PixelRect(Math.Min(x0, x1), Math.Min(y0, y1), Math.Abs(x1 - x0) + 1, Math.Abs(y1 - y0) + 1), rect);
    }

    [Fact]
    public void DrawLine_ClipsOutsideCanvasWithoutThrowing()
    {
        var canvas = new Canvas(8, 8);
        var dirty = new DirtyRect();

        int changed = canvas.DrawLine(-20, 4, 30, 4, TestDocs.Red, ref dirty);

        Assert.Equal(8, changed);
        Assert.Equal(new PixelRect(0, 4, 8, 1), dirty.ToRect());
    }

    [Fact]
    public void FloodFill_StopsAtBordersAndTreatsAllTransparentAsEqual()
    {
        var canvas = new Canvas(5, 5);
        // Vertical wall at x = 2; transparent pixels with different invisible RGB on the left.
        for (int y = 0; y < 5; y++)
        {
            canvas.Pixels[y * 5 + 2] = TestDocs.Blue;
        }

        canvas.Pixels[0] = new Color32(9, 9, 9, 0);
        var dirty = new DirtyRect();

        int written = canvas.FloodFill(0, 2, TestDocs.Red, ref dirty);

        Assert.Equal(10, written);
        for (int y = 0; y < 5; y++)
        {
            Assert.Equal(TestDocs.Red, canvas.GetPixel(0, y));
            Assert.Equal(TestDocs.Red, canvas.GetPixel(1, y));
            Assert.Equal(TestDocs.Blue, canvas.GetPixel(2, y));
            Assert.Equal(Color32.Transparent, canvas.GetPixel(3, y));
        }

        Assert.Equal(new PixelRect(0, 0, 2, 5), dirty.ToRect());
    }

    [Fact]
    public void FloodFill_IsFourConnected()
    {
        var canvas = new Canvas(3, 3);
        TestDocs.Fill(canvas, TestDocs.Blue);
        canvas.Pixels[0] = Color32.Transparent; // (0,0)
        canvas.Pixels[4] = Color32.Transparent; // (1,1) touches (0,0) only diagonally
        var dirty = new DirtyRect();

        Assert.Equal(1, canvas.FloodFill(0, 0, TestDocs.Red, ref dirty));
        Assert.Equal(Color32.Transparent, canvas.GetPixel(1, 1));
    }

    [Fact]
    public void FloodFill_WithSameColorIsNoOp()
    {
        var canvas = new Canvas(4, 4);
        TestDocs.Fill(canvas, TestDocs.Red);
        var dirty = new DirtyRect();

        Assert.Equal(0, canvas.FloodFill(1, 1, TestDocs.Red, ref dirty));
        Assert.True(dirty.IsEmpty);
    }

    [Fact]
    public void FloodFill_LargeOpenAreaAndSerpentineMazeDoNotOverflowTheStack()
    {
        const int size = DocumentLimits.MaxSize;
        var open = new Canvas(size, size);
        var dirty = new DirtyRect();
        Assert.Equal(size * size, open.FloodFill(size / 2, size / 2, TestDocs.Red, ref dirty));

        // 1-px serpentine corridor: the worst case for recursive fills (~131k-pixel path).
        var maze = new Canvas(size, size);
        for (int y = 0; y < size; y += 2)
        {
            for (int x = 0; x < size; x++)
            {
                maze.Pixels[y * size + x] = TestDocs.Blue;
            }

            if (y + 1 < size) // a gap connects the corridors above and below this wall
            {
                maze.Pixels[y * size + ((y / 2) % 2 == 0 ? size - 1 : 0)] = Color32.Transparent;
            }
        }

        int corridorPixels = maze.Pixels.Count(p => p.A == 0);
        Assert.Equal(corridorPixels, maze.FloodFill(0, 1, TestDocs.Red, ref dirty));
        Assert.DoesNotContain(maze.Pixels, p => p.A == 0);
    }

    [Fact]
    public void Clone_HasIndependentPixelArray()
    {
        var canvas = new Canvas(2, 2);
        var copy = canvas.Clone();

        copy.SetPixel(0, 0, TestDocs.Red);

        Assert.NotSame(canvas.Pixels, copy.Pixels);
        Assert.Equal(Color32.Transparent, canvas.GetPixel(0, 0));
    }

    [Fact]
    public void ReadRectAndWriteRect_RoundTrip()
    {
        var canvas = new Canvas(4, 3);
        for (int i = 0; i < canvas.Pixels.Length; i++)
        {
            canvas.Pixels[i] = new Color32((byte)i, 0, 0);
        }

        var rect = new PixelRect(1, 1, 2, 2);
        var data = canvas.ReadRect(rect);
        Assert.Equal([new Color32(5, 0, 0), new Color32(6, 0, 0), new Color32(9, 0, 0), new Color32(10, 0, 0)], data);

        var target = new Canvas(4, 3);
        target.WriteRect(rect, data);
        Assert.Equal(new Color32(10, 0, 0), target.GetPixel(2, 2));
        Assert.Equal(Color32.Transparent, target.GetPixel(0, 0));
    }

    [Theory]
    [InlineData("#FF8000", 255, 128, 0, 255)]
    [InlineData("ff800080", 255, 128, 0, 128)]
    [InlineData("#abc", 0xAA, 0xBB, 0xCC, 255)]
    public void Color_ParsesHex(string text, byte r, byte g, byte b, byte a)
    {
        Assert.True(Color32.TryParseHex(text, out var color));
        Assert.Equal(new Color32(r, g, b, a), color);
    }

    [Theory]
    [InlineData("")]
    [InlineData("#12345")]
    [InlineData("#GG0000")]
    public void Color_RejectsInvalidHex(string text) => Assert.False(Color32.TryParseHex(text, out _));
}
