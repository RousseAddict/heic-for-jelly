using System;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.HeicForJelly;

/// <summary>
/// The body of <c>GET /HeicForJelly/Status</c>.
/// </summary>
/// <remarks>
/// Every property name is pinned by attribute. Jellyfin answers a plain
/// <c>Accept: application/json</c> with its PascalCase formatter, so an unannotated property
/// would go out as <c>Decoding</c> and a client decoding <c>decoding</c> would read nothing.
/// </remarks>
public class StatusResponse
{
    /// <summary>
    /// The reason code when the image encoder the server resolves is not this plugin's.
    /// </summary>
    /// <remarks>
    /// Not a state of <see cref="HeicDecodingState"/>: the encoder that owns that decision is
    /// precisely what is missing. Reached when <c>PluginServiceRegistrator</c> declined to
    /// decorate, or when another plugin decorated the encoder after this one.
    /// </remarks>
    public const string EncoderNotDecorated = "ENCODER_NOT_DECORATED";

    /// <summary>
    /// Initializes a new instance of the <see cref="StatusResponse"/> class.
    /// </summary>
    /// <param name="decoding">Whether HEIC files are decoded.</param>
    /// <param name="reason">Why not, when they are not; null when they are.</param>
    public StatusResponse(bool decoding, string? reason)
    {
        Decoding = decoding;
        Reason = reason;
    }

    /// <summary>
    /// Gets a value indicating whether HEIC files on this server are decoded, and therefore
    /// render.
    /// </summary>
    [JsonPropertyName("decoding")]
    public bool Decoding { get; }

    /// <summary>
    /// Gets the reason code when <see cref="Decoding"/> is false: <c>FFMPEG_TOO_OLD</c>,
    /// <c>FFMPEG_MISSING</c> or <c>ENCODER_NOT_DECORATED</c>. Absent when it is true.
    /// </summary>
    /// <remarks>
    /// A client must render a code it does not know as a generic refusal: the set may grow, and
    /// a new code must not read as success. Neither the ffmpeg version nor its path is ever
    /// sent; the server log already gives both to the administrator.
    /// </remarks>
    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; }

    /// <summary>
    /// Builds the response for the encoder's own decision.
    /// </summary>
    /// <param name="state">The decision the encoder acts on.</param>
    /// <returns>The response to send.</returns>
    public static StatusResponse From(HeicDecodingState state)
    {
        return state switch
        {
            HeicDecodingState.Enabled => new StatusResponse(true, null),
            HeicDecodingState.FfmpegTooOld => new StatusResponse(false, "FFMPEG_TOO_OLD"),
            HeicDecodingState.FfmpegMissing => new StatusResponse(false, "FFMPEG_MISSING"),
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Every decoding state needs a wire code."),
        };
    }
}
