using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Common.Configuration;
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
/// Only two of the eleven interface members are intercepted: <see cref="GetImageSize"/>, which
/// answers for HEIC from the container rather than by decoding, and <see cref="EncodeImage"/>,
/// which decodes to a temporary file and then lets the inner encoder resize and encode it. Every
/// other call is delegated untouched, and so is every HEIC that cannot be decoded.
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
    private readonly HeicDecoder _decoder;
    private readonly Lazy<bool> _canDecodeHeic;

    /// <summary>
    /// Initializes a new instance of the <see cref="HeicImageEncoder"/> class.
    /// </summary>
    /// <param name="inner">The encoder being decorated — Skia on a healthy server, or the null
    /// encoder when Skia's native library is missing.</param>
    /// <param name="mediaEncoder">Used for ffmpeg's path and version, so that the plugin uses
    /// the same binary the server does rather than guessing at one.</param>
    /// <param name="appPaths">Used for the temporary directory the decode writes through.</param>
    /// <param name="logger">The logger.</param>
    public HeicImageEncoder(IImageEncoder inner, IMediaEncoder mediaEncoder, IApplicationPaths appPaths, ILogger<HeicImageEncoder> logger)
    {
        _inner = inner;
        _mediaEncoder = mediaEncoder;
        _logger = logger;
        _decoder = new HeicDecoder(mediaEncoder, appPaths, logger);

        // Deliberately lazy. MediaEncoder.EncoderVersion is null until the server validates
        // ffmpeg during startup, and this encoder may well be constructed before that happens.
        _canDecodeHeic = new Lazy<bool>(CheckFfmpeg);

        // The one line that proves the decoration took effect. Without it a failed swap is
        // indistinguishable from a working one, since the server logs only that the plugin
        // loaded and never names the encoder it ended up resolving.
        _logger.LogInformation("Image encoder decorated: {Name}", Name);
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

    /// <summary>
    /// Reports an image's dimensions, answering for HEIC from the container instead of the pixels.
    /// </summary>
    /// <param name="path">The image file.</param>
    /// <returns>The dimensions, already swapped for a rotated photo.</returns>
    /// <remarks>
    /// This is what <c>PhotoProvider.FetchAsync</c> calls to fill in a photo's width and height,
    /// so it runs once per item during a library scan. Answering from ffprobe's tile geometry
    /// keeps that to a metadata read; decoding the pixels first would have added the better part
    /// of a second per photo to every scan, which on a library this size is the difference between
    /// a scan and an outage.
    /// </remarks>
    public ImageDimensions GetImageSize(string path)
    {
        if (_canDecodeHeic.Value && HeicContainer.HasHeicExtension(path))
        {
            var image = _decoder.Probe(path);

            if (image is not null)
            {
                return image.Dimensions;
            }
        }

        return _inner.GetImageSize(path);
    }

    /// <inheritdoc />
    public string GetImageBlurHash(int xComp, int yComp, string path)
    {
        return _inner.GetImageBlurHash(xComp, yComp, path);
    }

    /// <summary>
    /// Produces the requested image, decoding HEIC first and letting the inner encoder do the rest.
    /// </summary>
    /// <param name="inputPath">The source image.</param>
    /// <param name="dateModified">The source's modification date.</param>
    /// <param name="outputPath">Where to write the result.</param>
    /// <param name="autoOrient">Whether the caller wants EXIF orientation applied.</param>
    /// <param name="orientation">The orientation the caller recorded for the item.</param>
    /// <param name="quality">Encoding quality.</param>
    /// <param name="options">The processing options: resize, blur, overlays, output format.</param>
    /// <param name="outputFormat">The requested output format.</param>
    /// <returns>The path actually written.</returns>
    /// <remarks>
    /// <para>
    /// Only the decode is taken over. Resizing, blurring, the played indicator and the output
    /// format negotiation all stay with the inner encoder — reimplementing any of it would mean
    /// reimplementing <see cref="ImageProcessingOptions"/>, and getting it subtly wrong.
    /// </para>
    /// <para>
    /// Every failure path falls back to the undecorated call. That yields today's behaviour, a
    /// HEIC served unresized, rather than a broken image: a regression is worse than the status
    /// quo this plugin is trying to improve on.
    /// </para>
    /// </remarks>
    public string EncodeImage(string inputPath, DateTime dateModified, string outputPath, bool autoOrient, ImageOrientation? orientation, int quality, ImageProcessingOptions options, ImageFormat outputFormat)
    {
        string Delegate() => _inner.EncodeImage(inputPath, dateModified, outputPath, autoOrient, orientation, quality, options, outputFormat);

        if (!_canDecodeHeic.Value || !HeicContainer.HasHeicExtension(inputPath))
        {
            return Delegate();
        }

        var image = _decoder.Probe(inputPath);

        if (image is null)
        {
            return Delegate();
        }

        var decoded = _decoder.TryDecode(inputPath, image);

        if (decoded is null)
        {
            return Delegate();
        }

        try
        {
            // The check J0 insisted on. ffmpeg exits 0 while handing back a single tile or an
            // embedded preview, and a wrong-sized image encoded here lands in Jellyfin's resized
            // image cache, keyed by a tag that will not change — it would outlive the bug.
            var actual = _inner.GetImageSize(decoded);

            if (!actual.Equals(image.Dimensions))
            {
                _logger.LogError(
                    "Decoded {Path} to {Actual} but its {Tiles}-tile grid declares {Expected}; discarding the result rather than caching it.",
                    inputPath,
                    actual,
                    image.TileCount,
                    image.Dimensions);
                return Delegate();
            }

            // The item's own orientation is dropped on purpose: the container's irot rotation is
            // already baked into the decoded file, so passing it on would rotate a second time.
            // TopLeft says "already upright" and is a no-op inside Skia, which reads the real
            // origin from the decoded file's codec and finds TopLeft there too.
            //
            // autoOrient must nevertheless be true, and that is not a contradiction. Passing false
            // makes SkiaEncoder.EncodeImage take its `HasDefaultOptions(...) && !autoOrient`
            // short-circuit and return the *input* path — which here is our temporary file, about
            // to be deleted in the finally below. ImageProcessor then hands the client a cache
            // path nothing ever wrote, and every full-size request 404s. That was the behaviour
            // until this line; see docs/02-jellyfin-internals.md §4.
            var encoded = _inner.EncodeImage(decoded, dateModified, outputPath, true, ImageOrientation.TopLeft, quality, options, outputFormat);

            if (!string.Equals(encoded, outputPath, StringComparison.OrdinalIgnoreCase))
            {
                // Belt and braces for the trap above: anything other than outputPath is a path the
                // caller cannot use, because the temporary file backing it is deleted below.
                _logger.LogError(
                    "The inner encoder returned {Returned} instead of writing {Output} while encoding {Path}; serving the original instead.",
                    encoded,
                    outputPath,
                    inputPath);
                return Delegate();
            }

            return encoded;
        }
        finally
        {
            _decoder.Delete(decoded);
        }
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
