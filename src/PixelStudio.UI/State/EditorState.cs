using System.Diagnostics;
using PixelStudio.Core;
using PixelStudio.Core.Animation;
using PixelStudio.Core.Editing;
using PixelStudio.Core.Imaging;
using PixelStudio.Core.IO;
using PixelStudio.Core.Rendering;
using PixelStudio.UI.Platform;

namespace PixelStudio.UI.State;

public enum DialogKind
{
    None,
    NewProject,
    Export,
    ConfirmDiscard,
}

public enum StatusKind
{
    Info,
    Success,
    Warning,
    Error,
}

public enum ViewCommand
{
    ZoomIn,
    ZoomOut,
    Fit,
}

public enum ConfirmChoice
{
    Save,
    Discard,
    Cancel,
}

/// <summary>
/// Editor session state shared by all components: the open document, selection (frame,
/// layer), active tool and color, view toggles, playback and dialogs. Components call its
/// methods; it raises <see cref="Changed"/> (re-render UI) and <see cref="CanvasInvalidated"/>
/// (re-present pixels). No platform code here — files go through <see cref="IFileService"/>.
/// </summary>
public sealed class EditorState
{
    public const string UntitledName = "Без имени";

    private readonly IFileService _files;
    private readonly IPlatformShell _shell;
    private readonly PlaybackClock _clock = new();
    private readonly Dictionary<int, long> _frameRevisions = [];
    private CancellationTokenSource? _playback;
    private Func<Task>? _afterConfirm;
    private long _revisionCounter;
    private long _globalRevision;
    private int? _pixelEditFrameId;

    public EditorState(IFileService files, IPlatformShell shell)
    {
        _files = files;
        _shell = shell;
        Document = null!; // set by LoadDocument
        LoadDocument(SpriteDocument.Create(32, 32), UntitledName);
    }

    public SpriteDocument Document { get; private set; }
    public string DocumentName { get; private set; } = UntitledName;

    /// <summary>Incremented whenever a different document is loaded (the viewport re-fits).</summary>
    public int DocumentGeneration { get; private set; }

    public int CurrentFrameIndex { get; private set; }
    public int CurrentLayerId { get; private set; }
    public Frame CurrentFrame => Document.Frames[CurrentFrameIndex];
    public Layer CurrentLayer => Document.FindLayer(CurrentLayerId);

    public ToolController Tools { get; } = new();
    public OnionSkinSettings Onion { get; } = new();

    public bool ShowGrid { get; private set; } = true;
    public bool ShowCrosshair { get; private set; } = true;
    public bool ShowTilePreview { get; private set; }
    public int Zoom { get; private set; } = 1;

    public bool IsPlaying => _clock.IsPlaying;
    public bool IsDirty => Document.History.IsDirty;
    public bool SavesToDownloads => _files.SavesToDownloads;
    public string PlatformName => _shell.PlatformName;

    public DialogKind Dialog { get; private set; }
    public string? Status { get; private set; }
    public StatusKind StatusKind { get; private set; }

    /// <summary>File name stem for saving/exporting; untitled documents get an ASCII default.</summary>
    public string FileStem => DocumentName == UntitledName ? "sprite" : DocumentName;

    public string WindowTitle => $"{(IsDirty ? "• " : string.Empty)}{DocumentName} — PixelStudio CI";

    /// <summary>UI needs re-rendering (selection, lists, buttons, status...).</summary>
    public event Action? Changed;

    /// <summary>Canvas pixels or onion/tile settings changed and must be presented again.</summary>
    public event Action? CanvasInvalidated;

    /// <summary>Grid/crosshair/tool changed (the JS overlay needs the new options).</summary>
    public event Action? ViewOptionsChanged;

    public event Action<ViewCommand>? ViewCommandRequested;

    /// <summary>Thumbnail cache key: changes whenever that frame's composite may have changed.</summary>
    public long GetFrameRevision(int frameId) =>
        Math.Max(_globalRevision, _frameRevisions.GetValueOrDefault(frameId));

    // ------------------------------------------------------------------ input

