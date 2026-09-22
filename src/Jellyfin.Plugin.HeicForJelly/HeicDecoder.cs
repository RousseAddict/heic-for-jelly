using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.HeicForJelly;

/// <summary>
/// Turns a HEIC tile grid into an ordinary image file, using the ffmpeg the server already ships.
/// </summary>
/// <remarks>
/// <para>
/// Decoding happens out of process on purpose. HEIC is attacker-influenced input — anything that
/// lands in a photo library is — and the alternative considered, Magick.NET, parses it inside the
/// Jellyfin process where a libheif flaw becomes remote code execution in the media server. See
/// <c>docs/03-prior-art.md</c> §4.
/// </para>
/// <para>
/// Every invocation forces the demuxer and asserts the result. Both guards come from J0, where a
/// plain <c>ffmpeg -i</c> exited 0 three separate times while producing either an embedded 544x617
/// preview or a single 512x512 tile. Exit codes prove nothing here.
/// </para>
/// </remarks>
internal sealed class HeicDecoder
{
    /// <summary>
    /// The demuxer name for the ISO base media container family.
    /// </summary>
    /// <remarks>
    /// Without this, ffmpeg's <c>jpeg_pipe</c> probe wins on any file that happens to start with a
    /// JPEG marker and quietly decodes an embedded preview instead of the photo.
    /// </remarks>
    private const string Demuxer = "mov";

    private const int ProbeTimeoutMs = 30_000;
    private const int DecodeTimeoutMs = 120_000;

    private readonly IMediaEncoder _mediaEncoder;
    private readonly IApplicationPaths _appPaths;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="HeicDecoder"/> class.
    /// </summary>
    /// <param name="mediaEncoder">Source of the ffmpeg and ffprobe paths, so that the plugin runs
    /// the same binaries the server validated at startup rather than whatever is on PATH.</param>
    /// <param name="appPaths">Used for the temporary directory. Jellyfin's own temp area sits on
    /// the cache volume; <c>/tmp</c> in a container is often a small tmpfs and these intermediates
    /// run to several megabytes.</param>
    /// <param name="logger">The logger.</param>
    public HeicDecoder(IMediaEncoder mediaEncoder, IApplicationPaths appPaths, ILogger logger)
    {
        _mediaEncoder = mediaEncoder;
        _appPaths = appPaths;
        _logger = logger;
    }

    /// <summary>
    /// Reads a file's container and tile geometry without decoding any pixels.
    /// </summary>
    /// <param name="path">The file to probe.</param>
    /// <returns>The geometry, or <c>null</c> when the file is not a HEIC tile grid this plugin can
    /// handle — in which case the caller must fall back to the encoder it decorates.</returns>
    public HeicImage? Probe(string path)
    {
        if (!HeicContainer.TryRead(path, out var rotationDegrees, out var mirrored))
        {
            // Not HEIF at all. The usual cause is a JPEG wearing a .heic name, which this library
            // produced 1 386 of; returning null hands it back to Skia, which reads it perfectly.
            return null;
        }

        if (mirrored)
        {
            _logger.LogWarning(
                "{Path} carries an imir box, which this plugin does not apply. The image will be decoded unmirrored. No file in the library survey had one, so this is worth reporting.",
                path);
        }

        if (!TryRun(
                _mediaEncoder.ProbePath,
                new[] { "-v", "error", "-f", Demuxer, "-print_format", "json", "-show_stream_groups", path },
                ProbeTimeoutMs,
                out var json,
                out var probeError))
        {
            _logger.LogWarning("ffprobe failed on {Path}: {Error}", path, probeError);
            return null;
        }

        try
        {
            return ParseTileGrid(json, rotationDegrees, path);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            _logger.LogWarning(ex, "Could not make sense of the stream groups ffprobe reported for {Path}.", path);
            return null;
        }
    }

    /// <summary>
    /// Decodes a probed image to a new temporary file.
    /// </summary>
    /// <param name="sourcePath">The HEIC file.</param>
    /// <param name="image">The geometry returned by <see cref="Probe"/>.</param>
    /// <returns>The path of the decoded file, which the caller owns and must delete, or
    /// <c>null</c> if anything went wrong.</returns>
    /// <remarks>
    /// The intermediate is a near-lossless JPEG rather than a PNG. Skia resizes and re-encodes it
    /// immediately afterwards, so the extra generation is invisible, whereas writing a 16 MB PNG
    /// measured ten times slower than the 0.6 s the decode itself takes.
    /// </remarks>
    public string? TryDecode(string sourcePath, HeicImage image)
    {
        Directory.CreateDirectory(_appPaths.TempDirectory);
        var outputPath = Path.Combine(_appPaths.TempDirectory, Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".jpg");

        var arguments = new[]
        {
            "-nostdin",
            "-v", "error",
            "-f", Demuxer,
            "-i", sourcePath,
            "-filter_complex", image.BuildFilterGraph(),
            "-map", "[out]",
            "-frames:v", "1",

            // The image2 muxer refuses to write a single file without this, and says so.
            "-update", "1",
            "-q:v", "2",
            "-y", outputPath,
        };

        if (!TryRun(_mediaEncoder.EncoderPath, arguments, DecodeTimeoutMs, out _, out var error))
        {
            _logger.LogWarning("ffmpeg could not decode {Path}: {Error}", sourcePath, error);
            Delete(outputPath);
            return null;
        }

        return outputPath;
    }

