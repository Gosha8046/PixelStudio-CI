using System.Text;
using System.Text.Json;
using PixelStudio.Core.Editing;
using PixelStudio.Core.IO;
using PixelStudio.Tests.Support;
using PixelStudio.UI.Platform;
using PixelStudio.UI.State;

namespace PixelStudio.Tests;

/// <summary>Editor session logic (UI layer, platform-independent) with fake platform services.</summary>
public class EditorStateTests
{
    private sealed class FakeFiles : IFileService
    {
        public OpenedFile? NextOpen { get; set; }
        public Dictionary<string, byte[]> Saved { get; } = [];
        public string? ChosenPngName { get; set; }

        public bool SavesToDownloads => false;

        public Task<OpenedFile?> OpenProjectAsync() => Task.FromResult(NextOpen);

        public Task<string?> SaveProjectAsync(string suggestedFileName, byte[] data, bool saveAs)
        {
            Saved[suggestedFileName] = data;
            return Task.FromResult<string?>(suggestedFileName);
        }

        public Task<string?> SavePngAsync(string suggestedFileName, byte[] data)
        {
            Saved[suggestedFileName] = data;
            return Task.FromResult<string?>(suggestedFileName);
        }

        public Task<string?> SaveSpriteSheetAsync(string suggestedPngFileName, Func<string, SpriteSheetExport> build)
        {
            var name = ChosenPngName ?? suggestedPngFileName; // e.g. the user renamed it in a Save dialog
            var export = build(name);
            Saved[name] = export.Png;
            Saved[Path.ChangeExtension(name, ".json")] = Encoding.UTF8.GetBytes(export.Json);
            return Task.FromResult<string?>(name);
        }

        public void ForgetProjectLocation()
        {
        }
    }

    private sealed class FakeShell : IPlatformShell
    {
        public string LastTitle { get; private set; } = "";
        public bool LastDirty { get; private set; }
        public string PlatformName => "test";

        public Task UpdateDocumentStateAsync(string title, bool isDirty)
        {
            LastTitle = title;
            LastDirty = isDirty;
            return Task.CompletedTask;
        }
    }

    private static (EditorState State, FakeFiles Files, FakeShell Shell) Create()
    {
        var files = new FakeFiles();
        var shell = new FakeShell();
        return (new EditorState(files, shell), files, shell);
    }

    [Fact]
    public void KeyMap_ResolvesLayoutIndependentCodes()
    {
        Assert.Equal(AppCommand.ToolPencil, KeyMap.Resolve("KeyB"));
        Assert.Equal(AppCommand.Undo, KeyMap.Resolve("Ctrl+KeyZ"));
        Assert.Equal(AppCommand.Redo, KeyMap.Resolve("Ctrl+Shift+KeyZ"));
        Assert.Equal(AppCommand.Redo, KeyMap.Resolve("Ctrl+KeyY"));
        Assert.Null(KeyMap.Resolve("KeyQ"));
        Assert.Equal("Ctrl+Z", KeyMap.Hint(AppCommand.Undo));
        Assert.Equal(",", KeyMap.Hint(AppCommand.PreviousFrame));
        Assert.All(KeyMap.AllCombos, combo => Assert.NotNull(KeyMap.Resolve(combo)));
    }

    [Fact]
    public async Task Shortcuts_AreIgnoredWhileADialogIsOpen_ExceptEscape()
    {
        var (state, _, _) = Create();
        state.OpenDialog(DialogKind.Export);

        await state.ExecuteShortcutAsync("KeyE");
        Assert.Equal(ToolKind.Pencil, state.Tools.Tool);

        await state.ExecuteShortcutAsync("Escape");
        Assert.Equal(DialogKind.None, state.Dialog);
        await state.ExecuteShortcutAsync("KeyE");
        Assert.Equal(ToolKind.Eraser, state.Tools.Tool);
    }

