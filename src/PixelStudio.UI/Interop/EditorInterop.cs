using Microsoft.JSInterop;

namespace PixelStudio.UI.Interop;

/// <summary>Lazily imports the shared JS module (one instance per JS runtime).</summary>
public sealed class EditorInterop : IAsyncDisposable
{
    public const string ModulePath = "./_content/PixelStudio.UI/pixelstudio.js";

    private readonly IJSRuntime _js;
    private Task<IJSObjectReference>? _module;

    public EditorInterop(IJSRuntime js)
    {
        _js = js;
    }

    public Task<IJSObjectReference> GetModuleAsync() =>
        _module ??= _js.InvokeAsync<IJSObjectReference>("import", ModulePath).AsTask();

    public async ValueTask DisposeAsync()
    {
        if (_module is { IsCompletedSuccessfully: true })
        {
            try
            {
                await (await _module).DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // The WebView is already gone (desktop shutdown).
            }
        }
    }
}
