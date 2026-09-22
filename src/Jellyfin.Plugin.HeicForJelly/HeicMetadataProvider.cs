using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.HeicForJelly;

/// <summary>
/// Fills in the EXIF half of a HEIC photo, which the server's own <c>PhotoProvider</c> skips.
/// </summary>
/// <remarks>
/// <para>
/// <c>PhotoProvider</c> gates its whole EXIF block on a hardcoded extension list —
/// <c>.jpg .jpeg .png .tiff .cr2 .webp .avif</c> — because the comment above it says anything else
/// "might cause taglib to hang". HEIC is not on it, so a HEIC photo reaches the library with no
/// date, no camera and no coordinates. The date is the one that matters: jellypic sorts its entire
/// timeline on <c>PremiereDate</c>, so without this every HEIC lands in the undated bucket.
/// </para>
/// <para>
/// The primary image and the dimensions are not set here. <c>PhotoProvider</c> already does both
/// before its extension check, and the dimensions come from the encoder's probe.
/// </para>
/// </remarks>
public class HeicMetadataProvider : ICustomMetadataProvider<Photo>, IForcedProvider, IHasItemChangeMonitor
{
    private readonly ILogger<HeicMetadataProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="HeicMetadataProvider"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public HeicMetadataProvider(ILogger<HeicMetadataProvider> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets the name the dashboard shows against a refresh.
    /// </summary>
    /// <remarks>
    /// Distinct from <c>PhotoProvider</c>'s "Embedded Information" on purpose: both run for every
    /// photo item, and a log line naming one of them should say which.
    /// </remarks>
    public string Name => "HEIC Embedded Information";

    /// <summary>
    /// Reports whether the file changed since the last refresh.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="directoryService">The per-scan directory cache.</param>
    /// <returns><c>true</c> when the file is newer than what was recorded.</returns>
    /// <remarks>
    /// Copied in behaviour from <c>PhotoProvider.HasChanged</c>. Without it, a photo whose EXIF was
    /// corrected in place would keep the metadata read the first time round.
    /// </remarks>
    public bool HasChanged(BaseItem item, IDirectoryService directoryService)
    {
        if (item.IsFileProtocol)
        {
            var file = directoryService.GetFile(item.Path);
            return file is not null && item.HasChanged(file.LastWriteTimeUtc);
        }

        return false;
    }

    /// <summary>
    /// Reads the file's EXIF onto the item.
    /// </summary>
    /// <param name="item">The photo.</param>
    /// <param name="options">The refresh options, unused.</param>
    /// <param name="cancellationToken">The cancellation token, unused: the read is one bounded
    /// buffer and a parse over it, with no I/O to interrupt part-way.</param>
    /// <returns>What changed, for the refresh machinery to act on.</returns>
    public Task<ItemUpdateType> FetchAsync(Photo item, MetadataRefreshOptions options, CancellationToken cancellationToken)
    {
        if (item.Path is null || !HeicContainer.HasHeicExtension(item.Path))
        {
            // Every other photo format is PhotoProvider's, and it does a better job of them.
            return Task.FromResult(ItemUpdateType.None);
        }

        var exif = HeicContainer.ReadExif(item.Path);

        if (exif is null)
        {
            // Either not HEIF behind the extension — this library had 1 386 JPEGs named .HEIC — or
            // the EXIF sits past the head that was read. Neither is worth an error; the photo is
            // still perfectly viewable, it just sorts by file date.
            _logger.LogDebug("No EXIF found in {Path}; leaving its metadata alone.", item.Path);
            return Task.FromResult(ItemUpdateType.None);
        }

        if (exif.DateTaken is { } dateTaken)
        {
            // Exactly what PhotoProvider does, and the asymmetry is deliberate on its part: EXIF
            // carries no timezone, so PremiereDate keeps the camera's wall-clock reading untouched
            // while DateCreated accepts the server's zone. Converting PremiereDate too would move
            // every photo by the container's TZ offset.
            item.DateCreated = dateTaken.ToUniversalTime();
            item.PremiereDate = dateTaken;
            item.ProductionYear = dateTaken.Year;
        }

        item.CameraMake = exif.CameraMake;
        item.CameraModel = exif.CameraModel;
        item.Software = exif.Software;
        item.ExposureTime = exif.ExposureTime;
        item.FocalLength = exif.FocalLength;
        item.Aperture = exif.Aperture;
        item.ShutterSpeed = exif.ShutterSpeed;
        item.IsoSpeedRating = exif.IsoSpeedRating;
        item.Latitude = exif.Latitude;
        item.Longitude = exif.Longitude;
        item.Altitude = exif.Altitude;

        // item.Orientation is left null, and that is not an omission.
        //
        // The rotation is already baked into the pixels the decoder produces, and the width and
        // height stored for the item are the rotated ones. Photo.GetDefaultPrimaryImageAspectRatio
        // swaps width and height whenever Orientation is one of the four quarter-turn values, so
        // recording the file's real orientation here would swap a pair that is already correct and
        // give every portrait photo a landscape aspect ratio.
        //
        // Setting TopLeft instead would be worse: ImageProcessor.ProcessImage treats a null
        // orientation as "unknown, so auto-orient anyway" and TopLeft as "nothing to do", and that
        // second branch lets it return the original file without ever calling EncodeImage — which
        // would hand the client raw HEIC.
        return Task.FromResult(ItemUpdateType.MetadataImport);
    }
}
