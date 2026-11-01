namespace PixelStudio.UI.State;

public enum AppCommand
{
    ToolPencil,
    ToolEraser,
    ToolFill,
    ToolEyedropper,
    ToolHand,
    Undo,
    Redo,
    PlayPause,
    PreviousFrame,
    NextFrame,
    ToggleGrid,
    ToggleCrosshair,
    ToggleOnionSkin,
    ToggleTilePreview,
    ZoomIn,
    ZoomOut,
    ZoomFit,
    Save,
    Open,
    Export,
    Escape,
}

/// <summary>
/// Keyboard shortcuts. Combos use <c>KeyboardEvent.code</c> (physical keys), so they work
/// on any keyboard layout (e.g. Russian). The JS side only forwards combos listed in
/// <see cref="AllCombos"/> and never while focus is in a text field.
/// </summary>
public static class KeyMap
{
    private static readonly Dictionary<string, AppCommand> Bindings = new(StringComparer.Ordinal)
    {
        ["KeyB"] = AppCommand.ToolPencil,
        ["KeyE"] = AppCommand.ToolEraser,
        ["KeyG"] = AppCommand.ToolFill,
        ["KeyI"] = AppCommand.ToolEyedropper,
        ["KeyH"] = AppCommand.ToolHand,
        ["Ctrl+KeyZ"] = AppCommand.Undo,
        ["Ctrl+KeyY"] = AppCommand.Redo,
        ["Ctrl+Shift+KeyZ"] = AppCommand.Redo,
        ["KeyP"] = AppCommand.PlayPause,
        ["Comma"] = AppCommand.PreviousFrame,
        ["Period"] = AppCommand.NextFrame,
        ["Ctrl+Quote"] = AppCommand.ToggleGrid,
        ["KeyX"] = AppCommand.ToggleCrosshair,
        ["KeyO"] = AppCommand.ToggleOnionSkin,
        ["KeyT"] = AppCommand.ToggleTilePreview,
        ["Equal"] = AppCommand.ZoomIn,
        ["NumpadAdd"] = AppCommand.ZoomIn,
        ["Minus"] = AppCommand.ZoomOut,
        ["NumpadSubtract"] = AppCommand.ZoomOut,
        ["Digit0"] = AppCommand.ZoomFit,
        ["Ctrl+KeyS"] = AppCommand.Save,
        ["Ctrl+KeyO"] = AppCommand.Open,
        ["Ctrl+Shift+KeyE"] = AppCommand.Export,
        ["Escape"] = AppCommand.Escape,
    };

    public static string[] AllCombos { get; } = [.. Bindings.Keys];

    public static AppCommand? Resolve(string combo) =>
        Bindings.TryGetValue(combo, out var command) ? command : null;

    /// <summary>Human-readable hint for tooltips, e.g. "Ctrl+Z".</summary>
    public static string Hint(AppCommand command)
    {
        var combo = Bindings.FirstOrDefault(kv => kv.Value == command).Key;
        if (combo is null)
        {
            return string.Empty;
        }

        return combo
            .Replace("Key", string.Empty, StringComparison.Ordinal)
            .Replace("Digit", string.Empty, StringComparison.Ordinal)
            .Replace("Comma", ",", StringComparison.Ordinal)
            .Replace("Period", ".", StringComparison.Ordinal)
            .Replace("Equal", "+", StringComparison.Ordinal)
            .Replace("Minus", "−", StringComparison.Ordinal)
            .Replace("Quote", "'", StringComparison.Ordinal);
    }
}
