using Microsoft.JSInterop;
using PixelStudio.Core.Diagnostics;

namespace PixelStudio.Web;

/// <summary>
/// Runs the core performance probe inside the browser (.NET on WebAssembly) so README numbers
/// cover the real web runtime. Invoked from the browser console or the E2E script:
/// <c>await DotNet.invokeMethodAsync('PixelStudio.Web', 'RunPerfProbe')</c>.
/// </summary>
public static class Diagnostics
{
    [JSInvokable]
    public static string RunPerfProbe() =>
        $".NET {Environment.Version} on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}\n" +
        PerfProbe.FormatTable(PerfProbe.RunAll());
}
