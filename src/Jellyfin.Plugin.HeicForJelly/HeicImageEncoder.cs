using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Drawing;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.HeicForJelly;

/// <summary>
/// Wraps the server's own image encoder and takes over the HEIC/HEIF files it cannot read,
/// leaving every other format to it untouched.
/// </summary>
/// <remarks>
/// <para>
/// Decorating <see cref="IImageEncoder"/> is the whole design, not an implementation detail.
/// Sitting inside Jellyfin's image pipeline means the resized-image cache, the encoding
/// concurrency limit, ETags, conditional GETs, authentication and per-library permissions all
/// keep applying without this plugin reimplementing any of them. The alternative — exposing an
/// HTTP endpoint that converts a file — is what made the prior art an unauthenticated read of
/// any file on the host. See <c>docs/03-prior-art.md</c>.
/// </para>
/// <para>
/// Nothing here decodes yet. This is the seam and the capability check; the ffmpeg tile-grid
/// decode lands next. Until then every call is delegated, so behaviour is unchanged.
/// </para>
/// </remarks>
public class HeicImageEncoder : IImageEncoder
{
    /// <summary>
    /// The oldest ffmpeg that can demux HEIF still images at all. FFmpeg added them in 7.0.
    /// </summary>
    /// <remarks>
    /// This has to be checked, because Jellyfin will not check it for us:
    /// <c>EncoderValidator.MinVersion</c> is <b>4.4</b>, so a server can be perfectly healthy
    /// while carrying an ffmpeg three years too old for this plugin. Failing loudly once at
    /// startup beats producing thumbnails that are mysteriously blank.
    /// </remarks>
    private static readonly Version MinimumFfmpegVersion = new Version(7, 0);

    private readonly IImageEncoder _inner;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<HeicImageEncoder> _logger;
    private readonly Lazy<bool> _canDecodeHeic;

    /// <summary>
    /// Initializes a new instance of the <see cref="HeicImageEncoder"/> class.
    /// </summary>
    /// <param name="inner">The encoder being decorated — Skia on a healthy server, or the null
    /// encoder when Skia's native library is missing.</param>
    /// <param name="mediaEncoder">Used for ffmpeg's path and version, so that the plugin uses
    /// the same binary the server does rather than guessing at one.</param>
    /// <param name="logger">The logger.</param>
    public HeicImageEncoder(IImageEncoder inner, IMediaEncoder mediaEncoder, ILogger<HeicImageEncoder> logger)
    {
        _inner = inner;
        _mediaEncoder = mediaEncoder;
        _logger = logger;

        // Deliberately lazy. MediaEncoder.EncoderVersion is null until the server validates
        // ffmpeg during startup, and this encoder may well be constructed before that happens.
        _canDecodeHeic = new Lazy<bool>(CheckFfmpeg);
    }

    /// <summary>
    /// Gets the formats this encoder can read.
    /// </summary>
    /// <remarks>
    /// Adding heic and heif here is honest bookkeeping rather than the mechanism that makes them
    /// work: nothing in the server actually reads this property. <c>ImageProcessor</c> has its
    /// own hardcoded list and never consults the encoder's, which is exactly why a resolver was
    /// needed as well. Verified in <c>ImageProcessor.cs:SupportedInputFormats</c> at v10.11.6.
    /// </remarks>
    public IReadOnlyCollection<string> SupportedInputFormats =>
        _canDecodeHeic.Value
            ? _inner.SupportedInputFormats.Concat(new[] { "heic", "heif" }).ToArray()
            : _inner.SupportedInputFormats;

    /// <inheritdoc />
    public IReadOnlyCollection<ImageFormat> SupportedOutputFormats => _inner.SupportedOutputFormats;

    /// <summary>
    /// Gets the display name, which the server logs at startup.
    /// </summary>
    /// <remarks>
    /// Naming the wrapped encoder too makes the decoration visible in the log, so that a server
    /// running this plugin cannot be mistaken for a stock one when reading a bug report.
    /// </remarks>
    public string Name => "HeicForJelly over " + _inner.Name;

    /// <inheritdoc />
    public bool SupportsImageCollageCreation => _inner.SupportsImageCollageCreation;

    /// <inheritdoc />
    public bool SupportsImageEncoding => _inner.SupportsImageEncoding;

    /// <inheritdoc />
    public ImageDimensions GetImageSize(string path)
    {
        return _inner.GetImageSize(path);
    }

    /// <inheritdoc />
    public string GetImageBlurHash(int xComp, int yComp, string path)
    {
        return _inner.GetImageBlurHash(xComp, yComp, path);
    }

    /// <inheritdoc />
    public string EncodeImage(string inputPath, DateTime dateModified, string outputPath, bool autoOrient, ImageOrientation? orientation, int quality, ImageProcessingOptions options, ImageFormat outputFormat)
    {
        return _inner.EncodeImage(inputPath, dateModified, outputPath, autoOrient, orientation, quality, options, outputFormat);
    }

    /// <inheritdoc />
    public void CreateImageCollage(ImageCollageOptions options, string? libraryName)
    {
        _inner.CreateImageCollage(options, libraryName);
    }

    /// <inheritdoc />
    public void CreateSplashscreen(IReadOnlyList<string> posters, IReadOnlyList<string> backdrops)
    {
        _inner.CreateSplashscreen(posters, backdrops);
    }

    /// <inheritdoc />
    public int CreateTrickplayTile(ImageCollageOptions options, int quality, int imgWidth, int? imgHeight)
    {
        return _inner.CreateTrickplayTile(options, quality, imgWidth, imgHeight);
    }

    /// <summary>
    /// Decides once whether the server's ffmpeg is new enough to decode HEIF, and says so in the
    /// log either way.
    /// </summary>
    /// <returns><c>true</c> when HEIC decoding should be attempted.</returns>
    private bool CheckFfmpeg()
    {
        var version = _mediaEncoder.EncoderVersion;

        if (version is null)
        {
            // Reached when no usable ffmpeg was found at startup, or when something resolved an
            // image encoder before validation ran. Either way there is nothing to decode with.
            _logger.LogWarning(
                "No ffmpeg version reported by the server, so HEIC decoding is off. Photos will resolve but will not render.");
            return false;
        }

        if (version < MinimumFfmpegVersion)
        {
            _logger.LogWarning(
                "ffmpeg {Version} at {Path} is older than the {Minimum} required to read HEIF; HEIC decoding is off. The server tolerates this version for video, so nothing else will complain.",
                version,
                _mediaEncoder.EncoderPath,
                MinimumFfmpegVersion);
            return false;
        }

        _logger.LogInformation("HEIC decoding enabled, using ffmpeg {Version} at {Path}.", version, _mediaEncoder.EncoderPath);
        return true;
    }
}
