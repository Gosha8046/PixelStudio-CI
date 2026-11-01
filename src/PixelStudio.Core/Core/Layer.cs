namespace PixelStudio.Core;

/// <summary>Separable blend modes, defined as in W3C "Compositing and Blending Level 1".</summary>
public enum BlendMode
{
    Normal = 0,
    Multiply = 1,
    Screen = 2,
}

/// <summary>
/// Layer properties shared by all frames (the layer stack is common to the whole animation).
/// Pixel data lives per frame in <see cref="Animation.Frame.Cels"/>, keyed by <see cref="Id"/>.
/// </summary>
/// <remarks>
/// Mutate layers only through <see cref="LayerManager"/> so that every change is recorded
/// in the undo history.
/// </remarks>
public sealed class Layer
{
    private string _name;
    private int _opacity = 100;

    public Layer(int id, string name)
    {
        Id = id;
        _name = NormalizeName(name);
    }

    /// <summary>Document-unique, stable identifier (survives reordering and save/load).</summary>
    public int Id { get; }

    public string Name
    {
        get => _name;
        internal set => _name = NormalizeName(value);
    }

    public bool Visible { get; internal set; } = true;

    /// <summary>Layer opacity in percent, 0..100 (integer, so it round-trips losslessly).</summary>
    public int Opacity
    {
        get => _opacity;
        internal set => _opacity = Math.Clamp(value, 0, 100);
    }

    public BlendMode BlendMode { get; internal set; } = BlendMode.Normal;

    internal LayerState ToState() => new(Id, Name, Visible, Opacity, BlendMode);

    internal static Layer FromState(LayerState state) => new(state.Id, state.Name)
    {
        Visible = state.Visible,
        Opacity = state.Opacity,
        BlendMode = state.BlendMode,
    };

    internal static string NormalizeName(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            trimmed = "Слой";
        }

        return trimmed.Length > DocumentLimits.MaxNameLength ? trimmed[..DocumentLimits.MaxNameLength] : trimmed;
    }
}

/// <summary>Immutable value snapshot of a layer's properties (used by undo history).</summary>
internal readonly record struct LayerState(int Id, string Name, bool Visible, int Opacity, BlendMode BlendMode);
