using System.Runtime.InteropServices;
using PixelStudio.Core.Imaging;

namespace PixelStudio.Core.Rendering;

/// <summary>Onion-skin display settings (view state; never saved into exports).</summary>
public sealed class OnionSkinSettings
{
    public bool Enabled { get; set; }
    public bool ShowPrevious { get; set; } = true;
    public bool ShowNext { get; set; } = true;

    /// <summary>Opacity of the ghost frames, 0..100 %.</summary>
    public int Opacity { get; set; } = 40;

    public Color32 PreviousTint { get; set; } = new(255, 72, 72);
    public Color32 NextTint { get; set; } = new(64, 156, 255);
}

/// <summary>
/// Builds the image shown in the editor viewport: the composited current frame, with
/// tinted previous/next frames ("ghosts") drawn underneath when onion skinning is on.
/// Display only — the exporter never calls this.
/// </summary>
/// <remarks>
/// <see cref="Render"/> recomputes everything and caches the ghost image;
/// <see cref="RenderRegion"/> then updates only a rectangle of the current frame, which is
/// what happens on every pointer move while drawing. Buffers are reused, so neither path
/// allocates. Output is exposed as RGBA byte arrays for zero-copy transfer to the UI layer.
/// </remarks>
public sealed class ViewRenderer
{
    private const float TintStrength = 0.65f;

    private byte[] _viewBytes = [];
    private byte[] _compositeBytes = [];
    private Color32[] _ghosts = [];
    private Color32[] _scratch = [];
    private bool _hasGhosts;
    private SpriteDocument? _cachedDocument;
    private int _cachedFrameIndex = -1;

    /// <summary>RGBA bytes of the view (composite with onion skin).</summary>
    public byte[] ViewBytes => _viewBytes;

    /// <summary>RGBA bytes of the plain composite of the current frame (no overlays).</summary>
    public byte[] CompositeBytes => _compositeBytes;

    /// <summary>Full render of frame <paramref name="frameIndex"/>; refreshes the ghost cache.</summary>
    public void Render(SpriteDocument document, int frameIndex, OnionSkinSettings onion)
    {
        int pixelCount = document.Width * document.Height;
        EnsureCapacity(pixelCount);

        var frames = document.Frames;
        frameIndex = Math.Clamp(frameIndex, 0, frames.Count - 1);
        Compositor.CompositeFrame(document, frames[frameIndex], Composite);

        _hasGhosts = false;
        if (onion.Enabled && frames.Count > 1 && onion.Opacity > 0)
        {
            Array.Clear(_ghosts);
            if (onion.ShowPrevious && frameIndex > 0)
            {
                DrawGhost(document, frameIndex - 1, onion.PreviousTint, onion.Opacity);
                _hasGhosts = true;
            }

            if (onion.ShowNext && frameIndex < frames.Count - 1)
            {
                DrawGhost(document, frameIndex + 1, onion.NextTint, onion.Opacity);
                _hasGhosts = true;
            }
        }

        _cachedDocument = document;
        _cachedFrameIndex = frameIndex;
        ComposeView(new PixelRect(0, 0, document.Width, document.Height), document.Width);
    }

    /// <summary>True when <see cref="RenderRegion"/> may be used (same document, size and frame as the last full render).</summary>
    public bool CanRenderRegion(SpriteDocument document, int frameIndex) =>
        ReferenceEquals(document, _cachedDocument) &&
        frameIndex == _cachedFrameIndex &&
        _scratch.Length == document.Width * document.Height;

    /// <summary>
    /// Updates only <paramref name="region"/> after pixels of the current frame changed.
    /// Other frames (and therefore the cached ghosts) must be unchanged since <see cref="Render"/>.
    /// </summary>
    /// <returns>The clipped region that was updated.</returns>
    public PixelRect RenderRegion(SpriteDocument document, int frameIndex, PixelRect region)
    {
        if (!CanRenderRegion(document, frameIndex))
        {
            throw new InvalidOperationException("Call Render first for this document and frame.");
        }

        region = Compositor.Clip(region, document.Width, document.Height);
        if (!region.IsEmpty)
        {
            Compositor.CompositeRegion(document, document.Frames[frameIndex], Composite, region);
            ComposeView(region, document.Width);
        }

        return region;
    }

    private Span<Color32> Composite => MemoryMarshal.Cast<byte, Color32>(_compositeBytes.AsSpan());

    private Span<Color32> View => MemoryMarshal.Cast<byte, Color32>(_viewBytes.AsSpan());

    private void ComposeView(PixelRect region, int width)
    {
        var composite = Composite;
        var view = View;
        for (int y = region.Y; y < region.Bottom; y++)
        {
            int offset = y * width + region.X;
            var target = view.Slice(offset, region.Width);
            var current = composite.Slice(offset, region.Width);
            if (_hasGhosts)
            {
                _ghosts.AsSpan(offset, region.Width).CopyTo(target);
                Compositor.BlendLayer(target, current, 100, BlendMode.Normal);
            }
            else
            {
                current.CopyTo(target);
            }
        }
    }

    private void DrawGhost(SpriteDocument document, int index, Color32 tint, int opacity)
    {
        var scratch = _scratch.AsSpan();
        Compositor.CompositeFrame(document, document.Frames[index], scratch);
        for (int i = 0; i < scratch.Length; i++)
        {
            var p = scratch[i];
            if (p.A == 0)
            {
                continue;
            }

            scratch[i] = new Color32(Mix(p.R, tint.R), Mix(p.G, tint.G), Mix(p.B, tint.B), p.A);
        }

        Compositor.BlendLayer(_ghosts, scratch, opacity, BlendMode.Normal);

        static byte Mix(byte value, byte tintValue) => (byte)(value + (tintValue - value) * TintStrength + 0.5f);
    }

    private void EnsureCapacity(int pixelCount)
    {
        if (_scratch.Length == pixelCount)
        {
            return;
        }

        _scratch = new Color32[pixelCount];
        _ghosts = new Color32[pixelCount];
        _viewBytes = new byte[pixelCount * 4];
        _compositeBytes = new byte[pixelCount * 4];
        _cachedDocument = null;
    }
}
