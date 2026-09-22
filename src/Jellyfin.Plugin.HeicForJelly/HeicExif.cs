using System;
using System.Globalization;
using System.Text;

namespace Jellyfin.Plugin.HeicForJelly;

/// <summary>
/// The EXIF fields Jellyfin stores on a photo, read out of a TIFF block by hand.
/// </summary>
/// <remarks>
/// <para>
/// Written rather than taken from a library on purpose. The server reads EXIF with TagLib#, which
/// <c>PhotoProvider</c> will not point at a <c>.heic</c> — its own comment says other extensions
/// "might cause taglib to hang" — and the obvious NuGet alternative would add a general-purpose
/// parser, and its transitive dependencies, to handle a dozen tags. What is here reads only what
/// is used, and every offset it follows is bounds-checked against the block it was handed, so a
/// malformed file yields <c>null</c> or a half-filled result rather than an exception.
/// </para>
/// <para>
/// Deliberately absent: orientation. It is present in these files, and it agrees with the
/// container's <c>irot</c> on all 575 surveyed, but it must not be stored —
/// <see cref="HeicMetadataProvider"/> explains why.
/// </para>
/// </remarks>
internal sealed class HeicExif
{
    /// <summary>
    /// Byte order mark, the constant 42, and the offset of the first directory.
    /// </summary>
    private const int TiffHeaderLength = 8;

    /// <summary>
    /// A ceiling on entries per directory, so that a corrupt count cannot drive a long loop.
    /// </summary>
    /// <remarks>
    /// An iPhone writes on the order of thirty. Anything past a few hundred is not a real
    /// directory, and treating it as one would mean reading 12 bytes per phantom entry.
    /// </remarks>
    private const int MaxIfdEntries = 512;

    /// <summary>
    /// Root, then Exif or GPS, then nothing. Bounds the recursion a crafted file could provoke.
    /// </summary>
    /// <remarks>
    /// Sub-directory pointers are just offsets into the same block, so nothing in the format stops
    /// one pointing back at the directory that holds it.
    /// </remarks>
    private const int MaxIfdDepth = 1;

    /// <summary>
    /// Bytes per TIFF value type, indexed by the type code. Zero marks a code TIFF does not define.
    /// </summary>
    private static readonly int[] TypeSizes = { 0, 1, 1, 2, 4, 8, 1, 1, 2, 4, 8, 4, 8 };

    private DateTime? _dateTimeOriginal;
    private DateTime? _dateTimeDigitized;
    private DateTime? _dateTime;
    private double? _latitudeDegrees;
    private double? _longitudeDegrees;
    private double? _altitudeMetres;
    private char _latitudeRef;
    private char _longitudeRef;
    private byte _altitudeRef;

    /// <summary>
    /// Which directory a tag was found in. The same number means different things in each, so the
    /// parser has to know where it is rather than switch on the number alone.
    /// </summary>
    private enum IfdKind
    {
        /// <summary>IFD0, holding the camera and the file-level timestamp.</summary>
        Root,

        /// <summary>The Exif sub-directory, holding exposure and the capture timestamp.</summary>
        Exif,

        /// <summary>The GPS sub-directory.</summary>
        Gps,
    }

    /// <summary>
    /// Gets when the photo was taken, with no timezone attached.
    /// </summary>
    /// <remarks>
    /// The <see cref="DateTime.Kind"/> is <see cref="DateTimeKind.Unspecified"/> and must stay that
    /// way. EXIF timestamps carry no zone; the neighbouring <c>OffsetTimeOriginal</c> tag does and
    /// is deliberately ignored, because <c>PhotoProvider</c> ignores it too, and jellypic's
    /// timeline would otherwise place a HEIC and a JPEG from the same afternoon hours apart.
    /// </remarks>
    public DateTime? DateTaken { get; private set; }

    /// <summary>Gets the camera manufacturer.</summary>
    public string? CameraMake { get; private set; }

    /// <summary>Gets the camera model.</summary>
    public string? CameraModel { get; private set; }

