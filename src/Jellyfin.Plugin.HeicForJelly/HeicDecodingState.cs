namespace Jellyfin.Plugin.HeicForJelly;

/// <summary>
/// Whether HEIC decoding is on, and if not, why. Decided once, by
/// <see cref="HeicImageEncoder"/>, the first time anything needs to know.
/// </summary>
/// <remarks>
/// One value serves both the decode path and <c>GET /HeicForJelly/Status</c>, so the endpoint
/// reports the very decision the encoder acts on rather than a second check that could drift
/// from it. See <c>docs/06-status-endpoint.md</c>.
/// </remarks>
public enum HeicDecodingState
{
    /// <summary>
    /// The server's ffmpeg is 7.0 or newer, so HEIC files are decoded.
    /// </summary>
    Enabled,

    /// <summary>
    /// The server reports no ffmpeg version at all.
    /// </summary>
    /// <remarks>
    /// A stock server refuses to start without a valid ffmpeg, so this means validation was
    /// skipped (<c>FFmpeg:novalidation</c>), the version string could not be parsed, or the
    /// decision was taken before startup validated ffmpeg.
    /// </remarks>
    FfmpegMissing,

    /// <summary>
    /// The server's ffmpeg predates 7.0, the first release that demuxes HEIF still images.
    /// Jellyfin itself accepts 4.4, so the server runs normally and only HEIC is affected.
    /// </summary>
    FfmpegTooOld,
}