    /// <summary>Handles one pointer event in pixel coordinates (0 = down, 1 = move, 2 = up).</summary>
    public ToolResult HandlePointer(int type, int x, int y)
    {
        if (Dialog != DialogKind.None)
        {
            return ToolResult.None;
        }

        if (type == 0 && IsPlaying)
        {
            StopPlayback();
        }

        _pixelEditFrameId = CurrentFrame.Id;
        ToolResult result;
        try
        {
            result = type switch
            {
                0 => Tools.PointerDown(Document, CurrentFrameIndex, CurrentLayerId, x, y),
                1 => Tools.PointerMove(x, y),
                _ => Tools.PointerUp(),
            };
        }
        finally
        {
            _pixelEditFrameId = null;
        }

        if (result.HasFlag(ToolResult.LayerHidden))
        {
            SetStatus($"Слой «{CurrentLayer.Name}» скрыт — включите видимость, чтобы рисовать.", StatusKind.Warning);
        }

        if (result.HasFlag(ToolResult.ColorPicked))
        {
            SetStatus($"Цвет взят пипеткой: {Tools.PrimaryColor.ToHex()}", StatusKind.Info);
        }

        return result;
    }

    public async Task ExecuteShortcutAsync(string combo)
    {
        if (KeyMap.Resolve(combo) is not { } command)
        {
            return;
        }

        if (Dialog != DialogKind.None)
        {
            if (command == AppCommand.Escape)
            {
                if (Dialog == DialogKind.ConfirmDiscard)
                {
                    await ResolveConfirmAsync(ConfirmChoice.Cancel);
                }
                else
                {
                    CloseDialog();
                }
            }

            return; // Other shortcuts are disabled while a dialog is open.
        }

        await ExecuteAsync(command);
    }

    public async Task ExecuteAsync(AppCommand command)
    {
        switch (command)
        {
            case AppCommand.ToolPencil: SetTool(ToolKind.Pencil); break;
            case AppCommand.ToolEraser: SetTool(ToolKind.Eraser); break;
            case AppCommand.ToolFill: SetTool(ToolKind.Fill); break;
            case AppCommand.ToolEyedropper: SetTool(ToolKind.Eyedropper); break;
            case AppCommand.ToolHand: SetTool(ToolKind.Hand); break;
            case AppCommand.Undo: Undo(); break;
            case AppCommand.Redo: Redo(); break;
            case AppCommand.PlayPause: TogglePlayback(); break;
            case AppCommand.PreviousFrame: SelectFrame(CurrentFrameIndex - 1); break;
            case AppCommand.NextFrame: SelectFrame(CurrentFrameIndex + 1); break;
            case AppCommand.ToggleGrid: SetShowGrid(!ShowGrid); break;
            case AppCommand.ToggleCrosshair: SetShowCrosshair(!ShowCrosshair); break;
            case AppCommand.ToggleOnionSkin: SetOnionEnabled(!Onion.Enabled); break;
            case AppCommand.ToggleTilePreview: SetShowTilePreview(!ShowTilePreview); break;
            case AppCommand.ZoomIn: ViewCommandRequested?.Invoke(ViewCommand.ZoomIn); break;
            case AppCommand.ZoomOut: ViewCommandRequested?.Invoke(ViewCommand.ZoomOut); break;
            case AppCommand.ZoomFit: ViewCommandRequested?.Invoke(ViewCommand.Fit); break;
            case AppCommand.Save: await SaveAsync(); break;
            case AppCommand.Open: await OpenAsync(); break;
            case AppCommand.Export: OpenDialog(DialogKind.Export); break;
        }
    }

    // ------------------------------------------------------------------ tools & view

    public void SetTool(ToolKind tool)
    {
        CancelGesture();
        Tools.Tool = tool;
        ViewOptionsChanged?.Invoke();
        NotifyChanged();
    }

    public void SetPrimaryColor(Color32 color)
    {
        Tools.PrimaryColor = color;
        NotifyChanged();
    }

    public void SetSampleMerged(bool merged)
    {
        Tools.SampleMergedLayers = merged;
        NotifyChanged();
    }

