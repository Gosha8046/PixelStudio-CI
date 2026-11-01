using PixelStudio.Core.Imaging;

namespace PixelStudio.Core;

/// <summary>Small document palette (saved with the project). Edit through <see cref="SpriteDocument"/>.</summary>
public sealed class Palette
{
    /// <summary>
    /// Default 16-color palette designed for this project (neutral ramp + warm/cool accents).
    /// </summary>
    public static IReadOnlyList<Color32> Default16 { get; } =
    [
        new(0x1A, 0x1C, 0x2C), new(0x5D, 0x27, 0x5D), new(0xB1, 0x3E, 0x53), new(0xEF, 0x7D, 0x57),
        new(0xFF, 0xCD, 0x75), new(0xA7, 0xF0, 0x70), new(0x38, 0xB7, 0x64), new(0x25, 0x71, 0x79),
        new(0x29, 0x36, 0x6F), new(0x3B, 0x5D, 0xC9), new(0x41, 0xA6, 0xF6), new(0x73, 0xEF, 0xF7),
        new(0xF4, 0xF4, 0xF4), new(0x94, 0xB0, 0xC2), new(0x56, 0x6C, 0x86), new(0x33, 0x3C, 0x57),
    ];

    internal Palette(IEnumerable<Color32> colors)
    {
        ColorsInternal = colors.Take(DocumentLimits.MaxPaletteColors).ToList();
    }

    internal List<Color32> ColorsInternal { get; }

    public IReadOnlyList<Color32> Colors => ColorsInternal;
}
