using System;
using System.IO;

namespace Jellyfin.Plugin.HeicForJelly;

/// <summary>
/// Reads the few container bytes that decide whether a file really is HEIF, and which way up it
/// is meant to be shown.
/// </summary>
/// <remarks>
/// Both questions exist because the tools below cannot answer them. The extension lies — J0 found
/// 1 386 JPEGs named <c>.HEIC</c> in this very library, and the upstream transcode that produced
/// them is still unidentified, so more will arrive. And ffprobe reports no rotation whatsoever
/// for HEIF: no side data, no tag. A decoder that skips this file ships sideways photos for 58 %
/// of the library, silently. See <c>docs/04-j0-spike.md</c>.
/// </remarks>
internal static class HeicContainer
{
    /// <summary>
    /// How much of the file head to keep when looking for the transform boxes.
    /// </summary>
    /// <remarks>
    /// <c>irot</c> lives in <c>meta/iprp/ipco</c>, which an ISO base media file writes before the
    /// <c>mdat</c> payload. A few tens of KiB covers an iPhone's item property container; this
    /// bounds the cost of a read that happens once per image request, and it also bounds how far
    /// the byte scan below can wander into compressed data.
    /// </remarks>
    private const int HeaderBytes = 256 * 1024;

    /// <summary>
    /// The brands that mean "HEVC still image in an ISO base media container".
    /// </summary>
    /// <remarks>
    /// AVIF's <c>avif</c>/<c>avis</c> are deliberately absent: Jellyfin already lists avif as a
    /// photo format, and claiming it here would put this plugin on the decode path for files it
    /// was never tested against.
    /// </remarks>
    private static readonly string[] HeifBrands =
    {
        "heic", "heix", "heim", "heis", "hevc", "hevx", "hevm", "hevs", "mif1", "mif2", "msf1", "miaf",
    };

