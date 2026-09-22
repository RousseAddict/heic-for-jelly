using System.Collections.Generic;
using System.Globalization;
using System.Text;
using MediaBrowser.Model.Drawing;

namespace Jellyfin.Plugin.HeicForJelly;

/// <summary>
/// The geometry of one HEIC tile grid, and the ffmpeg filtergraph that reassembles it.
/// </summary>
/// <remarks>
/// This is the part of the plugin that has no side effects: probing produces one of these,
/// decoding consumes it. Keeping the arithmetic away from the process plumbing is what makes the
/// layout string readable at all — and the layout string is where a silent error costs the most,
/// because a wrong offset produces a scrambled photo that ffmpeg reports as a success.
/// </remarks>
internal sealed class HeicImage
{
    private readonly IReadOnlyList<(int Stream, int X, int Y)> _tiles;
    private readonly int _cropX;
    private readonly int _cropY;
    private readonly int _width;
    private readonly int _height;
    private readonly int _rotationDegrees;

    /// <summary>
    /// Initializes a new instance of the <see cref="HeicImage"/> class.
    /// </summary>
    /// <param name="tiles">Each tile's <b>global</b> ffmpeg stream index and its pixel offset
    /// inside the grid, in the order ffprobe listed them.</param>
    /// <param name="cropX">Left edge of the visible area within the assembled grid.</param>
    /// <param name="cropY">Top edge of the visible area within the assembled grid.</param>
    /// <param name="width">Visible width, before rotation.</param>
    /// <param name="height">Visible height, before rotation.</param>
    /// <param name="rotationDegrees">Clockwise rotation to apply for display: 0, 90, 180 or 270.</param>
    public HeicImage(
        IReadOnlyList<(int Stream, int X, int Y)> tiles,
        int cropX,
        int cropY,
        int width,
        int height,
        int rotationDegrees)
    {
        _tiles = tiles;
        _cropX = cropX;
        _cropY = cropY;
        _width = width;
        _height = height;
        _rotationDegrees = rotationDegrees;
    }

    /// <summary>
    /// Gets the size the decoded image will have once rotated — the size Jellyfin should store.
    /// </summary>
    /// <remarks>
    /// The grid dimensions ffprobe reports are those of the coded image, so a portrait photo shot
    /// with the phone turned sideways probes as landscape. Swapping here rather than at each call
    /// site is what keeps <c>GetImageSize</c> and the post-decode assertion in agreement.
    /// </remarks>
    public ImageDimensions Dimensions => _rotationDegrees is 90 or 270
        ? new ImageDimensions(_height, _width)
        : new ImageDimensions(_width, _height);

    /// <summary>
    /// Gets the number of tiles the grid is made of.
    /// </summary>
    /// <remarks>
    /// Worth logging: the survey behind J0 found grids of 9 to 98 tiles in one library, so a
    /// count that looks like a round guess is a sign the wrong stream group was picked.
    /// </remarks>
    public int TileCount => _tiles.Count;

    /// <summary>
    /// Builds the <c>-filter_complex</c> argument that turns the tile streams into one image.
    /// </summary>
    /// <returns>A filtergraph ending in the <c>[out]</c> label.</returns>
    /// <remarks>
    /// <para>
    /// The shape is <c>[0:a][0:b]…xstack,crop[,rotate][out]</c>. Inputs must be written as
    /// filtergraph labels: passing the same streams with <c>-map</c> fails with
    /// <i>Cannot find a matching stream for unlabeled input pad xstack</i>.
    /// </para>
    /// <para>
    /// The crop is not cosmetic. A grid is coded in whole tiles, so a 4032x3024 photo is stored as
    /// 4096x3072 and the last row and column contain padding.
    /// </para>
    /// <para>
    /// <c>exact=1</c> on the crop is load-bearing for the eleven files in this library whose
    /// declared size has an odd dimension. Without it the filter silently rounds the crop down to
    /// the chroma alignment of its <em>input</em> — the tiles are yuv420p — so a photo declaring
    /// 3418x2359 decodes as 3418x2358, and the post-decode size assertion then throws away a
    /// picture that is otherwise perfect. It is not the output format that decides this: writing
    /// PNG rounds down too. Since the crop offset is always 0:0 here, disabling the alignment
    /// cannot shift the chroma planes.
    /// </para>
    /// </remarks>
    public string BuildFilterGraph()
    {
        var builder = new StringBuilder();

        foreach (var tile in _tiles)
        {
            builder.Append(CultureInfo.InvariantCulture, $"[0:{tile.Stream}]");
        }

        builder.Append(CultureInfo.InvariantCulture, $"xstack=inputs={_tiles.Count}:layout=");

        for (var i = 0; i < _tiles.Count; i++)
        {
            if (i > 0)
            {
                builder.Append('|');
            }

            builder.Append(CultureInfo.InvariantCulture, $"{_tiles[i].X}_{_tiles[i].Y}");
        }

        builder.Append(CultureInfo.InvariantCulture, $",crop={_width}:{_height}:{_cropX}:{_cropY}:exact=1");
        builder.Append(RotationFilter());
        builder.Append("[out]");

        return builder.ToString();
    }

    /// <summary>
    /// Maps the container's rotation onto ffmpeg filters.
    /// </summary>
    /// <returns>A leading-comma filter fragment, or an empty string.</returns>
    /// <remarks>
    /// <c>transpose=1</c> is 90 degrees clockwise and <c>transpose=2</c> counter-clockwise. There
    /// is no 180 degree transpose, hence the flip pair.
    /// </remarks>
    private string RotationFilter() => _rotationDegrees switch
    {
        90 => ",transpose=1",
        180 => ",hflip,vflip",
        270 => ",transpose=2",
        _ => string.Empty,
    };
}
