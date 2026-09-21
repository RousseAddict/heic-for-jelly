using System;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.HeicForJelly;

/// <summary>
/// Plugin entry point. Jellyfin instantiates this once at startup, through
/// <c>PluginManager</c>, and shows it in the dashboard's plugin list.
/// </summary>
public class Plugin : BasePlugin<BasePluginConfiguration>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Supplied by Jellyfin; locates the per-plugin configuration directory.</param>
    /// <param name="xmlSerializer">Supplied by Jellyfin; reads and writes that configuration.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the single live instance, assigned by the constructor.
    /// </summary>
    /// <remarks>
    /// Null until Jellyfin has constructed the plugin. Components resolved through DI
    /// may run before that happens, so callers must not assume it is set.
    /// </remarks>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "HEIC for Jelly";

    /// <summary>
    /// Gets the plugin's stable identity.
    /// </summary>
    /// <remarks>
    /// Generated once and never to be changed: Jellyfin keys the installed-plugin
    /// record and its configuration directory on this GUID, so altering it would
    /// orphan both and present the plugin as a different, uninstalled one.
    /// </remarks>
    public override Guid Id => Guid.Parse("e0ca1e89-983f-495a-a3ce-7dba998cb21e");

    /// <inheritdoc />
    public override string Description =>
        "Makes HEIC/HEIF photos visible and renderable in a Jellyfin photo library.";
}