    public void SetShowGrid(bool value)
    {
        ShowGrid = value;
        ViewOptionsChanged?.Invoke();
        NotifyChanged();
    }

    public void SetShowCrosshair(bool value)
    {
        ShowCrosshair = value;
        ViewOptionsChanged?.Invoke();
        NotifyChanged();
    }

    public void SetShowTilePreview(bool value)
    {
        ShowTilePreview = value;
        NotifyChanged();
        CanvasInvalidated?.Invoke();
    }

    public void SetOnionEnabled(bool value)
    {
        Onion.Enabled = value;
        InvalidateCanvasAndUi();
    }

    public void SetOnionOpacity(int value)
    {
        Onion.Opacity = Math.Clamp(value, 0, 100);
        InvalidateCanvasAndUi();
    }

    public void SetOnionNeighbours(bool previous, bool next)
    {
        Onion.ShowPrevious = previous;
        Onion.ShowNext = next;
        InvalidateCanvasAndUi();
    }

    public void RequestView(ViewCommand command) => ViewCommandRequested?.Invoke(command);

    /// <summary>Called by the viewport when the zoom changes (wheel, buttons, fit).</summary>
    public void ReportZoom(int zoom)
    {
        if (Zoom != zoom)
        {
            Zoom = zoom;
            NotifyChanged();
        }
    }

    // ------------------------------------------------------------------ history

    public void Undo()
    {
        CancelGesture();
        if (!Document.Undo())
        {
            SetStatus("Нечего отменять.", StatusKind.Info);
        }
    }

    public void Redo()
    {
        CancelGesture();
        if (!Document.Redo())
        {
            SetStatus("Нечего повторять.", StatusKind.Info);
        }
    }

    // ------------------------------------------------------------------ layers

    public void SelectLayer(int layerId)
    {
        if (Document.IndexOfLayer(layerId) >= 0 && layerId != CurrentLayerId)
        {
            CancelGesture();
            CurrentLayerId = layerId;
            NotifyChanged();
        }
    }

    public void AddLayer() => Try(() =>
    {
        var layer = Document.Layers.AddLayer(index: Document.IndexOfLayer(CurrentLayerId) + 1);
        CurrentLayerId = layer.Id;
    });

    public void DuplicateLayer() => Try(() => CurrentLayerId = Document.Layers.DuplicateLayer(CurrentLayerId).Id);

    public void DeleteLayer() => Try(() =>
    {
        int index = Document.IndexOfLayer(CurrentLayerId);
        Document.Layers.RemoveLayer(CurrentLayerId);
        CurrentLayerId = Document.Layers[Math.Clamp(index - 1, 0, Document.Layers.Count - 1)].Id;
    });

    public void MoveLayer(int delta) => Try(() =>
        Document.Layers.MoveLayer(CurrentLayerId, Document.IndexOfLayer(CurrentLayerId) + delta));

    public void RenameLayer(int layerId, string name) => Try(() => Document.Layers.Rename(layerId, name));

    public void SetLayerVisible(int layerId, bool visible) => Try(() => Document.Layers.SetVisible(layerId, visible));

    public void SetLayerOpacity(int layerId, int opacity) => Try(() => Document.Layers.SetOpacity(layerId, opacity));

    /// <summary>Slider released: the next opacity change starts a new undo step.</summary>
    public void EndContinuousEdit() => Document.History.BreakMerge();

    public void SetLayerBlendMode(int layerId, BlendMode mode) => Try(() => Document.Layers.SetBlendMode(layerId, mode));

    public void MergeDown() => Try(() =>
    {
        var result = Document.Layers.MergeDown(CurrentLayerId);
        CurrentLayerId = result.MergedLayer.Id;
        if (result.VisuallyIdentical)
        {
            SetStatus("Слои объединены, изображение не изменилось.", StatusKind.Success);
        }
        else
        {
            SetStatus(
                $"Слои объединены, но изображение изменилось (до {result.MaxChannelDifference}/255): режим смешивания нельзя выразить одним слоем. Ctrl+Z — отменить.",
                StatusKind.Warning);
        }
    });

