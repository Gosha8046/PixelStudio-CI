using PixelStudio.Core.Animation;
using PixelStudio.Core.Imaging;

namespace PixelStudio.Core;

/// <summary>
/// Layer compositing. The rules (also documented in docs/architecture.md):
/// <list type="number">
///   <item>Storage: 8-bit sRGB with straight (non-premultiplied) alpha. No gamma
///   linearization — blending happens on sRGB values, like most pixel-art editors.</item>
///   <item>Order: layer 0 (bottom) to top. Hidden layers are skipped. Start from a fully
///   transparent backdrop.</item>
///   <item>Effective source alpha: αs = A/255 × opacity/100.</item>
///   <item>Blend function B(Cb, Cs): Normal = Cs; Multiply = Cb·Cs; Screen = Cb + Cs − Cb·Cs.</item>
///   <item>W3C source-over with blending:
///   Cs' = (1 − αb)·Cs + αb·B(Cb, Cs);
///   αo = αs + αb·(1 − αs);
///   Co = (αs·Cs' + αb·Cb·(1 − αs)) / αo.
///   Over a transparent backdrop every mode therefore reduces to the source itself.</item>
///   <item>Results are rounded to nearest (half up) and clamped to 0..255;
///   αo = 0 yields (0, 0, 0, 0).</item>
/// </list>
/// </summary>
public static class Compositor
{
    /// <summary>Composites the visible layers of <paramref name="frame"/> into <paramref name="destination"/>.</summary>
    public static void CompositeFrame(SpriteDocument document, Frame frame, Span<Color32> destination)
    {
        if (destination.Length != document.Width * document.Height)
        {
            throw new ArgumentException("Destination size does not match the document.", nameof(destination));
        }

        destination.Clear();
        foreach (var layer in document.LayersInternal)
        {
            if (!layer.Visible || layer.Opacity == 0)
            {
                continue;
            }

            BlendLayer(destination, frame.GetCel(layer.Id).Pixels, layer.Opacity, layer.BlendMode);
        }
    }

    /// <summary>
    /// Recomputes only <paramref name="region"/> of a full-size composite buffer. Used while
    /// drawing, so the cost per pointer event is proportional to the changed area, not the canvas.
    /// </summary>
    public static void CompositeRegion(SpriteDocument document, Frame frame, Span<Color32> destination, PixelRect region)
    {
        int width = document.Width;
        region = Clip(region, width, document.Height);
        if (region.IsEmpty)
        {
            return;
        }

        for (int y = region.Y; y < region.Bottom; y++)
        {
            destination.Slice(y * width + region.X, region.Width).Clear();
        }

        foreach (var layer in document.LayersInternal)
        {
            if (!layer.Visible || layer.Opacity == 0)
            {
                continue;
            }

            var source = frame.GetCel(layer.Id).Pixels.AsSpan();
            for (int y = region.Y; y < region.Bottom; y++)
            {
                int offset = y * width + region.X;
                BlendLayer(destination.Slice(offset, region.Width), source.Slice(offset, region.Width), layer.Opacity, layer.BlendMode);
            }
        }
    }

    public static PixelRect Clip(PixelRect rect, int width, int height)
    {
        int x0 = Math.Max(0, rect.X);
        int y0 = Math.Max(0, rect.Y);
        int x1 = Math.Min(width, rect.Right);
        int y1 = Math.Min(height, rect.Bottom);
        return x1 > x0 && y1 > y0 ? new PixelRect(x0, y0, x1 - x0, y1 - y0) : default;
    }

    /// <summary>Allocating convenience overload (used by export and tests, not by the render loop).</summary>
    public static Canvas CompositeFrame(SpriteDocument document, Frame frame)
    {
        var canvas = document.NewCanvas();
        CompositeFrame(document, frame, canvas.Pixels);
        return canvas;
    }

    /// <summary>Blends <paramref name="source"/> onto <paramref name="backdrop"/> in place.</summary>
    public static void BlendLayer(Span<Color32> backdrop, ReadOnlySpan<Color32> source, int opacityPercent, BlendMode mode)
    {
        if (backdrop.Length != source.Length)
        {
            throw new ArgumentException("Buffers must have equal length.");
        }

        opacityPercent = Math.Clamp(opacityPercent, 0, 100);
        if (opacityPercent == 0)
        {
            return;
        }

        bool fullOpacity = opacityPercent == 100;
        for (int i = 0; i < source.Length; i++)
        {
            var s = source[i];
            if (s.A == 0)
            {
                continue; // Fully transparent source never changes the backdrop.
            }

            ref var b = ref backdrop[i];
            if (fullOpacity && s.A == 255 && mode == BlendMode.Normal)
            {
                b = s; // Opaque Normal source simply replaces the backdrop.
                continue;
            }

            b = Blend(b, s, opacityPercent, mode);
        }
    }

    /// <summary>Blends a single source pixel over a backdrop pixel (see class remarks).</summary>
    public static Color32 Blend(Color32 backdrop, Color32 source, int opacityPercent, BlendMode mode)
    {
        double alphaS = source.A / 255.0 * (Math.Clamp(opacityPercent, 0, 100) / 100.0);
        if (alphaS <= 0)
        {
            return backdrop;
        }

        if (backdrop.A == 0)
        {
            // αb = 0: Cs' = Cs and Co = Cs for every blend mode.
            return new Color32(source.R, source.G, source.B, ToByte(alphaS));
        }

        double alphaB = backdrop.A / 255.0;
        double alphaO = alphaS + alphaB * (1 - alphaS);

        return new Color32(
            Channel(backdrop.R, source.R, alphaB, alphaS, alphaO, mode),
            Channel(backdrop.G, source.G, alphaB, alphaS, alphaO, mode),
            Channel(backdrop.B, source.B, alphaB, alphaS, alphaO, mode),
            ToByte(alphaO));
    }

    private static byte Channel(byte backdrop, byte source, double alphaB, double alphaS, double alphaO, BlendMode mode)
    {
        double cb = backdrop / 255.0;
        double cs = source / 255.0;
        double blended = mode switch
        {
            BlendMode.Multiply => cb * cs,
            BlendMode.Screen => cb + cs - cb * cs,
            _ => cs,
        };

        double mixed = (1 - alphaB) * cs + alphaB * blended;
        double premultiplied = alphaS * mixed + alphaB * cb * (1 - alphaS);
        return ToByte(premultiplied / alphaO);
    }

    private static byte ToByte(double unit)
    {
        // Small epsilon guards against values like 127.49999999 caused by binary fractions.
        double v = unit * 255.0 + 0.5 + 1e-9;
        return v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)v;
    }
}
