using Microsoft.JSInterop;
using PixelStudio.Core.IO;
using PixelStudio.UI.Interop;
using PixelStudio.UI.Platform;

namespace PixelStudio.Web.Platform;

/// <summary>
/// Browser file access: the page cannot write to the user's disk directly, so saving
/// downloads a file (Blob + &lt;a download&gt;, supported by all evergreen browsers) and opening
/// uses an &lt;input type="file"&gt; picker. The File System Access API is intentionally not used:
/// it is Chromium-only (see README).
/// </summary>
public sealed class BrowserFileService : IFileService
{
    private readonly IJSRuntime _js;
    private Task<IJSObjectReference>? _module;

    public BrowserFileService(IJSRuntime js)
    {
        _js = js;
    }

    public bool SavesToDownloads => true;

    public async Task<OpenedFile?> OpenProjectAsync()
    {
        var module = await GetModuleAsync();
        var name = await module.InvokeAsync<string?>("pickFile", $"{ProjectSerializer.FileExtension},application/json");
        if (name is null)
        {
            return null;
        }

        var data = await module.InvokeAsync<byte[]>("takePickedBytes");
        return new OpenedFile(name, data);
    }

    public async Task<string?> SaveProjectAsync(string suggestedFileName, byte[] data, bool saveAs)
    {
        await DownloadAsync(suggestedFileName, "application/json", data);
        return suggestedFileName;
    }

    public async Task<string?> SavePngAsync(string suggestedFileName, byte[] data)
    {
        await DownloadAsync(suggestedFileName, "image/png", data);
        return suggestedFileName;
    }

    public async Task<string?> SaveSpriteSheetAsync(string suggestedPngFileName, Func<string, SpriteSheetExport> build)
    {
        var export = build(suggestedPngFileName);
        await DownloadAsync(suggestedPngFileName, "image/png", export.Png);
        await DownloadAsync(Path.ChangeExtension(suggestedPngFileName, ".json"), "application/json", System.Text.Encoding.UTF8.GetBytes(export.Json));
        return suggestedPngFileName;
    }

    public void ForgetProjectLocation()
    {
        // Downloads have no remembered location.
    }

    private async Task DownloadAsync(string fileName, string mime, byte[] data)
    {
        var module = await GetModuleAsync();
        await module.InvokeVoidAsync("downloadFile", fileName, mime, data);
    }

    private Task<IJSObjectReference> GetModuleAsync() =>
        _module ??= _js.InvokeAsync<IJSObjectReference>("import", EditorInterop.ModulePath).AsTask();
}
