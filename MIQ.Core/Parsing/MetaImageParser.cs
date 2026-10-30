using System.Globalization;
using System.Text;

namespace MIQ.Parsing;

/// <summary>
/// ITK MetaImage (<c>.mha</c>) parser.
/// Direct port of MIQCore's <c>MIQParser+MetaImage.swift</c>.
///
/// A text header of <c>Key = Value</c> lines that ends with
/// <c>ElementDataFile = LOCAL</c>, followed by the voxels in the same file — raw,
/// or a zlib stream when <c>CompressedData = True</c>. A detached <c>.mhd</c> names
/// its data file instead (<c>.raw</c>, or <c>.zraw</c> when compressed), read through
/// the loader <see cref="DetachedData"/> supplies (Windows-only: macOS Quick Look
/// grants access to the selected file only).
/// </summary>
public static class MetaImageParser
{
    private sealed class MetaImageParsedHeader
    {
        /// `DimSize`, fastest axis first. Two or three spatial axes, optionally a fourth.
        public required int[] Sizes { get; init; }
        /// `ElementNumberOfChannels`; channels are interleaved per voxel.
        public required int Channels { get; init; }
        /// Scalar type of one channel.
        public required MiqDatatype ElementType { get; init; }
        public required bool LittleEndian { get; init; }
        public required bool Compressed { get; init; }
        public long? CompressedSize { get; init; }
        public float[]? Spacing { get; init; }
        public float[]? Origin { get; init; }
        /// `TransformMatrix`, NDims × NDims. Axis i's direction in ITK's LPS world
        /// is the i-th run of NDims values (ITK's MetaImageIO reads and writes it so).
        public float[]? Transform { get; init; }
        /// The `Comment` field.
        public string? Comment { get; init; }
        /// `ElementDataFile` of a detached header; null when LOCAL.
        public string? DataFile { get; init; }
        /// `HeaderSize`: bytes to skip in a detached data file (-1 = take the tail).
        public long? HeaderSize { get; init; }
    }

    // ── Public entry point ───────────────────────────────────────────────────

    /// <param name="loadDataFile">Set for a detached <c>.mhd</c> header: reads the
    /// file `ElementDataFile` names. Null for <c>.mha</c>, which must be LOCAL.</param>
    public static MiqImage Parse(byte[] data, Func<string, byte[]>? loadDataFile = null)
    {
        var (fields, payloadIndex) = SplitHeader(data);
        var meta = BuildParsedHeader(fields, detached: loadDataFile is not null);

        // Bounded by ValidateDimensionExtent in BuildParsedHeader.
        long payloadBytes = meta.ElementType.BytesPerVoxel() * (long)meta.Channels;
        foreach (var s in meta.Sizes) payloadBytes *= s;
        if (payloadBytes <= 0) throw MiqException.InvalidDimensions();

        if (meta.DataFile is { } dataFile)
        {
            data = loadDataFile!(dataFile);
            payloadIndex = DetachedPayloadIndex(meta, data.Length, payloadBytes);
        }

        byte[] storage;
        int payloadOffset;
        if (meta.Compressed)
        {
            // The decompressed payload is the whole Storage buffer.
            if (payloadBytes > MiqParser.MaxArrayBytes)
                throw new MiqException("MetaImage: compressed volume is too large to decompress.");
            var length = data.Length - payloadIndex;
            if (meta.CompressedSize is { } size && size > 0 && size < length)
                length = (int)size;
            var compressed = new byte[length];
            Array.Copy(data, payloadIndex, compressed, 0, length);
            storage = MiqBinaryReader.InflateZlib(compressed, (int)payloadBytes);
            payloadOffset = 0;
        }
        else
        {
            storage = data;
            payloadOffset = payloadIndex;
        }
        // Subtracting on the left keeps the comparison overflow-free.
        if (storage.Length - (long)payloadOffset < payloadBytes) throw MiqException.TruncatedData();

        return new MiqImage
        {
            Header = BuildMiqHeader(meta, payloadOffset),
            Storage = storage,
            PayloadOffset = payloadOffset,
            ElementStrides = Strides(meta),
        };
    }

