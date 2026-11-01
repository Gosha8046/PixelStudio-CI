namespace PixelStudio.Windows;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        if (!WebView2Check.EnsureRuntimeAvailable())
        {
            return;
        }

        Application.Run(new MainForm());
    }
}
