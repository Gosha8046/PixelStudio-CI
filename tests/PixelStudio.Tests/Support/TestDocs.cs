using PixelStudio.Core.Imaging;

namespace PixelStudio.Tests.Support;

internal static class TestDocs
{
    public static readonly Color32 Red = new(255, 0, 0);
    public static readonly Color32 Green = new(0, 255, 0);
    public static readonly Color32 Blue = new(0, 0, 255);

    /// <summary>Snapshot of every cel's pixels, for "nothing else changed" assertions.</summary>
    public static Dictionary<(int Frame, int Layer), Color32[]> SnapshotPixels(SpriteDocument doc) =>
        doc.Frames.SelectMany(f => doc.Layers.Select(l => (Key: (f.Id, l.Id), Pixels: (Color32[])f.GetCel(l.Id).Pixels.Clone())))
            .ToDictionary(x => x.Key, x => x.Pixels);

    public static void AssertSamePixels(Dictionary<(int Frame, int Layer), Color32[]> expected, SpriteDocument doc)
    {
        var actual = SnapshotPixels(doc);
        Assert.Equal(expected.Keys.OrderBy(k => k), actual.Keys.OrderBy(k => k));
        foreach (var (key, pixels) in expected)
        {
            Assert.True(pixels.AsSpan().SequenceEqual(actual[key]), $"Pixels differ in cel {key}");
        }
    }

    public static void Fill(Canvas canvas, Color32 color) => canvas.Pixels.AsSpan().Fill(color);

    /// <summary>A canonical document description used to compare documents field by field.</summary>
    public static string Describe(SpriteDocument doc)
    {
        var parts = new List<string>
        {
            $"size={doc.Width}x{doc.Height} fps={doc.Timeline.Fps} loop={doc.Timeline.Loop}",
            "palette=" + string.Join(",", doc.Palette.Colors.Select(c => c.ToHexRgba())),
        };
        parts.AddRange(doc.Layers.Select(l => $"layer {l.Id} '{l.Name}' vis={l.Visible} op={l.Opacity} mode={l.BlendMode}"));
        foreach (var f in doc.Frames)
        {
            parts.Add($"frame {f.Id} dur={f.DurationMs?.ToString() ?? "default"}");
            foreach (var l in doc.Layers)
            {
                var px = f.GetCel(l.Id).Pixels;
                parts.Add($"  cel {l.Id}: {Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(px.AsSpan())))}");
            }
        }

        return string.Join("\n", parts);
    }
}
