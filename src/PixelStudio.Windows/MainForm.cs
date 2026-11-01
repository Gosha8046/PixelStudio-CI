using Microsoft.AspNetCore.Components.WebView.WindowsForms;
using Microsoft.Extensions.DependencyInjection;
using PixelStudio.UI;
using PixelStudio.UI.Platform;
using PixelStudio.UI.State;
using PixelStudio.Windows.Platform;

namespace PixelStudio.Windows;

/// <summary>Main window: hosts the shared Razor UI in a BlazorWebView and guards closing with unsaved changes.</summary>
internal sealed class MainForm : Form
{
    private readonly ServiceProvider _services;
    private readonly WindowsFileService _files;
    private bool _closeConfirmed;

    public MainForm()
    {
        Text = "PixelStudio CI";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        MinimumSize = new Size(1000, 640);
        Size = new Size(1440, 900);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(23, 24, 28);

        // WebView2 keeps its profile in a user-writable folder, so the app also works when
        // unpacked into a read-only location such as Program Files.
        Environment.SetEnvironmentVariable(
            "WEBVIEW2_USER_DATA_FOLDER",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PixelStudioCI", "WebView2"));

        _files = new WindowsFileService(this);
        var services = new ServiceCollection();
        services.AddWindowsFormsBlazorWebView();
#if DEBUG
        services.AddBlazorWebViewDeveloperTools();
#endif
        services.AddSingleton<IFileService>(_files);
        services.AddSingleton<IPlatformShell>(new WindowsShell(this));
        services.AddPixelStudioEditor();
        _services = services.BuildServiceProvider();

        var webView = new BlazorWebView
        {
            Dock = DockStyle.Fill,
            HostPage = "wwwroot\\index.html",
            Services = _services,
        };
        webView.RootComponents.Add<App>("#app");
        webView.BlazorWebViewInitialized += (_, e) =>
        {
            // Disable browser accelerators (F5 reload, Ctrl+P, Ctrl+F...) so they cannot discard work;
            // the editor's own shortcuts are handled in JavaScript and are unaffected.
            e.WebView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
            e.WebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
        };
        Controls.Add(webView);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (_closeConfirmed || e.CloseReason == CloseReason.WindowsShutDown)
        {
            return;
        }

        var state = _services.GetRequiredService<EditorState>();
        if (!state.IsDirty)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"Сохранить изменения в «{state.DocumentName}» перед выходом?",
            "PixelStudio CI",
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Question);

        switch (answer)
        {
            case DialogResult.Yes:
                var saved = _files.SaveProjectSync(state.FileStem + PixelStudio.Core.IO.ProjectSerializer.FileExtension, state.SerializeDocument(), saveAs: false);
                if (saved is null)
                {
                    e.Cancel = true; // Save dialog cancelled: stay open.
                    return;
                }

                state.MarkSavedExternally(saved);
                break;
            case DialogResult.No:
                break;
            default:
                e.Cancel = true;
                return;
        }

        _closeConfirmed = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _services.Dispose();
        }

        base.Dispose(disposing);
    }
}
