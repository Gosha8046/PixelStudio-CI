using System.Text;
using PixelStudio.Core.IO;
using PixelStudio.UI.Platform;

namespace PixelStudio.Windows.Platform;

/// <summary>Native Open/Save dialogs. Remembers the project path so Ctrl+S saves in place.</summary>
internal sealed class WindowsFileService : IFileService
{
    private const string ProjectFilter = "Проект PixelStudio (*.pxstudio)|*.pxstudio|Все файлы (*.*)|*.*";
    private const string PngFilter = "Изображение PNG (*.png)|*.png";

    private readonly Form _owner;
    private string? _projectPath;
    private string? _exportDirectory;

    public WindowsFileService(Form owner)
    {
        _owner = owner;
    }

    public bool SavesToDownloads => false;

    public Task<OpenedFile?> OpenProjectAsync() => OnUiThread(() =>
    {
        using var dialog = new OpenFileDialog
        {
            Filter = ProjectFilter,
            Title = "Открыть проект",
            InitialDirectory = DirectoryOf(_projectPath),
        };
        if (dialog.ShowDialog(_owner) != DialogResult.OK)
        {
            return null;
        }

        var info = new FileInfo(dialog.FileName);
        if (info.Length > ProjectSerializer.MaxFileBytes)
        {
            // Let the serializer produce its standard "file too large" message without reading it all.
            return new OpenedFile(info.Name, new byte[ProjectSerializer.MaxFileBytes + 1]);
        }

        var data = File.ReadAllBytes(dialog.FileName);
        _projectPath = dialog.FileName;
        return new OpenedFile(info.Name, data);
    });

    public Task<string?> SaveProjectAsync(string suggestedFileName, byte[] data, bool saveAs) =>
        OnUiThread(() => SaveProjectSync(suggestedFileName, data, saveAs));

    /// <summary>Synchronous variant used from FormClosing.</summary>
    public string? SaveProjectSync(string suggestedFileName, byte[] data, bool saveAs)
    {
        string? path = saveAs ? null : _projectPath;
        if (path is null)
        {
            using var dialog = new SaveFileDialog
            {
                Filter = ProjectFilter,
                Title = "Сохранить проект",
                FileName = suggestedFileName,
                DefaultExt = ProjectSerializer.FileExtension,
                AddExtension = true,
                InitialDirectory = DirectoryOf(_projectPath),
            };
            if (dialog.ShowDialog(_owner) != DialogResult.OK)
            {
                return null;
            }

            path = dialog.FileName;
        }

        WriteAtomically(path, data);
        _projectPath = path;
        return Path.GetFileName(path);
    }

    public Task<string?> SavePngAsync(string suggestedFileName, byte[] data) => OnUiThread(() =>
    {
        var path = AskExportPath(suggestedFileName, "Экспорт PNG");
        if (path is null)
        {
            return null;
        }

        WriteAtomically(path, data);
        return Path.GetFileName(path);
    });

    public Task<string?> SaveSpriteSheetAsync(string suggestedPngFileName, Func<string, SpriteSheetExport> build) => OnUiThread(() =>
    {
        var pngPath = AskExportPath(suggestedPngFileName, "Экспорт спрайтшита (рядом будет сохранён JSON)");
        if (pngPath is null)
        {
            return null;
        }

        // The JSON references the PNG by the file name the user actually chose.
        var export = build(Path.GetFileName(pngPath));
        WriteAtomically(pngPath, export.Png);
        WriteAtomically(Path.ChangeExtension(pngPath, ".json"), Encoding.UTF8.GetBytes(export.Json));
        return Path.GetFileName(pngPath);
    });

    public void ForgetProjectLocation() => _projectPath = null;

    private string? AskExportPath(string suggestedFileName, string title)
    {
        using var dialog = new SaveFileDialog
        {
            Filter = PngFilter,
            Title = title,
            FileName = suggestedFileName,
            DefaultExt = ".png",
            AddExtension = true,
            InitialDirectory = _exportDirectory ?? DirectoryOf(_projectPath),
        };
        if (dialog.ShowDialog(_owner) != DialogResult.OK)
        {
            return null;
        }

        _exportDirectory = Path.GetDirectoryName(dialog.FileName);
        return dialog.FileName;
    }

    /// <summary>Writes to a temporary file first so a failed write never corrupts an existing file.</summary>
    private static void WriteAtomically(string path, byte[] data)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, data);
        File.Move(temp, path, overwrite: true);
    }

    private static string DirectoryOf(string? path) =>
        path is null ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) : Path.GetDirectoryName(path) ?? string.Empty;

    /// <summary>Dialogs must run on the WinForms UI thread; Control.Invoke runs inline when already there.</summary>
    private Task<T> OnUiThread<T>(Func<T> action) =>
        Task.FromResult(_owner.InvokeRequired ? (T)_owner.Invoke(action)! : action());
}