    [Fact]
    public void Selection_StaysValidAfterUndoOfANewLayer()
    {
        var (state, _, _) = Create();
        state.AddLayer();
        int added = state.CurrentLayerId;

        state.Undo();

        Assert.NotEqual(added, state.CurrentLayerId);
        Assert.True(state.Document.IndexOfLayer(state.CurrentLayerId) >= 0);
    }

    [Fact]
    public async Task UnsavedChanges_AskBeforeReplacingTheDocument()
    {
        var (state, files, shell) = Create();
        state.HandlePointer(0, 1, 1);
        state.HandlePointer(2, 1, 1);
        Assert.True(state.IsDirty);
        Assert.True(shell.LastDirty);
        Assert.StartsWith("• ", shell.LastTitle);

        await state.RequestNewAsync();
        Assert.Equal(DialogKind.ConfirmDiscard, state.Dialog);
        await state.ResolveConfirmAsync(ConfirmChoice.Cancel);
        Assert.Equal(DialogKind.None, state.Dialog);
        Assert.True(state.IsDirty);

        await state.RequestNewAsync();
        await state.ResolveConfirmAsync(ConfirmChoice.Save);
        Assert.True(files.Saved.ContainsKey("sprite.pxstudio"));
        Assert.False(state.IsDirty);
        Assert.Equal(DialogKind.NewProject, state.Dialog);

        state.CreateDocument(64, 16);
        Assert.Equal((64, 16), (state.Document.Width, state.Document.Height));
        Assert.False(state.IsDirty);
    }

    [Fact]
    public async Task OpeningACorruptedFile_KeepsTheCurrentDocument()
    {
        var (state, files, _) = Create();
        var before = state.Document;
        files.NextOpen = new OpenedFile("broken.pxstudio", Encoding.UTF8.GetBytes("{ not json"));

        await state.OpenAsync();

        Assert.Same(before, state.Document);
        Assert.Equal(StatusKind.Error, state.StatusKind);
        Assert.Contains("Не удалось открыть «broken.pxstudio»", state.Status);
    }

    [Fact]
    public async Task SaveThenOpen_RestoresTheDocument()
    {
        var (state, files, _) = Create();
        state.AddLayer();
        state.SetLayerBlendMode(state.CurrentLayerId, BlendMode.Screen);
        state.DuplicateFrame();
        state.HandlePointer(0, 3, 4);
        state.HandlePointer(1, 9, 4);
        state.HandlePointer(2, 9, 4);
        var expected = TestDocs.Describe(state.Document);
        await state.SaveAsync();

        var (reopened, reopenedFiles, _) = Create();
        reopenedFiles.NextOpen = new OpenedFile("sprite.pxstudio", files.Saved["sprite.pxstudio"]);
        await reopened.OpenAsync();

        Assert.Equal(expected, TestDocs.Describe(reopened.Document));
        Assert.Equal("sprite", reopened.DocumentName);
        Assert.False(reopened.IsDirty);
    }

    [Fact]
    public async Task SpriteSheetJson_ReferencesTheFileNameActuallyChosen()
    {
        var (state, files, _) = Create();
        state.AddFrame();
        files.ChosenPngName = "walk_cycle.png";

        await state.ExportSpriteSheetAsync(columns: 2, spacing: 1, baseName: "hero");

        using var json = JsonDocument.Parse(files.Saved["walk_cycle.json"]);
        Assert.Equal("walk_cycle.png", json.RootElement.GetProperty("image").GetString());
        Assert.Equal(StatusKind.Success, state.StatusKind);
    }

    [Fact]
    public void DrawingOnAHiddenLayer_ShowsAWarning()
    {
        var (state, _, _) = Create();
        state.SetLayerVisible(state.CurrentLayerId, false);

        var result = state.HandlePointer(0, 2, 2);

        Assert.True(result.HasFlag(ToolResult.LayerHidden));
        Assert.Equal(StatusKind.Warning, state.StatusKind);
        Assert.False(state.Document.History.CanUndo && state.Document.History.UndoLabel == "Карандаш");
    }
}