    /// Where the voxels start in a detached data file. `HeaderSize` skips that many
    /// bytes, and -1 means "the voxels are the file's tail". A compressed file is a
    /// zlib stream from its first byte.
    private static int DetachedPayloadIndex(MetaImageParsedHeader meta, int dataLength, long payloadBytes)
    {
        if (meta.Compressed) return 0;
        var skip = meta.HeaderSize ?? 0;
        if (skip == -1)
        {
            if (dataLength < payloadBytes) throw MiqException.TruncatedData();
            return (int)(dataLength - payloadBytes);
        }
        if (skip < 0 || skip > dataLength) throw MiqException.TruncatedData();
        return (int)skip;
    }

    /// The `ElementDataFile` value of a header, or null when it is LOCAL or missing.
    /// Lets the plugin check that a <c>.raw</c> belongs to a sibling <c>.mhd</c>.
    internal static string? DataFileName(byte[] header)
    {
        var (fields, _) = SplitHeader(header);
        return fields.TryGetValue("ElementDataFile", out var name)
               && !name.Equals("LOCAL", StringComparison.OrdinalIgnoreCase)
               && !DetachedData.IsMultiFile(name)
            ? name
            : null;
    }

    // ── Header split ─────────────────────────────────────────────────────────

    /// How far the header may run. Real headers are a few hundred bytes; the cap
    /// keeps a file that isn't MetaImage from being scanned in full.
    internal const int HeaderProbeBytes = 64 * 1024;

    /// Collects `Key = Value` fields (keys case-insensitive) up to and including the
    /// `ElementDataFile` line, which MetaImage always writes last, and returns the
    /// index right after it, where the payload begins. Walking line by line stops
    /// there, so the payload is never scanned.
    private static (Dictionary<string, string> fields, int payloadIndex) SplitHeader(byte[] data)
    {
        var limit = Math.Min(data.Length, HeaderProbeBytes);
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lineStart = 0;
        while (lineStart < limit)
        {
            var newline = Array.IndexOf(data, (byte)0x0A, lineStart, limit - lineStart);
            // A line cut off by the probe limit (not by the end of the file) is incomplete.
            if (newline < 0 && limit < data.Length) break;
            var lineEnd = newline >= 0 ? newline : limit;
            var next = newline >= 0 ? newline + 1 : limit;
            var line = Encoding.UTF8.GetString(data, lineStart, lineEnd - lineStart);
            var equals = line.IndexOf('=');
            if (equals >= 0)
            {
                var key = line.Substring(0, equals).Trim();
                fields[key] = line.Substring(equals + 1).Trim();
                if (string.Equals(key, "ElementDataFile", StringComparison.OrdinalIgnoreCase))
                    return (fields, next);
            }
            lineStart = next;
        }
        throw new MiqException(
            "MetaImage header has no ElementDataFile line; file does not appear to be MetaImage format.");
    }

    // ── Header fields ────────────────────────────────────────────────────────

