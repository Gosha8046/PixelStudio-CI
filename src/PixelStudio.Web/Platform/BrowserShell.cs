using Microsoft.JSInterop;
using PixelStudio.UI.Interop;
using PixelStudio.UI.Platform;

namespace PixelStudio.Web.Platform;

/// <summary>Updates the tab title and arms the browser's "leave page?" prompt while there are unsaved changes.</summary>
public sealed class BrowserShell : IPlatformShell
{
    private readonly IJSRuntime _js;
    private Task<IJSObjectReference>? _module;

    public BrowserShell(IJSRuntime js)
    {
        _js = js;
    }

    public string PlatformName => "браузер (WebAssembly)";

    public async Task UpdateDocumentStateAsync(string title, bool isDirty)
    {
        try
        {
            _module ??= _js.InvokeAsync<IJSObjectReference>("import", EditorInterop.ModulePath).AsTask();
            await (await _module).InvokeVoidAsync("setDocumentState", title, isDirty);
        }
        catch (JSException)
        {
            // Title updates are cosmetic; never break editing because of them.
        }
    }
}
