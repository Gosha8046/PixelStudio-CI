using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using PixelStudio.Core.Imaging;

namespace PixelStudio.Core.IO;

/// <summary>Sprite sheet options. <paramref name="ImageFileName"/> is written into the JSON "image" field.</summary>
public sealed record SpriteSheetOptions(int Columns, int Spacing, string ImageFileName);

/// <summary>
/// Fixed-cell grid layout (MVP): frames in timeline order, left-to-right then top-to-bottom,
/// no trimming, no rotation, <see cref="Spacing"/> pixels between neighbouring cells and
/// no outer margin.
/// </summary>
public sealed record SpriteSheetLayout(
    int FrameWidth,
    int FrameHeight,
    int FrameCount,
    int Columns,
    int Rows,
    int Spacing,
    int AtlasWidth,
    int AtlasHeight)
{
    public const int MaxSpacing = 64;

    public PixelRect GetFrameRect(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, FrameCount);
        int column = index % Columns;
        int row = index / Columns;
        return new PixelRect(column * (FrameWidth + Spacing), row * (FrameHeight + Spacing), FrameWidth, FrameHeight);
    }

    /// <summary>Builds and validates a layout. Columns are clamped to 1..frameCount.</summary>
    public static SpriteSheetLayout Create(int frameCount, int frameWidth, int frameHeight, int columns, int spacing)
    {
        if (frameCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameCount));
        }

        if (spacing is < 0 or > MaxSpacing)
        {
            throw new ArgumentOutOfRangeException(nameof(spacing), $"Отступ между кадрами: 0–{MaxSpacing} px.");
        }

        columns = Math.Clamp(columns, 1, frameCount);
        int rows = (frameCount + columns - 1) / columns;
        long width = (long)columns * frameWidth + (long)(columns - 1) * spacing;
        long height = (long)rows * frameHeight + (long)(rows - 1) * spacing;
        if (width > DocumentLimits.MaxAtlasSize || height > DocumentLimits.MaxAtlasSize || width * height > DocumentLimits.MaxAtlasArea)
        {
            throw new InvalidOperationException(
                $"Спрайтшит {width}×{height} слишком большой (не более {DocumentLimits.MaxAtlasSize} px по стороне и 4096×4096 по площади). Измените число столбцов.");
        }

        return new SpriteSheetLayout(frameWidth, frameHeight, frameCount, columns, rows, spacing, (int)width, (int)height);
    }
}

public sealed record SpriteSheetExport(byte[] Png, string Json, SpriteSheetLayout Layout);

/// <summary>
/// Asset export: PNG of a frame, PNG sprite sheet and the engine-neutral JSON description.
/// Works only on document data via <see cref="Compositor"/> — editor overlays (grid,
/// crosshair, onion skin, tile preview) are view state and cannot leak into exports.
/// </summary>
public static class Exporter
{
    public const string SheetFormatId = "pixelstudio-spritesheet";
    public const int SheetFormatVersion = 1;

