namespace PixelStudio.Core;

/// <summary>
/// Documented limits of a document. They keep memory bounded in the browser
/// (WebAssembly heap) and are enforced on creation and when loading project files.
/// </summary>
public static class DocumentLimits
{
    /// <summary>Canvas width/height range: 1..512 pixels per side.</summary>
    public const int MinSize = 1;
    public const int MaxSize = 512;

    public const int MaxLayers = 64;
    public const int MaxFrames = 256;

    public const int MinFps = 1;
    public const int MaxFps = 60;

    public const int MinFrameDurationMs = 1;
    public const int MaxFrameDurationMs = 60_000;

    public const int MaxPaletteColors = 64;
    public const int MaxNameLength = 64;

    /// <summary>Largest sprite sheet side produced by the exporter.</summary>
    public const int MaxAtlasSize = 8192;

    /// <summary>Largest atlas area (pixels); 16M px = 64 MB of RGBA.</summary>
    public const long MaxAtlasArea = 4096L * 4096L;

    public static readonly int[] PresetSizes = [16, 32, 64];

    public static bool IsValidSize(int width, int height) =>
        width is >= MinSize and <= MaxSize && height is >= MinSize and <= MaxSize;
}
