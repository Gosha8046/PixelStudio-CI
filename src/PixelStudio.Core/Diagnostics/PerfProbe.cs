using System.Diagnostics;
using PixelStudio.Core.Animation;
using PixelStudio.Core.Imaging;
using PixelStudio.Core.IO;
using PixelStudio.Core.Rendering;

namespace PixelStudio.Core.Diagnostics;

public sealed record PerfResult(string Name, int Iterations, double MeanMs, double MinMs, long AllocatedBytesPerOp);

/// <summary>
/// Small, dependency-free measurement suite for the hot paths. The same code runs natively
/// (tools/PixelStudio.Bench) and inside the browser on .NET WebAssembly (?bench=1), so
/// performance claims in the README are backed by numbers from both runtimes.
/// </summary>
public static class PerfProbe
{
    public static IReadOnlyList<PerfResult> RunAll(int scale = 1)
    {
        var results = new List<PerfResult>();
        foreach (var size in new[] { 64, 256 })
        {
            var doc = BuildDocument(size, layers: 4, frames: 4);
            var frame = doc.Frames[0];
            var buffer = new Color32[size * size];
            results.Add(Measure($"Composite {size}x{size}, 4 layers", 20 * scale, () => Compositor.CompositeFrame(doc, frame, buffer)));

            var renderer = new ViewRenderer();
            var onion = new OnionSkinSettings { Enabled = true };
            results.Add(Measure($"View render {size}x{size} + onion skin", 10 * scale, () => renderer.Render(doc, 1, onion)));

            var canvas = doc.Frames[0].GetCel(doc.Layers[0].Id);
            var dirty = new DirtyRect();
            int toggle = 0;
            results.Add(Measure($"Pencil line diagonal {size}px", 200 * scale, () =>
            {
                var color = (toggle++ & 1) == 0 ? new Color32(255, 0, 0) : new Color32(0, 0, 255);
                canvas.DrawLine(0, 0, size - 1, size - 1, color, ref dirty);
            }));
        }

        var fillCanvas = new Canvas(DocumentLimits.MaxSize, DocumentLimits.MaxSize);
        var fillDirty = new DirtyRect();
        int fillToggle = 0;
        results.Add(Measure("Flood fill 512x512 open area", 5 * scale, () =>
        {
            var color = (fillToggle++ & 1) == 0 ? new Color32(10, 20, 30) : new Color32(40, 50, 60);
            fillCanvas.FloodFill(0, 0, color, ref fillDirty);
        }));

        var maze = BuildMaze(DocumentLimits.MaxSize);
        int mazeToggle = 0;
        results.Add(Measure("Flood fill 512x512 maze (worst case)", 3 * scale, () =>
        {
            var color = (mazeToggle++ & 1) == 0 ? new Color32(200, 0, 0) : new Color32(0, 200, 0);
            maze.FloodFill(1, 1, color, ref fillDirty);
        }));

        var exportDoc = BuildDocument(64, layers: 4, frames: 16);
        results.Add(Measure("PNG encode 64x64 frame", 20 * scale, () => Exporter.ExportFramePng(exportDoc, 0)));
        results.Add(Measure("Sprite sheet 16 frames 64x64 (PNG+JSON)", 5 * scale,
            () => Exporter.ExportSpriteSheet(exportDoc, new SpriteSheetOptions(4, 2, "bench.png"))));
        byte[] saved = ProjectSerializer.Save(exportDoc);
        results.Add(Measure("Project save 64x64, 4 layers x 16 frames", 5 * scale, () => ProjectSerializer.Save(exportDoc)));
        results.Add(Measure("Project load 64x64, 4 layers x 16 frames", 5 * scale, () => ProjectSerializer.Load(saved)));
        return results;
    }

    public static string FormatTable(IEnumerable<PerfResult> results)
    {
        var lines = new List<string> { "| Operation | Iterations | Mean, ms | Min, ms | Alloc/op, bytes |", "|---|---:|---:|---:|---:|" };
        lines.AddRange(results.Select(r =>
            FormattableString.Invariant($"| {r.Name} | {r.Iterations} | {r.MeanMs:F3} | {r.MinMs:F3} | {r.AllocatedBytesPerOp} |")));
        return string.Join(Environment.NewLine, lines);
    }

    private static PerfResult Measure(string name, int iterations, Action action)
    {
        action(); // Warm-up (JIT / interpreter tiering, buffer allocation).
        action();

        double total = 0;
        double min = double.MaxValue;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++)
        {
            long start = Stopwatch.GetTimestamp();
            action();
            double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            total += ms;
            min = Math.Min(min, ms);
        }

        long allocated = (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / iterations;
        return new PerfResult(name, iterations, total / iterations, min, allocated);
    }

    private static SpriteDocument BuildDocument(int size, int layers, int frames)
    {
        var doc = SpriteDocument.Create(size, size);
        for (int i = 1; i < layers; i++)
        {
            doc.Layers.AddLayer();
        }

        doc.Layers.SetBlendMode(doc.Layers[1].Id, BlendMode.Multiply);
        doc.Layers.SetOpacity(doc.Layers[2].Id, 60);
        if (layers > 3)
        {
            doc.Layers.SetBlendMode(doc.Layers[3].Id, BlendMode.Screen);
        }

        for (int i = 1; i < frames; i++)
        {
            doc.Timeline.AddFrame();
        }

        var random = new Random(42);
        foreach (Frame frame in doc.Frames)
        {
            foreach (var layer in doc.Layers)
            {
                var pixels = frame.GetCel(layer.Id).Pixels;
                for (int p = 0; p < pixels.Length; p++)
                {
                    // ~70% coverage with mixed alpha to exercise every blend path.
                    if (random.Next(10) < 7)
                    {
                        pixels[p] = new Color32((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(64, 256));
                    }
                }
            }
        }

        doc.History.Clear();
        return doc;
    }

    private static Canvas BuildMaze(int size)
    {
        // Serpentine corridors: one long 1-px path that maximizes scanline seeds.
        var canvas = new Canvas(size, size);
        var wall = new Color32(0, 0, 0);
        for (int y = 0; y < size; y += 2)
        {
            for (int x = 0; x < size; x++)
            {
                canvas.Pixels[y * size + x] = wall;
            }

            int gap = (y / 2) % 2 == 0 ? size - 2 : 1;
            if (y + 1 < size)
            {
                canvas.Pixels[y * size + gap] = Color32.Transparent;
            }
        }

        for (int y = 0; y < size; y++)
        {
            canvas.Pixels[y * size] = wall;
            canvas.Pixels[y * size + size - 1] = wall;
        }

        return canvas;
    }
}