    // Keep non-ASCII file names (e.g. Cyrillic) readable instead of \uXXXX escapes.
    private static readonly SheetJsonContext JsonContext = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    });

    /// <summary>PNG (RGBA, transparency preserved) of the composited frame.</summary>
    public static byte[] ExportFramePng(SpriteDocument document, int frameIndex)
    {
        var canvas = Compositor.CompositeFrame(document, document.Frames[frameIndex]);
        return PngEncoder.Encode(canvas.Width, canvas.Height, canvas.Pixels);
    }

    /// <summary>Builds the sprite sheet PNG and its JSON from one shared layout, so they always agree.</summary>
    public static SpriteSheetExport ExportSpriteSheet(SpriteDocument document, SpriteSheetOptions options)
    {
        var imageName = NormalizeImageFileName(options.ImageFileName);
        var layout = SpriteSheetLayout.Create(document.Frames.Count, document.Width, document.Height, options.Columns, options.Spacing);
        var atlas = new Color32[layout.AtlasWidth * layout.AtlasHeight];
        var frameBuffer = new Color32[document.Width * document.Height];

        for (int i = 0; i < document.Frames.Count; i++)
        {
            Compositor.CompositeFrame(document, document.Frames[i], frameBuffer);
            var rect = layout.GetFrameRect(i);
            for (int row = 0; row < rect.Height; row++)
            {
                frameBuffer.AsSpan(row * rect.Width, rect.Width)
                    .CopyTo(atlas.AsSpan((rect.Y + row) * layout.AtlasWidth + rect.X, rect.Width));
            }
        }

        var png = PngEncoder.Encode(layout.AtlasWidth, layout.AtlasHeight, atlas);
        var json = BuildSheetJson(document, layout, imageName);
        return new SpriteSheetExport(png, json, layout);
    }

    public static string BuildSheetJson(SpriteDocument document, SpriteSheetLayout layout, string imageFileName)
    {
        var durations = document.Timeline.GetDurations();
        var dto = new SheetDto
        {
            Format = SheetFormatId,
            Version = SheetFormatVersion,
            Generator = $"PixelStudio CI {typeof(Exporter).Assembly.GetName().Version?.ToString(3)}",
            Image = imageFileName,
            Atlas = new SizeDto { Width = layout.AtlasWidth, Height = layout.AtlasHeight },
            FrameSize = new SizeDto { Width = layout.FrameWidth, Height = layout.FrameHeight },
            Layout = new LayoutDto { Columns = layout.Columns, Rows = layout.Rows, Spacing = layout.Spacing },
            Frames = Enumerable.Range(0, layout.FrameCount).Select(i =>
            {
                var r = layout.GetFrameRect(i);
                return new FrameEntryDto { Index = i, X = r.X, Y = r.Y, Width = r.Width, Height = r.Height, DurationMs = durations[i] };
            }).ToList(),
            Playback = new PlaybackDto
            {
                Order = Enumerable.Range(0, layout.FrameCount).ToList(),
                Loop = document.Timeline.Loop,
                TotalDurationMs = durations.Sum(),
            },
        };

        return JsonSerializer.Serialize(dto, JsonContext.SheetDto);
    }

    /// <summary>Keeps only a safe file name and guarantees the ".png" extension.</summary>
    public static string NormalizeImageFileName(string? name)
    {
        var fileName = Path.GetFileName((name ?? string.Empty).Replace('\\', '/').Split('/').Last()).Trim();
        foreach (var invalid in "<>:\"|?*")
        {
            fileName = fileName.Replace(invalid, '_');
        }

        if (fileName.Length == 0 || fileName == ".png")
        {
            fileName = "sprite.png";
        }

        return fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? fileName : fileName + ".png";
    }
}

internal sealed class SheetDto
{
    public string Format { get; set; } = "";
    public int Version { get; set; }
    public string? Generator { get; set; }
    public string Image { get; set; } = "";
    public SizeDto Atlas { get; set; } = new();
    public SizeDto FrameSize { get; set; } = new();
    public LayoutDto Layout { get; set; } = new();
    public List<FrameEntryDto> Frames { get; set; } = [];
    public PlaybackDto Playback { get; set; } = new();
}

internal sealed class SizeDto
{
    public int Width { get; set; }
    public int Height { get; set; }
}

internal sealed class LayoutDto
{
    public int Columns { get; set; }
    public int Rows { get; set; }
    public int Spacing { get; set; }
}

internal sealed class FrameEntryDto
{
    public int Index { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int DurationMs { get; set; }
}

internal sealed class PlaybackDto
{
    public List<int> Order { get; set; } = [];
    public bool Loop { get; set; }
    public int TotalDurationMs { get; set; }
}

[JsonSerializable(typeof(SheetDto))]
internal sealed partial class SheetJsonContext : JsonSerializerContext;
