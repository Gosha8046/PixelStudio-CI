using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using PixelStudio.UI;
using PixelStudio.UI.Platform;
using PixelStudio.Web.Platform;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");

builder.Services.AddSingleton<IFileService, BrowserFileService>();
builder.Services.AddSingleton<IPlatformShell, BrowserShell>();
builder.Services.AddPixelStudioEditor();

await builder.Build().RunAsync();