    /// <summary>
    /// Tests the extension alone, without opening the file.
    /// </summary>
    /// <param name="path">The path to test.</param>
    /// <returns><c>true</c> for <c>.heic</c> and <c>.heif</c>, in any case.</returns>
    /// <remarks>
    /// This is the cheap gate that keeps the rest of the plugin off the hot path. The encoder is
    /// asked about every image the server renders, for every library; reading 256 KiB of each one
    /// to find out it is a JPEG would be a real cost. The extension decides whether to look
    /// further, never whether to decode — <see cref="TryRead"/> decides that, on the bytes.
    /// </remarks>
    public static bool HasHeicExtension(string path)
    {
        var extension = Path.GetExtension(path.AsSpan());
        return extension.Equals(".heic", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".heif", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads the head of a file and reports whether it is HEIF and how it should be rotated.
    /// </summary>
    /// <param name="path">The file to inspect.</param>
    /// <param name="rotationDegrees">The clockwise rotation needed to display it upright: 0, 90,
    /// 180 or 270.</param>
    /// <param name="mirrored">Whether an <c>imir</c> box is present, which this plugin detects but
    /// does not apply. No file in the 575 surveyed for J0 carries one; the flag exists so that the
    /// day one appears it is a log line rather than a mirrored photo nobody notices.</param>
    /// <returns><c>true</c> when the file is HEIF and was read successfully.</returns>
    public static bool TryRead(string path, out int rotationDegrees, out bool mirrored)
    {
        rotationDegrees = 0;
        mirrored = false;

        byte[] head;
        int length;

        try
        {
            using var stream = File.OpenRead(path);
            head = new byte[HeaderBytes];
            length = stream.ReadAtLeast(head, HeaderBytes, throwOnEndOfStream: false);
        }
        catch (IOException)
        {
            // A file being written, a dropped network mount. Say "not ours" and let the inner
            // encoder produce whatever error it normally would, rather than inventing one here.
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        var span = head.AsSpan(0, length);

        if (!IsHeif(span))
        {
            return false;
        }

        rotationDegrees = ReadRotationDegrees(span);
        mirrored = HasBox(span, "imir");
        return true;
    }

    /// <summary>
    /// Tests whether the bytes start with an <c>ftyp</c> box declaring a HEIF brand.
    /// </summary>
    /// <param name="head">The start of the file.</param>
    /// <returns><c>true</c> when the file is HEIF.</returns>
    private static bool IsHeif(ReadOnlySpan<byte> head)
    {
        if (head.Length < 12 || !Matches(head[4..8], "ftyp"))
        {
            return false;
        }

        // The major brand sits at offset 8 and the compatible brands follow it to the end of the
        // box. Checking all of them matters: files here carry major brand "heic" but others in the
        // wild put a generic brand first and the meaningful one in the compatible list.
        var boxSize = (int)Math.Min(ReadUInt32(head), head.Length);

        for (var offset = 8; offset + 4 <= boxSize; offset += 4)
        {
            foreach (var brand in HeifBrands)
            {
                if (Matches(head.Slice(offset, 4), brand))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Finds the <c>irot</c> box and returns the clockwise rotation, in degrees, needed to display
    /// the image upright.
    /// </summary>
    /// <param name="head">The start of the file.</param>
    /// <returns>0, 90, 180 or 270.</returns>
    /// <remarks>
    /// <c>irot</c> stores a counter-clockwise angle in the low two bits of its single payload
    /// byte; this returns the clockwise equivalent, because that is the direction every rotation
    /// filter and EXIF orientation value is expressed in. Walking the box tree properly would mean
    /// parsing <c>meta</c>, <c>iprp</c>, <c>ipco</c> and <c>ipma</c>; scanning for the literal
    /// instead is a fraction of the code, and requiring the exact nine-byte length prefix in front
    /// of it makes a coincidental match in compressed data a one-in-four-billion event.
    /// </remarks>
    private static int ReadRotationDegrees(ReadOnlySpan<byte> head)
    {
        var index = IndexOfBox(head, "irot");

        if (index < 0)
        {
            return 0;
        }

        var counterClockwise = (head[index + 4] & 3) * 90;
        return counterClockwise == 0 ? 0 : 360 - counterClockwise;
    }

    /// <summary>
    /// Reports whether a nine-byte transform box of the given type is present.
    /// </summary>
    /// <param name="head">The start of the file.</param>
    /// <param name="type">The four-character box type.</param>
    /// <returns><c>true</c> when the box was found.</returns>
    private static bool HasBox(ReadOnlySpan<byte> head, string type) => IndexOfBox(head, type) >= 0;

    /// <summary>
    /// Locates a nine-byte transform box and returns the offset of its type field.
    /// </summary>
    /// <param name="head">The start of the file.</param>
    /// <param name="type">The four-character box type.</param>
    /// <returns>The offset, or -1.</returns>
    private static int IndexOfBox(ReadOnlySpan<byte> head, string type)
    {
        for (var offset = 4; offset + 5 <= head.Length; offset++)
        {
            if (Matches(head.Slice(offset, 4), type)
                && head[offset - 4] == 0 && head[offset - 3] == 0
                && head[offset - 2] == 0 && head[offset - 1] == 9)
            {
                return offset;
            }
        }

        return -1;
    }

    /// <summary>
    /// Compares four bytes against a four-character ASCII box type or brand.
    /// </summary>
    /// <param name="bytes">Exactly four bytes.</param>
    /// <param name="ascii">The four characters to match.</param>
    /// <returns><c>true</c> when they are equal.</returns>
    private static bool Matches(ReadOnlySpan<byte> bytes, string ascii)
    {
        for (var i = 0; i < 4; i++)
        {
            if (bytes[i] != (byte)ascii[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Reads a big-endian 32-bit box size.
    /// </summary>
    /// <param name="bytes">At least four bytes.</param>
    /// <returns>The value.</returns>
    private static uint ReadUInt32(ReadOnlySpan<byte> bytes)
        => ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
}