    // ------------------------------------------------------------------ frames & playback

    public void SelectFrame(int index)
    {
        if (Document.Frames.Count == 0)
        {
            return;
        }

        index = Math.Clamp(index, 0, Document.Frames.Count - 1);
        if (IsPlaying)
        {
            StopPlayback();
        }

        if (index != CurrentFrameIndex)
        {
            CancelGesture();
            CurrentFrameIndex = index;
            InvalidateCanvasAndUi();
        }
    }

    public void AddFrame() => Try(() =>
    {
        Document.Timeline.AddFrame(CurrentFrameIndex + 1);
        CurrentFrameIndex++;
    });

    public void DuplicateFrame() => Try(() =>
    {
        Document.Timeline.DuplicateFrame(CurrentFrame.Id);
        CurrentFrameIndex++;
    });

    public void DeleteFrame() => Try(() =>
    {
        Document.Timeline.RemoveFrame(CurrentFrame.Id);
        CurrentFrameIndex = Math.Min(CurrentFrameIndex, Document.Frames.Count - 1);
    });

    public void MoveFrame(int delta) => Try(() =>
    {
        int target = Math.Clamp(CurrentFrameIndex + delta, 0, Document.Frames.Count - 1);
        Document.Timeline.MoveFrame(CurrentFrame.Id, target);
        CurrentFrameIndex = target;
    });

    public void SetFrameDuration(int? durationMs) => Try(() => Document.Timeline.SetFrameDuration(CurrentFrame.Id, durationMs));

    public void SetFps(int fps) => Try(() => Document.Timeline.SetFps(fps));

    public void SetLoop(bool loop) => Try(() => Document.Timeline.SetLoop(loop));

    public void TogglePlayback()
    {
        if (IsPlaying)
        {
            StopPlayback();
            return;
        }

        if (Document.Frames.Count < 2)
        {
            SetStatus("Для анимации нужно хотя бы два кадра.", StatusKind.Info);
            return;
        }

        CancelGesture();
        int start = CurrentFrameIndex;
        if (!Document.Timeline.Loop && start == Document.Frames.Count - 1)
        {
            start = 0; // Replaying a non-looping animation from its last frame restarts it.
            CurrentFrameIndex = 0;
        }

        _clock.Start(start);
        _playback = new CancellationTokenSource();
        _ = RunPlaybackAsync(_playback.Token);
        InvalidateCanvasAndUi();
    }

    public void StopPlayback()
    {
        _clock.Pause();
        _playback?.Cancel();
        _playback = null;
        NotifyChanged();
    }

