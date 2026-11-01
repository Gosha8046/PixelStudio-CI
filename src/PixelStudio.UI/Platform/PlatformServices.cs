using PixelStudio.Core.IO;

namespace PixelStudio.UI.Platform;

public sealed record OpenedFile(string Name, byte[] Data);

/// <summary>
/// Platform file access. The browser cannot write to arbitrary paths, so its implementation
/// downloads files and opens them through the file picker; the Windows implementation uses
/// native Open/Save dialogs and remembers the project path.
/// </summary>
public interface IFileService
{
    /// <summary>True when "saving" means downloading (browser). The UI adapts its wording.</summary>
    bool SavesToDownloads { get; }

    /// <returns>The chosen project file, or null when the user cancelled.</returns>
    Task<OpenedFile?> OpenProjectAsync();

    /// <param name="saveAs">When false, a desktop host may overwrite the last project location.</param>
    /// <returns>The saved file name (without directory), or null when cancelled.</returns>
    Task<string?> SaveProjectAsync(string suggestedFileName, byte[] data, bool saveAs);

    Task<string?> SavePngAsync(string suggestedFileName, byte[] data);

    /// <summary>
    /// Saves a sprite sheet PNG and its JSON side by side. <paramref name="build"/> receives the
    /// final PNG file name, so the JSON "image" field always matches the file actually written.
    /// </summary>
    /// <returns>The PNG file name, or null when cancelled.</returns>
    Task<string?> SaveSpriteSheetAsync(string suggestedPngFileName, Func<string, SpriteSheetExport> build);

    /// <summary>Called for a new document: the next save must ask for a location again.</summary>
    void ForgetProjectLocation();
}

/// <summary>Window title and unsaved-changes integration.</summary>
public interface IPlatformShell
{
    string PlatformName { get; }

    /// <summary>Browser: document.title + beforeunload guard. Windows: form caption (close is guarded by the form).</summary>
    Task UpdateDocumentStateAsync(string title, bool isDirty);
}
