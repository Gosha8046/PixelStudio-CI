using System.Globalization;
using System.Runtime.InteropServices;

namespace PixelStudio.Core.Imaging;

/// <summary>
/// 8-bit-per-channel sRGB color with <b>straight (non-premultiplied)</b> alpha.
/// Memory layout is R, G, B, A, which matches HTML canvas <c>ImageData</c> and PNG
/// color type 6, so a <c>Color32[]</c> can be reinterpreted as RGBA bytes without copying.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 4)]
public readonly struct Color32 : IEquatable<Color32>
{
    public readonly byte R;
    public readonly byte G;
    public readonly byte B;
    public readonly byte A;

    public Color32(byte r, byte g, byte b, byte a = 255)
    {
        R = r;
        G = g;
        B = b;
        A = a;
    }

    public static Color32 Transparent => default;
    public static Color32 Black => new(0, 0, 0);
    public static Color32 White => new(255, 255, 255);

    public bool IsTransparent => A == 0;

    public Color32 WithAlpha(byte a) => new(R, G, B, a);

    /// <summary>
    /// Fill/selection equivalence: every fully transparent pixel is treated as the same
    /// color regardless of its (invisible) RGB values; otherwise exact RGBA equality.
    /// </summary>
    public static bool SameVisible(Color32 a, Color32 b) => (a.A == 0 && b.A == 0) || a.Equals(b);

    public string ToHex() => A == 255
        ? string.Create(CultureInfo.InvariantCulture, $"#{R:X2}{G:X2}{B:X2}")
        : string.Create(CultureInfo.InvariantCulture, $"#{R:X2}{G:X2}{B:X2}{A:X2}");

    /// <summary>Always 8 hex digits (#RRGGBBAA); used by the project format.</summary>
    public string ToHexRgba() => string.Create(CultureInfo.InvariantCulture, $"#{R:X2}{G:X2}{B:X2}{A:X2}");

    /// <summary>Parses #RGB, #RRGGBB or #RRGGBBAA (the leading '#' is optional).</summary>
    public static bool TryParseHex(string? text, out Color32 color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        ReadOnlySpan<char> s = text.AsSpan().Trim();
        if (s.Length > 0 && s[0] == '#')
        {
            s = s[1..];
        }

        static bool Hex(ReadOnlySpan<char> part, out byte value) =>
            byte.TryParse(part, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);

        switch (s.Length)
        {
            case 3:
                if (Hex(stackalloc char[] { s[0], s[0] }, out var r3) &&
                    Hex(stackalloc char[] { s[1], s[1] }, out var g3) &&
                    Hex(stackalloc char[] { s[2], s[2] }, out var b3))
                {
                    color = new Color32(r3, g3, b3);
                    return true;
                }

                return false;
            case 6:
            case 8:
                if (Hex(s[..2], out var r) && Hex(s[2..4], out var g) && Hex(s[4..6], out var b))
                {
                    byte a = 255;
                    if (s.Length == 8 && !Hex(s[6..8], out a))
                    {
                        return false;
                    }

                    color = new Color32(r, g, b, a);
                    return true;
                }

                return false;
            default:
                return false;
        }
    }

    public bool Equals(Color32 other) => R == other.R && G == other.G && B == other.B && A == other.A;

    public override bool Equals(object? obj) => obj is Color32 other && Equals(other);

    public override int GetHashCode() => R | (G << 8) | (B << 16) | (A << 24);

    public static bool operator ==(Color32 left, Color32 right) => left.Equals(right);

    public static bool operator !=(Color32 left, Color32 right) => !left.Equals(right);

    public override string ToString() => $"RGBA({R}, {G}, {B}, {A})";
}