    private static MetaImageParsedHeader BuildParsedHeader(Dictionary<string, string> fields, bool detached)
    {
        if (fields.TryGetValue("ObjectType", out var objectType) &&
            !objectType.Equals("Image", StringComparison.OrdinalIgnoreCase))
            throw new MiqException($"MetaImage object type '{objectType}' is not an image.");

        var dataFile = fields.TryGetValue("ElementDataFile", out var df) ? df : "";
        var isLocal = dataFile.Equals("LOCAL", StringComparison.OrdinalIgnoreCase);
        if (!isLocal && !detached)
            throw new MiqException(
                $"MetaImage with a separate data file (ElementDataFile = {dataFile}) is not supported; use a self-contained .mha file.");
        if (!isLocal && DetachedData.IsMultiFile(dataFile))
            throw new MiqException(
                $"MetaImage split across several data files (ElementDataFile = {dataFile}) is not supported.");
        long? headerSize = fields.TryGetValue("HeaderSize", out var hs) &&
            long.TryParse(hs, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hsv) ? hsv : null;
        if (fields.TryGetValue("BinaryData", out var binary) && !Bool(binary))
            throw new MiqException("MetaImage ASCII data (BinaryData = False) is not supported.");

        if (!fields.TryGetValue("DimSize", out var sizesStr))
            throw new MiqException("MetaImage header is missing required field 'DimSize'.");
        var sizes = ParseIntList(sizesStr, "DimSize");
        if (sizes.Any(s => s <= 0)) throw MiqException.InvalidDimensions();
        if (fields.TryGetValue("NDims", out var ndimsStr) &&
            int.TryParse(ndimsStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ndims) &&
            ndims != sizes.Length)
            throw new MiqException($"MetaImage NDims = {ndims} but DimSize lists {sizes.Length} values.");
        if (sizes.Length < 2 || sizes.Length > 4)
            throw new MiqException(
                $"MetaImage with {sizes.Length} dimensions is not supported (2 to 4 are).");

        var channels = fields.TryGetValue("ElementNumberOfChannels", out var ch)
            ? ParseIntList(ch, "ElementNumberOfChannels")[0]
            : 1;
        if (channels < 1) throw MiqException.InvalidDimensions();
        if (channels > 1 && sizes.Length == 4)
            throw new MiqException("4D MetaImage with more than one channel per voxel is not supported.");

        if (!fields.TryGetValue("ElementType", out var typeStr))
            throw new MiqException("MetaImage header is missing required field 'ElementType'.");
        var elementType = ParseElementType(typeStr);
        MiqParser.ValidateDimensionExtent([.. sizes, channels], elementType.BytesPerVoxel());
        MiqParser.ValidateSlicePlaneExtent(sizes[0], sizes[1], sizes.Length > 2 ? sizes[2] : 1);

        var msb = fields.TryGetValue("BinaryDataByteOrderMSB", out var m) ? m
            : fields.TryGetValue("ElementByteOrderMSB", out m) ? m : null;
        long? compressedSize = fields.TryGetValue("CompressedDataSize", out var cs) &&
            long.TryParse(cs, NumberStyles.Integer, CultureInfo.InvariantCulture, out var csv) ? csv : null;
        var spacing = FirstFloatList(fields, "ElementSpacing", "ElementSize");
        var origin = FirstFloatList(fields, "Offset", "Position", "Origin");
        var transform = FirstFloatList(fields, "TransformMatrix", "Rotation", "Orientation");
        if (transform is not null && transform.Length != sizes.Length * sizes.Length) transform = null;

        return new MetaImageParsedHeader
        {
            Sizes = sizes,
            Channels = channels,
            ElementType = elementType,
            LittleEndian = !Bool(msb),
            Compressed = Bool(fields.TryGetValue("CompressedData", out var cd) ? cd : null),
            CompressedSize = compressedSize,
            Spacing = spacing,
            Origin = origin,
            Transform = transform,
            Comment = fields.TryGetValue("Comment", out var comment) ? comment : null,
            DataFile = isLocal ? null : dataFile,
            HeaderSize = headerSize,
        };
    }

    // ── Image construction ───────────────────────────────────────────────────

    /// Three uint8 channels are RGB and four RGBA, which render in colour as stored
    /// (interleaved per voxel). Any other multi-channel image (a displacement
    /// field, say) shows its channels as volumes.
    private static MiqDatatype? RendersAsColor(MetaImageParsedHeader meta)
    {
        if (meta.ElementType != MiqDatatype.Uint8) return null;
        return meta.Channels switch
        {
            3 => MiqDatatype.Rgb24,
            4 => MiqDatatype.Rgba32,
            _ => null,
        };
    }

    /// Channels shown as volumes are the fastest axis, so the spatial axes step
    /// over them; every other layout is the default x-fastest one.
    private static int[]? Strides(MetaImageParsedHeader meta)
    {
        if (meta.Channels <= 1 || RendersAsColor(meta) is not null) return null;
        var c = meta.Channels;
        var sx = meta.Sizes[0];
        var sy = meta.Sizes[1];
        return [c, c * sx, c * sx * sy, 1];
    }

