using System.Diagnostics;
using Microsoft.Web.WebView2.Core;

namespace PixelStudio.Windows;

/// <summary>
/// The UI is rendered by the Microsoft Edge WebView2 Runtime (preinstalled on Windows 11 and
/// on up-to-date Windows 10). If it is missing, explain how to get it instead of crashing.
/// </summary>
internal static class WebView2Check
{
    private const string DownloadUrl = "https://developer.microsoft.com/microsoft-edge/webview2/";

    public static bool EnsureRuntimeAvailable()
    {
        try
        {
            _ = CoreWebView2Environment.GetAvailableBrowserVersionString();
            return true;
        }
        catch (WebView2RuntimeNotFoundException)
        {
            var answer = MessageBox.Show(
                "Для работы PixelStudio CI нужен Microsoft Edge WebView2 Runtime.\n\n" +
                "Открыть страницу загрузки (выберите «Evergreen Bootstrapper»)?",
                "PixelStudio CI",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (answer == DialogResult.Yes)
            {
                Process.Start(new ProcessStartInfo(DownloadUrl) { UseShellExecute = true });
            }

            return false;
        }
    }
}