    private async Task RunPlaybackAsync(CancellationToken token)
    {
        var stopwatch = Stopwatch.StartNew();
        double last = 0;
        try
        {
            while (!token.IsCancellationRequested && _clock.IsPlaying)
            {
                var durations = Document.Timeline.GetDurations();
                int delay = (int)Math.Ceiling(_clock.MillisecondsUntilNextFrame(durations));
                await Task.Delay(Math.Clamp(delay, 1, 1000), token);

                double now = stopwatch.Elapsed.TotalMilliseconds;
                bool changed = _clock.Advance(now - last, durations, Document.Timeline.Loop);
                last = now;
                if (changed)
                {
                    CurrentFrameIndex = Math.Min(_clock.FrameIndex, Document.Frames.Count - 1);
                    CanvasInvalidated?.Invoke();
                }

                if (changed || !_clock.IsPlaying)
                {
                    NotifyChanged();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Paused.
        }
    }

    // ------------------------------------------------------------------ palette

    public void AddCurrentColorToPalette() => Try(() =>
    {
        if (Document.Palette.Colors.Count >= DocumentLimits.MaxPaletteColors)
        {
            SetStatus($"В палитре максимум {DocumentLimits.MaxPaletteColors} цветов.", StatusKind.Warning);
            return;
        }

        Document.AddPaletteColor(Tools.PrimaryColor);
    });

    public void RemoveCurrentColorFromPalette() => Try(() =>
    {
        int index = Document.Palette.Colors.ToList().IndexOf(Tools.PrimaryColor);
        if (index >= 0)
        {
            Document.RemovePaletteColorAt(index);
        }
    });

    // ------------------------------------------------------------------ dialogs

    public void OpenDialog(DialogKind dialog)
    {
        if (IsPlaying)
        {
            StopPlayback();
        }

        CancelGesture();
        Dialog = dialog;
        NotifyChanged();
    }

    public void CloseDialog()
    {
        Dialog = DialogKind.None;
        _afterConfirm = null;
        NotifyChanged();
    }

    public void ClearStatus()
    {
        Status = null;
        NotifyChanged();
    }

    // ------------------------------------------------------------------ files

    public Task RequestNewAsync() => GuardUnsavedAsync(() =>
    {
        OpenDialog(DialogKind.NewProject);
        return Task.CompletedTask;
    });

    public void CreateDocument(int width, int height)
    {
        if (!DocumentLimits.IsValidSize(width, height))
        {
            SetStatus($"Размер должен быть от {DocumentLimits.MinSize} до {DocumentLimits.MaxSize} px.", StatusKind.Error);
            return;
        }

        _files.ForgetProjectLocation();
        Dialog = DialogKind.None;
        LoadDocument(SpriteDocument.Create(width, height), UntitledName);
        SetStatus($"Создан холст {width}×{height}.", StatusKind.Success);
    }

    public Task OpenAsync() => GuardUnsavedAsync(async () =>
    {
        var file = await _files.OpenProjectAsync();
        if (file is null)
        {
            return;
        }

        try
        {
            var document = ProjectSerializer.Load(file.Data);
            LoadDocument(document, Path.GetFileNameWithoutExtension(file.Name));
            SetStatus($"Открыт проект «{file.Name}» ({document.Width}×{document.Height}, слоёв: {document.Layers.Count}, кадров: {document.Frames.Count}).", StatusKind.Success);
        }
        catch (ProjectFormatException ex)
        {
            SetStatus($"Не удалось открыть «{file.Name}»: {ex.Message}", StatusKind.Error);
        }
    });

    public async Task<bool> SaveAsync(bool saveAs = false)
    {
        CancelGesture();
        try
        {
            var data = ProjectSerializer.Save(Document);
            var saved = await _files.SaveProjectAsync(FileStem + ProjectSerializer.FileExtension, data, saveAs);
            if (saved is null)
            {
                return false;
            }

            Document.History.MarkSaved();
            DocumentName = Path.GetFileNameWithoutExtension(saved);
            SetStatus(
                SavesToDownloads ? $"Проект «{saved}» скачан (папка загрузок браузера)." : $"Проект сохранён: {saved}",
                StatusKind.Success);
            await UpdateShellAsync();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus($"Не удалось сохранить: {ex.Message}", StatusKind.Error);
            return false;
        }
    }

    /// <summary>Serializes the document for a synchronous save on application close (desktop).</summary>
    public byte[] SerializeDocument() => ProjectSerializer.Save(Document);

    public void MarkSavedExternally(string fileName)
    {
        Document.History.MarkSaved();
        DocumentName = Path.GetFileNameWithoutExtension(fileName);
        NotifyChanged();
    }

    public async Task ExportCurrentFrameAsync()
    {
        try
        {
            var png = Exporter.ExportFramePng(Document, CurrentFrameIndex);
            var saved = await _files.SavePngAsync($"{FileStem}_frame{CurrentFrameIndex + 1}.png", png);
            if (saved is not null)
            {
                Dialog = DialogKind.None;
                SetStatus($"Экспортирован кадр {CurrentFrameIndex + 1}: {saved}", StatusKind.Success);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus($"Ошибка экспорта: {ex.Message}", StatusKind.Error);
        }
    }

    public async Task ExportSpriteSheetAsync(int columns, int spacing, string baseName)
    {
        try
        {
            // Validate before any dialog appears.
            SpriteSheetLayout.Create(Document.Frames.Count, Document.Width, Document.Height, columns, spacing);
            var pngName = Exporter.NormalizeImageFileName(string.IsNullOrWhiteSpace(baseName) ? FileStem : baseName);
            var saved = await _files.SaveSpriteSheetAsync(
                pngName,
                finalName => Exporter.ExportSpriteSheet(Document, new SpriteSheetOptions(columns, spacing, finalName)));
            if (saved is not null)
            {
                Dialog = DialogKind.None;
                SetStatus($"Экспортированы {saved} и {Path.ChangeExtension(saved, ".json")} ({Document.Frames.Count} кадр.).", StatusKind.Success);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            SetStatus($"Ошибка экспорта: {ex.Message}", StatusKind.Error);
        }
    }

    public async Task ResolveConfirmAsync(ConfirmChoice choice)
    {
        var action = _afterConfirm;
        _afterConfirm = null;
        Dialog = DialogKind.None;
        NotifyChanged();

        if (choice == ConfirmChoice.Cancel || action is null)
        {
            return;
        }

        if (choice == ConfirmChoice.Save && !await SaveAsync())
        {
            return;
        }

        await action();
    }

    private async Task GuardUnsavedAsync(Func<Task> action)
    {
        if (!IsDirty)
        {
            await action();
            return;
        }

        OpenDialog(DialogKind.ConfirmDiscard);
        _afterConfirm = action;
    }

    // ------------------------------------------------------------------ internals

    private void LoadDocument(SpriteDocument document, string name)
    {
        StopPlaybackSilently();
        Tools.CancelGesture();
        if (Document is not null)
        {
            Document.Changed -= OnDocumentChanged;
        }

        Document = document;
        Document.Changed += OnDocumentChanged;
        DocumentName = string.IsNullOrWhiteSpace(name) ? UntitledName : name;
        CurrentFrameIndex = 0;
        CurrentLayerId = document.Layers[^1].Id;
        _frameRevisions.Clear();
        _globalRevision = ++_revisionCounter;
        DocumentGeneration++;
        _ = UpdateShellAsync();
        InvalidateCanvasAndUi();
    }

    private void OnDocumentChanged()
    {
        // Keep the selection valid after undo/redo/structural changes.
        if (Document.IndexOfLayer(CurrentLayerId) < 0)
        {
            CurrentLayerId = Document.Layers[^1].Id;
        }

        CurrentFrameIndex = Math.Clamp(CurrentFrameIndex, 0, Document.Frames.Count - 1);

        _ = UpdateShellAsync();
        if (_pixelEditFrameId is int frameId)
        {
            // A tool gesture was committed: only this frame's thumbnail changed, and the stage
            // already presented the changed region itself while handling the pointer batch.
            _frameRevisions[frameId] = ++_revisionCounter;
            NotifyChanged();
            return;
        }

        _globalRevision = ++_revisionCounter;
        InvalidateCanvasAndUi();
    }

    /// <summary>Aborts an unfinished drawing gesture; its pixels are restored, so the canvas is repainted.</summary>
    private void CancelGesture()
    {
        if (Tools.IsGestureActive)
        {
            Tools.CancelGesture();
            CanvasInvalidated?.Invoke();
        }
    }

    private void StopPlaybackSilently()
    {
        _clock.Pause();
        _playback?.Cancel();
        _playback = null;
    }

    /// <summary>Runs a document operation; selection may change after the document event, so refresh afterwards.</summary>
    private void Try(Action action)
    {
        try
        {
            action();
            InvalidateCanvasAndUi();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            SetStatus(ex.Message, StatusKind.Warning);
        }
    }

    private void SetStatus(string message, StatusKind kind)
    {
        Status = message;
        StatusKind = kind;
        NotifyChanged();
    }

    private Task UpdateShellAsync() => _shell.UpdateDocumentStateAsync(WindowTitle, IsDirty);

    private void InvalidateCanvasAndUi()
    {
        CanvasInvalidated?.Invoke();
        NotifyChanged();
    }

    private void NotifyChanged() => Changed?.Invoke();
}