    /// <summary>
    /// Deletes a temporary file, tolerating a file that is already gone.
    /// </summary>
    /// <param name="path">The file to delete.</param>
    public void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException ex)
        {
            // Leaking one file in the temp directory is not worth failing an image request over,
            // but it should not be invisible either: a persistent leak fills the cache volume.
            _logger.LogWarning(ex, "Could not delete the temporary file {Path}.", path);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Could not delete the temporary file {Path}.", path);
        }
    }

    /// <summary>
    /// Extracts the primary tile grid from ffprobe's JSON.
    /// </summary>
    /// <param name="json">The ffprobe output.</param>
    /// <param name="rotationDegrees">The rotation read from the container.</param>
    /// <param name="path">The file, for logging only.</param>
    /// <returns>The geometry, or <c>null</c> when there is no tile grid.</returns>
    private HeicImage? ParseTileGrid(string json, int rotationDegrees, string path)
    {
        using var document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty("stream_groups", out var groups))
        {
            _logger.LogWarning("{Path} is HEIF but ffprobe reported no stream groups; leaving it alone.", path);
            return null;
        }

        JsonElement? primary = null;
        JsonElement? firstGrid = null;

        foreach (var group in groups.EnumerateArray())
        {
            if (!group.TryGetProperty("type", out var type) || type.GetString() != "Tile Grid")
            {
                continue;
            }

            firstGrid ??= group;

            // Two files in the surveyed library carry a second grid at exactly half resolution —
            // the HDR gain map. Both are tile grids and both decode without error, so the only
            // thing separating the photo from the gain map is this flag, which the demuxer sets
            // from the container's primary-item box.
            if (group.TryGetProperty("disposition", out var disposition)
                && disposition.TryGetProperty("default", out var isDefault)
                && isDefault.GetInt32() == 1)
            {
                primary = group;
                break;
            }
        }

        var chosen = primary ?? firstGrid;

        if (chosen is null)
        {
            _logger.LogWarning("{Path} is HEIF but holds no tile grid; this plugin only handles tiled images.", path);
            return null;
        }

        // ffprobe numbers a subcomponent's stream relative to its own group, while a filtergraph
        // label needs the file-wide index. They coincide for the first group and diverge for every
        // one after it, so translating through the group's stream list is not optional.
        var globalIndices = new List<int>();

        foreach (var stream in chosen.Value.GetProperty("streams").EnumerateArray())
        {
            globalIndices.Add(stream.GetProperty("index").GetInt32());
        }

        var component = chosen.Value.GetProperty("components")[0];
        var tiles = new List<(int Stream, int X, int Y)>();

        foreach (var subcomponent in component.GetProperty("subcomponents").EnumerateArray())
        {
            var relative = subcomponent.GetProperty("stream_index").GetInt32();

            if (relative < 0 || relative >= globalIndices.Count)
            {
                _logger.LogWarning("{Path} declares a tile on stream {Index}, which its group does not contain.", path, relative);
                return null;
            }

            tiles.Add((
                globalIndices[relative],
                subcomponent.GetProperty("tile_horizontal_offset").GetInt32(),
                subcomponent.GetProperty("tile_vertical_offset").GetInt32()));
        }

        if (tiles.Count == 0)
        {
            return null;
        }

        return new HeicImage(
            tiles,
            component.GetProperty("horizontal_offset").GetInt32(),
            component.GetProperty("vertical_offset").GetInt32(),
            component.GetProperty("width").GetInt32(),
            component.GetProperty("height").GetInt32(),
            rotationDegrees);
    }

    /// <summary>
    /// Runs a child process to completion and collects its output.
    /// </summary>
    /// <param name="fileName">The executable.</param>
    /// <param name="arguments">Arguments, passed individually so that no shell ever sees a
    /// filename — a library path is user-controlled and quoting it by hand is how command
    /// injection gets in.</param>
    /// <param name="timeoutMs">How long to wait before killing it.</param>
    /// <param name="standardOutput">What it wrote to stdout.</param>
    /// <param name="standardError">What it wrote to stderr.</param>
    /// <returns><c>true</c> when the process exited with status 0.</returns>
    private bool TryRun(
        string fileName,
        IReadOnlyList<string> arguments,
        int timeoutMs,
        out string standardOutput,
        out string standardError)
    {
        standardOutput = string.Empty;
        standardError = string.Empty;

        using var process = new Process();
        process.StartInfo.FileName = fileName;

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            standardError = ex.Message;
            return false;
        }

        // Read both pipes before waiting. A child that fills a pipe buffer nobody is draining
        // blocks forever, and ffmpeg is perfectly capable of producing more than a pipe holds.
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(timeoutMs))
        {
            // An image request holds one of Jellyfin's limited encoding slots, so a hung ffmpeg
            // does not merely fail a thumbnail, it starves the whole image pipeline.
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
            {
                // It exited between the timeout and the kill; nothing to do.
            }

            standardError = string.Create(CultureInfo.InvariantCulture, $"timed out after {timeoutMs} ms");
            return false;
        }

        // The parameterless overload is what guarantees the redirected readers have finished.
        process.WaitForExit();

        standardOutput = outputTask.GetAwaiter().GetResult();
        standardError = errorTask.GetAwaiter().GetResult();

        return process.ExitCode == 0;
    }
}
