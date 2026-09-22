using System;
using System.IO;
using Emby.Naming.Common;
using Emby.Naming.Video;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Resolvers;

namespace Jellyfin.Plugin.HeicForJelly;

/// <summary>
/// Turns a HEIC/HEIF file into a <see cref="Photo"/> item, which the server's own
/// <c>PhotoResolver</c> will not do.
/// </summary>
/// <remarks>
/// <para>
/// The server's resolver gates on <c>IImageProcessor.SupportedInputFormats</c>, a hardcoded
/// list that contains no <c>heic</c> and no <c>heif</c>. It therefore returns <c>null</c> and
/// <em>no item is created at all</em> — the photos are missing from the library entirely
/// rather than showing up with a broken thumbnail. Adding a resolver of our own is the
/// supported way past that, because <c>ApplicationHost.GetExports&lt;IItemResolver&gt;()</c>
/// picks up plugin assemblies and no format list has to be patched.
/// </para>
/// <para>
/// Resolving a file here says only "this is a photo". Whether its pixels can be read is a
/// separate question answered by the image encoder, so until that half exists these items
/// will appear with no thumbnail.
/// </para>
/// </remarks>
public class HeicPhotoResolver : ItemResolver<Photo>
{
    /// <summary>
    /// Filename prefixes that mean "artwork", not "photo". Copied in behaviour, not in code,
    /// from the server's <c>PhotoResolver._ignoreFiles</c>: a folder holding <c>cover.heic</c>
    /// wants that file used as its image, not indexed as a photo of its own.
    /// </summary>
    private static readonly string[] IgnoredFilenamePrefixes =
    {
        "folder",
        "thumb",
        "landscape",
        "fanart",
        "backdrop",
        "poster",
        "cover",
        "logo",
        "default",
    };

    private readonly NamingOptions _namingOptions;
    private readonly IDirectoryService _directoryService;

    /// <summary>
    /// Initializes a new instance of the <see cref="HeicPhotoResolver"/> class.
    /// </summary>
    /// <param name="namingOptions">Naming options, used to recognise sibling video files.</param>
    /// <param name="directoryService">Directory service, whose per-scan cache is why sibling
    /// lookups do not cost a stat call per photo.</param>
    public HeicPhotoResolver(NamingOptions namingOptions, IDirectoryService directoryService)
    {
        _namingOptions = namingOptions;
        _directoryService = directoryService;
    }

    /// <summary>
    /// Gets the resolver priority.
    /// </summary>
    /// <remarks>
    /// <see cref="ResolverPriority.Plugin"/> is the slot the server reserves for plugins that
    /// need to run ahead of its own resolvers. Nothing else claims HEIC, so the ordering is not
    /// a fight over the file — but it does mean this method runs for <em>every file in every
    /// library</em>, so <see cref="Resolve"/> tests the extension before anything else.
    /// </remarks>
    public override ResolverPriority Priority => ResolverPriority.Plugin;

    /// <summary>
    /// Resolves a HEIC or HEIF path into a photo item.
    /// </summary>
    /// <param name="args">The resolve arguments.</param>
    /// <returns>A <see cref="Photo"/>, or <c>null</c> if this file is not ours to claim.</returns>
    protected override Photo? Resolve(ItemResolveArgs args)
    {
        // Cheapest possible rejection first: this runs once per file for the whole server.
        if (args.IsDirectory || !HasHeicExtension(args.Path))
        {
            return null;
        }

        // A HEIC sitting in a music or movie library is cover art or a stray file, not a photo.
        var collectionType = args.CollectionType;
        if (collectionType != CollectionType.photos
            && !(collectionType == CollectionType.homevideos && args.LibraryOptions.EnablePhotos))
        {
            return null;
        }

        var filename = Path.GetFileNameWithoutExtension(args.Path);

        foreach (var prefix in IgnoredFilenamePrefixes)
        {
            if (filename.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        // An image whose name prefixes a sibling video belongs to that video: MOV_1234.heic
        // beside MOV_1234.mov is that clip's thumbnail. Indexing it would duplicate the clip
        // as a still. This is the one expensive check, hence its position last.
        var directory = Path.GetDirectoryName(args.Path);
        if (directory is not null)
        {
            foreach (var file in _directoryService.GetFiles(directory))
            {
                if (VideoResolver.IsVideoFile(file.FullName, _namingOptions)
                    && filename.StartsWith(
                        Path.GetFileNameWithoutExtension(file.FullName),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
            }
        }

        return new Photo
        {
            Path = args.Path,
        };
    }

    /// <summary>
    /// Reports whether a path carries a HEIC or HEIF extension.
    /// </summary>
    /// <remarks>
    /// Extension only — the bytes are not sniffed, even though this library is known to contain
    /// JPEGs wearing a <c>.HEIC</c> extension. Sniffing would mean opening every candidate during
    /// a scan, and it would not help: a mislabelled file resolves either way, since the server
    /// ignores <c>.heic</c> wholesale. Getting the extension right is the rename script's job.
    /// What the decoder must not do is trust this classification — it has to check the magic
    /// bytes itself before assuming it was handed HEIF.
    /// </remarks>
    /// <param name="path">The file path.</param>
    /// <returns><c>true</c> when the extension is <c>.heic</c> or <c>.heif</c>.</returns>
    private static bool HasHeicExtension(string path)
    {
        var extension = Path.GetExtension(path.AsSpan());
        return extension.Equals(".heic", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".heif", StringComparison.OrdinalIgnoreCase);
    }
}
