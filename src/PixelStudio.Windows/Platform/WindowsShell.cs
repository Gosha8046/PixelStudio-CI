using PixelStudio.UI.Platform;

namespace PixelStudio.Windows.Platform;

/// <summary>Shows the document name and unsaved state in the window caption.</summary>
internal sealed class WindowsShell : IPlatformShell
{
    private readonly Form _form;

    public WindowsShell(Form form)
    {
        _form = form;
    }

    public string PlatformName => "Windows (WebView2)";

    public Task UpdateDocumentStateAsync(string title, bool isDirty)
    {
        if (_form.IsHandleCreated && _form.InvokeRequired)
        {
            _form.BeginInvoke(() => _form.Text = title);
        }
        else
        {
            _form.Text = title;
        }

        return Task.CompletedTask;
    }
}