    /// <summary>Gets the software that wrote the file — the iOS version, on these photos.</summary>
    public string? Software { get; private set; }

    /// <summary>Gets the exposure time, in seconds.</summary>
    public double? ExposureTime { get; private set; }

    /// <summary>Gets the focal length, in millimetres.</summary>
    public double? FocalLength { get; private set; }

    /// <summary>
    /// Gets the aperture, as the raw APEX value rather than an f-number.
    /// </summary>
    /// <remarks>
    /// Unconverted on purpose: <c>PhotoProvider</c> stores <c>ApertureValue</c> as it stands, so a
    /// JPEG in this library shot at f/1.5 is recorded as 1.17. Converting here would make HEIC and
    /// JPEG items disagree about the same lens.
    /// </remarks>
    public double? Aperture { get; private set; }

    /// <summary>
    /// Gets the shutter speed, as the raw APEX value.
    /// </summary>
    /// <remarks>
    /// Same reasoning as <see cref="Aperture"/>. JPEG items here have no shutter speed at all,
    /// because TagLib hands <c>PhotoProvider</c> a signed rational where it expects an unsigned one
    /// and the cast quietly fails; filling it in is a small improvement on that, not a divergence.
    /// </remarks>
    public double? ShutterSpeed { get; private set; }

    /// <summary>Gets the ISO sensitivity.</summary>
    public int? IsoSpeedRating { get; private set; }

    /// <summary>Gets the latitude in signed degrees, negative south of the equator.</summary>
    public double? Latitude { get; private set; }

    /// <summary>Gets the longitude in signed degrees, negative west of Greenwich.</summary>
    public double? Longitude { get; private set; }

    /// <summary>Gets the altitude in metres, negative below sea level.</summary>
    public double? Altitude { get; private set; }

    /// <summary>
    /// Parses a TIFF block.
    /// </summary>
    /// <param name="tiff">The bytes from the TIFF header onwards. Every offset the format stores
    /// is relative to that header, so index 0 here is the origin the whole parse works from.</param>
    /// <returns>The fields that were found, or <c>null</c> if this is not a TIFF block.</returns>
    public static HeicExif? Parse(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < TiffHeaderLength)
        {
            return null;
        }

        bool bigEndian;

        if (tiff[0] == (byte)'M' && tiff[1] == (byte)'M')
        {
            bigEndian = true;
        }
        else if (tiff[0] == (byte)'I' && tiff[1] == (byte)'I')
        {
            bigEndian = false;
        }
        else
        {
            return null;
        }

        if (ReadUInt16(tiff, 2, bigEndian) != 42)
        {
            return null;
        }

        var firstIfd = (long)ReadUInt32(tiff, 4, bigEndian);

        if (firstIfd < TiffHeaderLength || firstIfd + 2 > tiff.Length)
        {
            return null;
        }

        var exif = new HeicExif();
        exif.ReadIfd(tiff, bigEndian, (int)firstIfd, IfdKind.Root, 0);
        exif.Finish();

