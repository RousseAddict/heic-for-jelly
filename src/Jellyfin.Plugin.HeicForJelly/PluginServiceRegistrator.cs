using System;
using System.Linq;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.HeicForJelly;

/// <summary>
/// Replaces the registered <see cref="IImageEncoder"/> with one that wraps it.
/// </summary>
/// <remarks>
/// The server registers its encoder in <c>CoreAppHost.RegisterServices</c> and only afterwards
/// calls <c>PluginManager.RegisterServices</c>, so by the time this runs the original
/// registration is already present and can be taken apart. That ordering is the whole reason a
/// plugin is able to decorate a core service at all.
/// </remarks>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <summary>
    /// Registers the decorated image encoder.
    /// </summary>
    /// <param name="serviceCollection">The service collection.</param>
    /// <param name="applicationHost">The server application host.</param>
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        ArgumentNullException.ThrowIfNull(serviceCollection);

        // Last, not First: the container resolves the last registration of a service type, so
        // the last one is the one that would have been used had this plugin not loaded.
        var original = serviceCollection.LastOrDefault(d => d.ServiceType == typeof(IImageEncoder));

        if (original?.ImplementationType is null)
        {
            // The server has always registered this by implementation type. If that ever stops
            // being true, decorating blind would silently disable image encoding for the whole
            // server, so leave the registration alone and let the plugin do nothing instead.
            return;
        }

        // Remove rather than merely shadow it. Both descriptors would otherwise be live, and
        // anything resolving IEnumerable<IImageEncoder> would get the undecorated one too.
        serviceCollection.Remove(original);

        var innerType = original.ImplementationType;

        serviceCollection.AddSingleton<IImageEncoder>(provider => new HeicImageEncoder(
            (IImageEncoder)ActivatorUtilities.CreateInstance(provider, innerType),
            provider.GetRequiredService<IMediaEncoder>(),
            provider.GetRequiredService<ILogger<HeicImageEncoder>>()));
    }
}
