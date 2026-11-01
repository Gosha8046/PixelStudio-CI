using Microsoft.Extensions.DependencyInjection;
using PixelStudio.UI.Interop;
using PixelStudio.UI.State;

namespace PixelStudio.UI;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the editor. The host must also register <see cref="Platform.IFileService"/>
    /// and <see cref="Platform.IPlatformShell"/>.
    /// </summary>
    public static IServiceCollection AddPixelStudioEditor(this IServiceCollection services)
    {
        services.AddSingleton<EditorState>();
        services.AddScoped<EditorInterop>();
        return services;
    }
}