        return exif;
    }

    /// <summary>
    /// Returns the slice holding an entry's value, or <c>false</c> if it falls outside the block.
    /// </summary>
    /// <param name="tiff">The TIFF block.</param>
    /// <param name="bigEndian">The byte order.</param>
    /// <param name="entry">Offset of the twelve-byte entry.</param>
    /// <param name="type">The TIFF value type.</param>
    /// <param name="count">The number of values.</param>
    /// <param name="value">Set to the bytes of the value.</param>
    /// <returns><c>true</c> when the value was located.</returns>
    /// <remarks>
    /// Values of four bytes or fewer sit inside the entry; anything larger is stored elsewhere and
    /// pointed at. That pointer is the one number in a TIFF file a writer chooses freely, which is
    /// why it is range-checked here — and why the arithmetic stays in <c>long</c> until it has
    /// been, since narrowing first would let a large offset wrap into a small valid-looking one.
    /// </remarks>
    private static bool TryReadValue(ReadOnlySpan<byte> tiff, bool bigEndian, int entry, int type, uint count, out ReadOnlySpan<byte> value)
    {
        value = default;

        if (type <= 0 || type >= TypeSizes.Length || TypeSizes[type] == 0)
        {
            return false;
        }

        var total = (long)TypeSizes[type] * count;

        if (total <= 0 || total > tiff.Length)
        {
            return false;
        }

        var offset = total <= 4 ? entry + 8 : (long)ReadUInt32(tiff, entry + 8, bigEndian);

        if (offset < 0 || offset + total > tiff.Length)
        {
            return false;
        }

        value = tiff.Slice((int)offset, (int)total);
        return true;
    }

    /// <summary>
    /// Reads an EXIF timestamp, which is ASCII in a fixed layout and carries no timezone.
    /// </summary>
    /// <param name="value">The value bytes.</param>
    /// <returns>The timestamp, or <c>null</c> if it does not match the layout.</returns>
    private static DateTime? ReadDateTime(ReadOnlySpan<byte> value)
    {
        var text = ReadAscii(value);

        if (text is null)
        {
            return null;
        }

        // DateTimeStyles.None, so the result is Unspecified: see the remarks on DateTaken.
        return DateTime.TryParseExact(text, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// Reads a NUL-terminated ASCII value.
    /// </summary>
    /// <param name="value">The value bytes.</param>
    /// <returns>The trimmed text, or <c>null</c> when it is empty.</returns>
    private static string? ReadAscii(ReadOnlySpan<byte> value)
    {
        var end = value.IndexOf((byte)0);
        var text = Encoding.ASCII.GetString(end < 0 ? value : value[..end]).Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// Reads a rational — two 32-bit integers, numerator then denominator.
    /// </summary>
    /// <param name="value">The value bytes.</param>
    /// <param name="offset">Where in them the rational starts.</param>
    /// <param name="bigEndian">The byte order.</param>
    /// <param name="signed">Whether the components are signed.</param>
    /// <returns>The quotient, or <c>null</c> when it will not divide.</returns>
    private static double? ReadRational(ReadOnlySpan<byte> value, int offset, bool bigEndian, bool signed)
    {
        if (offset < 0 || offset + 8 > value.Length)
        {
            return null;
        }

        var numerator = ReadUInt32(value, offset, bigEndian);
        var denominator = ReadUInt32(value, offset + 4, bigEndian);

        if (denominator == 0)
        {
            return null;
        }

        return signed
            ? (double)(int)numerator / (int)denominator
            : (double)numerator / denominator;
    }

    /// <summary>
    /// Reads a GPS coordinate, stored as three rationals: degrees, minutes, seconds.
    /// </summary>
    /// <param name="value">The value bytes.</param>
    /// <param name="bigEndian">The byte order.</param>
    /// <returns>The unsigned angle in degrees, or <c>null</c>.</returns>
    private static double? ReadDegrees(ReadOnlySpan<byte> value, bool bigEndian)
    {
        var degrees = ReadRational(value, 0, bigEndian, signed: false);
        var minutes = ReadRational(value, 8, bigEndian, signed: false);
        var seconds = ReadRational(value, 16, bigEndian, signed: false);

        if (degrees is null || minutes is null || seconds is null)
        {
            return null;
        }

        return degrees.Value + (minutes.Value / 60) + (seconds.Value / 3600);
    }

    /// <summary>
    /// Reads a big- or little-endian 16-bit value.
    /// </summary>
    /// <param name="data">The buffer.</param>
    /// <param name="offset">Where to read.</param>
    /// <param name="bigEndian">The byte order.</param>
    /// <returns>The value.</returns>
    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset, bool bigEndian)
        => bigEndian
            ? (ushort)((data[offset] << 8) | data[offset + 1])
            : (ushort)((data[offset + 1] << 8) | data[offset]);

    /// <summary>
    /// Reads a big- or little-endian 32-bit value.
    /// </summary>
    /// <param name="data">The buffer.</param>
    /// <param name="offset">Where to read.</param>
    /// <param name="bigEndian">The byte order.</param>
    /// <returns>The value.</returns>
    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset, bool bigEndian)
        => bigEndian
            ? ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3]
            : ((uint)data[offset + 3] << 24) | ((uint)data[offset + 2] << 16) | ((uint)data[offset + 1] << 8) | data[offset];

    /// <summary>
    /// Walks one image file directory, recursing into the Exif and GPS sub-directories.
    /// </summary>
    /// <param name="tiff">The TIFF block.</param>
    /// <param name="bigEndian">The byte order.</param>
    /// <param name="ifdOffset">Offset of the directory within the block.</param>
    /// <param name="kind">Which directory this is.</param>
    /// <param name="depth">How many sub-directories deep we already are.</param>
    /// <remarks>
    /// Every exit here is a silent <c>return</c>, never a throw. A directory that runs past the end
    /// of the block is the ordinary outcome for a file whose EXIF is larger than the window read
    /// from it, and it should cost the tags not yet reached, not the ones already found.
    /// </remarks>
    private void ReadIfd(ReadOnlySpan<byte> tiff, bool bigEndian, int ifdOffset, IfdKind kind, int depth)
    {
        if (ifdOffset < 0 || ifdOffset + 2 > tiff.Length)
        {
            return;
        }

        var entries = ReadUInt16(tiff, ifdOffset, bigEndian);

        if (entries > MaxIfdEntries)
        {
            return;
        }

        for (var i = 0; i < entries; i++)
        {
            var entry = ifdOffset + 2 + (i * 12);

            if (entry + 12 > tiff.Length)
            {
                return;
            }

            var tag = ReadUInt16(tiff, entry, bigEndian);
            var type = ReadUInt16(tiff, entry + 2, bigEndian);
            var count = ReadUInt32(tiff, entry + 4, bigEndian);

            if (!TryReadValue(tiff, bigEndian, entry, type, count, out var value))
            {
                continue;
            }

            switch (kind)
            {
                case IfdKind.Root:
                    ReadRootTag(tiff, bigEndian, tag, value, depth);
                    break;

                case IfdKind.Exif:
                    ReadExifTag(tag, value, bigEndian);
                    break;

                case IfdKind.Gps:
                    ReadGpsTag(tag, value, bigEndian);
                    break;
            }
        }
    }

    /// <summary>
    /// Handles one tag of IFD0.
    /// </summary>
    /// <param name="tiff">The TIFF block, needed to follow the sub-directory pointers.</param>
    /// <param name="bigEndian">The byte order.</param>
    /// <param name="tag">The tag number.</param>
    /// <param name="value">The value bytes.</param>
    /// <param name="depth">The current recursion depth.</param>
    private void ReadRootTag(ReadOnlySpan<byte> tiff, bool bigEndian, ushort tag, ReadOnlySpan<byte> value, int depth)
    {
        switch (tag)
        {
            case 0x010F:
                CameraMake = ReadAscii(value);
                break;

            case 0x0110:
                CameraModel = ReadAscii(value);
                break;

            case 0x0131:
                Software = ReadAscii(value);
                break;

            case 0x0132:
                _dateTime = ReadDateTime(value);
                break;

            case 0x8769:
                ReadSubIfd(tiff, bigEndian, value, IfdKind.Exif, depth);
                break;

            case 0x8825:
                ReadSubIfd(tiff, bigEndian, value, IfdKind.Gps, depth);
                break;
        }
    }

    /// <summary>
    /// Follows a pointer to a sub-directory.
    /// </summary>
    /// <param name="tiff">The TIFF block.</param>
    /// <param name="bigEndian">The byte order.</param>
    /// <param name="value">The value bytes of the pointing entry.</param>
    /// <param name="kind">Which directory it points at.</param>
    /// <param name="depth">The depth of the directory the pointer was found in.</param>
    /// <remarks>
    /// The entry is supposed to be a single 32-bit offset, but nothing stops a file declaring it
    /// one byte wide, so its width is checked before it is read.
    /// </remarks>
    private void ReadSubIfd(ReadOnlySpan<byte> tiff, bool bigEndian, ReadOnlySpan<byte> value, IfdKind kind, int depth)
    {
        if (depth >= MaxIfdDepth || value.Length < 4)
        {
            return;
        }

        var offset = (long)ReadUInt32(value, 0, bigEndian);

        if (offset < 0 || offset + 2 > tiff.Length)
        {
            return;
        }

        ReadIfd(tiff, bigEndian, (int)offset, kind, depth + 1);
    }

    /// <summary>
    /// Handles one tag of the Exif sub-directory.
    /// </summary>
    /// <param name="tag">The tag number.</param>
    /// <param name="value">The value bytes.</param>
    /// <param name="bigEndian">The byte order.</param>
    private void ReadExifTag(ushort tag, ReadOnlySpan<byte> value, bool bigEndian)
    {
        switch (tag)
        {
            case 0x829A:
                ExposureTime = ReadRational(value, 0, bigEndian, signed: false);
                break;

            case 0x8827:
                IsoSpeedRating = value.Length >= 2 ? ReadUInt16(value, 0, bigEndian) : null;
                break;

            case 0x9003:
                _dateTimeOriginal = ReadDateTime(value);
                break;

            case 0x9004:
                _dateTimeDigitized = ReadDateTime(value);
                break;

            case 0x9201:
                ShutterSpeed = ReadRational(value, 0, bigEndian, signed: true);
                break;

            case 0x9202:
                Aperture = ReadRational(value, 0, bigEndian, signed: false);
                break;

            case 0x920A:
                FocalLength = ReadRational(value, 0, bigEndian, signed: false);
                break;
        }
    }

    /// <summary>
    /// Handles one tag of the GPS sub-directory.
    /// </summary>
    /// <param name="tag">The tag number.</param>
    /// <param name="value">The value bytes.</param>
    /// <param name="bigEndian">The byte order.</param>
    /// <remarks>
    /// The hemisphere lives in a tag of its own, and nothing guarantees it is read before the angle
    /// it applies to, so the two are kept apart until <see cref="Finish"/>.
    /// </remarks>
    private void ReadGpsTag(ushort tag, ReadOnlySpan<byte> value, bool bigEndian)
    {
        switch (tag)
        {
            case 0x0001:
                _latitudeRef = ReadAscii(value)?[0] ?? default;
                break;

            case 0x0002:
                _latitudeDegrees = ReadDegrees(value, bigEndian);
                break;

            case 0x0003:
                _longitudeRef = ReadAscii(value)?[0] ?? default;
                break;

            case 0x0004:
                _longitudeDegrees = ReadDegrees(value, bigEndian);
                break;

            case 0x0005:
                _altitudeRef = value.Length >= 1 ? value[0] : (byte)0;
                break;

            case 0x0006:
                _altitudeMetres = ReadRational(value, 0, bigEndian, signed: false);
                break;
        }
    }

    /// <summary>
    /// Resolves the values that depend on more than one tag, once every tag has been seen.
    /// </summary>
    /// <remarks>
    /// A coordinate with no hemisphere is dropped rather than guessed. Half of this library was
    /// shot at a western longitude, so defaulting a missing reference to the positive direction
    /// would place those photos in the wrong hemisphere without anything looking wrong.
    /// </remarks>
    private void Finish()
    {
        DateTaken = _dateTimeOriginal ?? _dateTimeDigitized ?? _dateTime;

        if (_latitudeDegrees is not null && (_latitudeRef == 'N' || _latitudeRef == 'S'))
        {
            Latitude = _latitudeRef == 'S' ? -_latitudeDegrees : _latitudeDegrees;
        }

        if (_longitudeDegrees is not null && (_longitudeRef == 'E' || _longitudeRef == 'W'))
        {
            Longitude = _longitudeRef == 'W' ? -_longitudeDegrees : _longitudeDegrees;
        }

        if (_altitudeMetres is not null)
        {
            // GPSAltitudeRef is a flag, not a unit: 1 means the value is a depth below sea level.
            Altitude = _altitudeRef == 1 ? -_altitudeMetres : _altitudeMetres;
        }
    }
}