    private static MiqHeader BuildMiqHeader(MetaImageParsedHeader meta, int payloadOffset)
    {
        var sx = meta.Sizes[0];
        var sy = meta.Sizes[1];
        var sz = meta.Sizes.Length > 2 ? meta.Sizes[2] : 1;
        var color = RendersAsColor(meta);
        var nVols = color is null && meta.Channels > 1
            ? meta.Channels
            : meta.Sizes.Length > 3 ? meta.Sizes[3] : 1;

        var spacing = new float[3];
        for (var i = 0; i < 3; i++)
            spacing[i] = Math.Abs(meta.Spacing is { } sp && i < sp.Length ? sp[i] : 1f);

        var srowX = new float[4];
        var srowY = new float[4];
        var srowZ = new float[4];
        OrientationFrame? orientationFrame = null;
        // A 2-D image has no third direction to place, so only 3-D/4-D get a frame;
        // `AnatomicalOrientation` is informational and ignored, as ITK does on read.
        if (meta.Transform is { } transform && meta.Sizes.Length >= 3)
        {
            var n = meta.Sizes.Length;
            // LPS → RAS flips the first two world components.
            float[] toRas = [-1, -1, 1];
            float[][] rows = [srowX, srowY, srowZ];
            for (var world = 0; world < 3; world++)
            {
                for (var axis = 0; axis < 3; axis++)
                    rows[world][axis] = transform[axis * n + world] * spacing[axis] * toRas[world];
                var o = meta.Origin is { } origin && world < origin.Length ? origin[world] : 0f;
                rows[world][3] = o * toRas[world];
            }
            orientationFrame = OrientationFrame.From(srowX, srowY, srowZ);
        }

        return new MiqHeader
        {
            LittleEndian = meta.LittleEndian,
            Dimensions = new[] { sx, sy, sz, nVols },
            Pixdim = new[] { 1f, spacing[0], spacing[1], spacing[2] },
            Datatype = color ?? meta.ElementType,
            VoxOffset = payloadOffset,
            SclSlope = 1f,
            SclInter = 0f,
            QformCode = 0,
            SformCode = orientationFrame is not null ? 1 : 0,
            SrowX = srowX,
            SrowY = srowY,
            SrowZ = srowZ,
            FormatLabel = FormatLabel(meta),
            OrientationFrame = orientationFrame,
            Comment = meta.Comment is { } c ? MiqParser.HeaderComment([c]) : null,
        };
    }

    /// Null keeps the file kind's name ("MetaImage").
    private static string? FormatLabel(MetaImageParsedHeader meta)
    {
        var detached = meta.DataFile is not null;
        if (meta.Compressed) return detached ? "Compressed MetaImage (detached)" : "Compressed MetaImage";
        return detached ? "MetaImage (detached)" : null;
    }

    // ── Field value parsers ──────────────────────────────────────────────────

    private static bool Bool(string? value) =>
        value is not null &&
        (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1");

    /// `MET_LONG`/`MET_ULONG` are 4 bytes in MetaIO (`MET_ValueTypeSize`), not the
    /// platform `long`; ITK writes 64-bit integers as `MET_LONG_LONG`.
    private static MiqDatatype ParseElementType(string value) =>
        value.Trim().ToUpperInvariant() switch
        {
            "MET_CHAR" => MiqDatatype.Int8,
            "MET_UCHAR" => MiqDatatype.Uint8,
            "MET_SHORT" => MiqDatatype.Int16,
            "MET_USHORT" => MiqDatatype.Uint16,
            "MET_INT" or "MET_LONG" => MiqDatatype.Int32,
            "MET_UINT" or "MET_ULONG" => MiqDatatype.Uint32,
            "MET_LONG_LONG" => MiqDatatype.Int64,
            "MET_ULONG_LONG" => MiqDatatype.Uint64,
            "MET_FLOAT" => MiqDatatype.Float32,
            "MET_DOUBLE" => MiqDatatype.Float64,
            _ => throw new MiqException($"MetaImage element type '{value}' is not supported."),
        };

    private static int[] ParseIntList(string value, string field)
    {
        var parts = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var result = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out result[i]))
                throw new MiqException($"MetaImage '{field}' contains a non-integer value: '{value}'.");
        if (result.Length == 0)
            throw new MiqException($"MetaImage '{field}' contains a non-integer value: '{value}'.");
        return result;
    }

    /// The first of <paramref name="keys"/> present (MetaIO accepts aliases), parsed
    /// leniently: a malformed optional field yields null rather than failing the parse.
    private static float[]? FirstFloatList(Dictionary<string, string> fields, params string[] keys)
    {
        foreach (var key in keys)
            if (fields.TryGetValue(key, out var value)) return ParseFloatList(value);
        return null;
    }

    private static float[]? ParseFloatList(string value)
    {
        var parts = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        var result = new float[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out result[i]))
                return null;
        return result;
    }
}
