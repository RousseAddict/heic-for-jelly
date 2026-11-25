using System.Net.Mime;
using MediaBrowser.Controller.Drawing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.HeicForJelly;

/// <summary>
/// The plugin's only HTTP surface: one read-only status route.
/// </summary>
/// <remarks>
/// <para>
/// It exists so that a client can tell whether HEIC photos will render, which no
/// non-administrator could otherwise learn: <c>PluginsController</c> requires elevation, and
/// a server with no HEIC file yet looks identical with or without this plugin.
/// </para>
/// <para>
/// It takes no input, serves no file and never touches a path, which is what keeps it clear
/// of the hole that disqualified the prior art — an anonymous action converting any file
/// named in its query string. Images still go through Jellyfin's own pipeline only. The rule
/// this narrows is in <c>CLAUDE.md</c>; the reasoning is <c>docs/06-status-endpoint.md</c>.
/// </para>
/// <para>
/// Routed because <c>ApplicationHost.GetApiPluginAssemblies</c> hands every plugin assembly
/// containing a <see cref="ControllerBase"/> to <c>AddJellyfinApi</c> as an MVC application
/// part. There is no automatic prefix, so the route below is the whole path.
/// </para>
/// </remarks>
[ApiController]
[Authorize]
[Route("HeicForJelly")]
[Produces(MediaTypeNames.Application.Json)]
public class HeicForJellyController : ControllerBase
{
    private readonly IImageEncoder _imageEncoder;

    /// <summary>
    /// Initializes a new instance of the <see cref="HeicForJellyController"/> class.
    /// </summary>
    /// <param name="imageEncoder">The encoder the server actually resolves, which is this
    /// plugin's decorator whenever the decoration took effect.</param>
    public HeicForJellyController(IImageEncoder imageEncoder)
    {
        _imageEncoder = imageEncoder;
    }

    /// <summary>
    /// Reports whether this server decodes HEIC, and if not, why.
    /// </summary>
    /// <response code="200">The decision the decoder acts on.</response>
    /// <response code="401">No token, or one the server does not accept.</response>
    /// <response code="403">A valid user the server refuses here: remote access disabled for
    /// them, or outside their parental schedule.</response>
    /// <returns>The status.</returns>
    /// <remarks>
    /// Plain <see cref="AuthorizeAttribute"/>, so any signed-in user, and an API key too; never
    /// an anonymous caller. Existence of this route is also how a client detects the plugin, so
    /// only a 404 means "not installed" — which a 1.0.x install answers as well, since it has
    /// no route at all.
    /// </remarks>
    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<StatusResponse> GetStatus()
    {
        if (_imageEncoder is not HeicImageEncoder decorated)
        {
            return new StatusResponse(false, StatusResponse.EncoderNotDecorated);
        }

        return StatusResponse.From(decorated.DecodingState);
    }
}
