using System.Globalization;
using System.Text;

namespace MIQ.Parsing;

/// <summary>
/// NRRD parser (single-file <c>.nrrd</c> only).
/// Direct port of MIQCore's <c>MIQParser+NRRD.swift</c>.
///
/// An NRRD file is an ASCII header (magic line <c>NRRD000x</c> followed by
/// <c>key: value</c> fields) terminated by a blank line, then binary voxel data.
/// Data may be stored <c>raw</c> or <c>gzip</c>-compressed in the payload segment
/// (this is independent of any file-level gzip — a plain <c>.nrrd</c> is never
/// whole-file compressed). Detached headers (<c>.nhdr</c> / <c>data file:</c>) are
/// out of scope.
/// </summary>
public static class NrrdParser
{
    private enum NrrdEncoding { Raw, Gzip }

    private sealed class NrrdParsedHeader
    {
        public required int[] Sizes { get; init; }
        public required MiqDatatype Datatype { get; init; }
        public required bool LittleEndian { get; init; }
        public required NrrdEncoding Encoding { get; init; }
        /// Per-axis direction vectors in NRRD space; null entry = non-spatial ("none").
        /// Null array = field absent entirely.
        public float[]?[]? SpaceDirections { get; init; }
        public float[]? SpaceOrigin { get; init; }
        /// Multipliers converting NRRD (x,y,z) components to RAS (R,A,S).
        public required (float rx, float ry, float rz) ToRasSigns { get; init; }
        /// Fallback voxel spacings when SpaceDirections is absent.
        public float[]? Spacings { get; init; }
    }

    // ── Public entry points ──────────────────────────────────────────────────

    public static MiqImage Parse(byte[] data, string? formatLabel = null)
    {
        var (nrrd, storage, payloadOffset) = LoadNrrd(data);
        return BuildImage(nrrd, storage, payloadOffset);
    }

    public static MiqHeader ParseHeader(byte[] data)
    {
        var (nrrd, _, payloadOffset) = LoadNrrd(data);
        return BuildMiqHeader(nrrd, payloadOffset).Header;
    }

    // ── Loading ──────────────────────────────────────────────────────────────

    private static (NrrdParsedHeader nrrd, byte[] storage, int payloadOffset) LoadNrrd(byte[] data)
    {
        var (headerText, payloadIndex) = SplitHeader(data);
        var lines = headerText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        if (lines.Length == 0 || !lines[0].StartsWith("NRRD000", StringComparison.Ordinal))
            throw new MiqException(
                "NRRD magic line not found; file does not appear to be NRRD format.");

        var fields = ParseFields(lines);
        var nrrd = BuildParsedHeader(fields);

        switch (nrrd.Encoding)
        {
            case NrrdEncoding.Raw:
                return (nrrd, data, payloadIndex);

            case NrrdEncoding.Gzip:
                var compressed = new byte[data.Length - payloadIndex];
                Array.Copy(data, payloadIndex, compressed, 0, compressed.Length);
                if (!MiqBinaryReader.IsLikelyGzip(compressed))
                    throw new MiqException(
                        "NRRD encoding is gzip but gzip magic bytes are missing in payload.");
                return (nrrd, MiqBinaryReader.Gunzip(compressed), 0);

            default:
                throw new MiqException("NRRD: unknown encoding.");
        }
    }

    // Returns the header text and the byte offset where the payload begins,
    // splitting on the first blank line (CRLF preferred, then LF).
    private static (string headerText, int payloadIndex) SplitHeader(byte[] data)
    {
        var crlf = IndexOf(data, new byte[] { 0x0D, 0x0A, 0x0D, 0x0A });
        if (crlf >= 0)
            return (Encoding.UTF8.GetString(data, 0, crlf), crlf + 4);

        var lf = IndexOf(data, new byte[] { 0x0A, 0x0A });
        if (lf >= 0)
            return (Encoding.UTF8.GetString(data, 0, lf), lf + 2);

        throw new MiqException(
            "NRRD header is missing the blank-line separator; detached headers (.nhdr) are not supported.");
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
                if (haystack[i + j] != needle[j]) { match = false; break; }
            if (match) return i;
        }
        return -1;
    }

    // ── Header field parsing ─────────────────────────────────────────────────

    private static Dictionary<string, string> ParseFields(string[] lines)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith("#", StringComparison.Ordinal)) continue;

            var colon = line.IndexOf(':');
            if (colon < 0 || colon + 1 >= line.Length) continue;
            // Skip NRRD custom key-value pairs (key:=value); only standard fields.
            if (line[colon + 1] == '=') continue;

            var key = line.Substring(0, colon).Trim().ToLowerInvariant();
            var value = line.Substring(colon + 1).Trim();
            fields[key] = value;
        }
        return fields;
    }

    private static NrrdParsedHeader BuildParsedHeader(Dictionary<string, string> fields)
    {
        // Reject detached headers early.
        if (fields.TryGetValue("data file", out var dataFile) ||
            fields.TryGetValue("datafile", out dataFile))
            throw new MiqException(
                $"NRRD detached header (data file: {dataFile}) is not supported; use a self-contained .nrrd file.");

        var encodingStr = (fields.TryGetValue("encoding", out var e) ? e : "raw").ToLowerInvariant();
        var encoding = encodingStr switch
        {
            "raw" => NrrdEncoding.Raw,
            "gzip" or "gz" => NrrdEncoding.Gzip,
            "ascii" or "text" or "txt" => throw new MiqException(
                "NRRD ASCII encoding is not supported; re-save with encoding: raw or encoding: gzip."),
            "hex" => throw new MiqException(
                "NRRD hex encoding is not supported; re-save with encoding: raw or encoding: gzip."),
            "bzip2" or "bz2" => throw new MiqException(
                "NRRD bzip2 encoding is not supported; re-save with encoding: raw or encoding: gzip."),
            _ => throw new MiqException($"Unknown NRRD encoding '{encodingStr}'."),
        };

        if (!fields.TryGetValue("sizes", out var sizesStr))
            throw new MiqException("NRRD header is missing required field 'sizes'.");
        var sizes = ParseIntList(sizesStr, "sizes");
        if (sizes.Length == 0 || sizes.Any(s => s <= 0))
            throw MiqException.InvalidDimensions();

        if (!fields.TryGetValue("type", out var typeStr))
            throw new MiqException("NRRD header is missing required field 'type'.");
        var datatype = ParseDatatype(typeStr);

        var endianStr = (fields.TryGetValue("endian", out var en) ? en : "little").ToLowerInvariant();
        var littleEndian = endianStr != "big";

        var toRasSigns = SpaceToRasSigns(fields.TryGetValue("space", out var sp) ? sp.ToLowerInvariant() : null);

        var spaceDirections = fields.TryGetValue("space directions", out var sd)
            ? ParseSpaceDirections(sd, sizes.Length)
            : null;
        var spaceOrigin = fields.TryGetValue("space origin", out var so) ? ParseVector(so) : null;
        var spacings = fields.TryGetValue("spacings", out var spc) ? ParseSpacingList(spc) : null;

        return new NrrdParsedHeader
        {
            Sizes = sizes,
            Datatype = datatype,
            LittleEndian = littleEndian,
            Encoding = encoding,
            SpaceDirections = spaceDirections,
            SpaceOrigin = spaceOrigin,
            ToRasSigns = toRasSigns,
            Spacings = spacings,
        };
    }

    // ── Image construction ───────────────────────────────────────────────────

    private readonly struct NrrdImageDescriptor
    {
        public MiqHeader Header { get; }
        public int[]? Strides { get; }
        public NrrdImageDescriptor(MiqHeader header, int[]? strides)
        {
            Header = header;
            Strides = strides;
        }
    }

    private static NrrdImageDescriptor BuildMiqHeader(NrrdParsedHeader nrrd, int payloadOffset)
    {
        var (spatialAxes, volumeAxis) = AxisLayout(nrrd);

        var sx = nrrd.Sizes[spatialAxes[0]];
        var sy = nrrd.Sizes[spatialAxes[1]];
        var sz = nrrd.Sizes[spatialAxes[2]];
        var nVols = volumeAxis.HasValue ? nrrd.Sizes[volumeAxis.Value] : 1;

        // Raw element strides (each axis varies faster than the next).
        var rawStrides = new int[nrrd.Sizes.Length];
        var stride = 1;
        for (var i = 0; i < nrrd.Sizes.Length; i++)
        {
            rawStrides[i] = stride;
            stride *= nrrd.Sizes[i];
        }

        var xStride = rawStrides[spatialAxes[0]];
        var yStride = rawStrides[spatialAxes[1]];
        var zStride = rawStrides[spatialAxes[2]];
        var tStride = volumeAxis.HasValue ? rawStrides[volumeAxis.Value] : sx * sy * sz;

        // Only store custom strides when they differ from the default x-fastest layout.
        var defaultStrides = new[] { 1, sx, sx * sy, sx * sy * sz };
        var computedStrides = new[] { xStride, yStride, zStride, tStride };
        var customStrides = computedStrides.SequenceEqual(defaultStrides) ? null : computedStrides;

        var (pixX, pixY, pixZ, srowX, srowY, srowZ) = Affine(nrrd, spatialAxes);
        var hasSform = srowX.Take(3).Any(v => v != 0f)
                       || srowY.Take(3).Any(v => v != 0f)
                       || srowZ.Take(3).Any(v => v != 0f);

        var orientationFrame = hasSform ? OrientationFrame.From(srowX, srowY, srowZ) : null;

        var header = new MiqHeader
        {
            LittleEndian = nrrd.LittleEndian,
            Dimensions = new[] { sx, sy, sz, nVols },
            Pixdim = new[] { 1f, pixX, pixY, pixZ },
            Datatype = nrrd.Datatype,
            VoxOffset = payloadOffset,
            SclSlope = 1f,
            SclInter = 0f,
            QformCode = 0,
            SformCode = hasSform ? 1 : 0,
            SrowX = srowX,
            SrowY = srowY,
            SrowZ = srowZ,
            FormatLabel = nrrd.Encoding == NrrdEncoding.Gzip ? "Compressed NRRD" : null,
            OrientationFrame = orientationFrame,
        };

        return new NrrdImageDescriptor(header, customStrides);
    }

    private static MiqImage BuildImage(NrrdParsedHeader nrrd, byte[] storage, int payloadOffset)
    {
        long totalElements = 1;
        foreach (var s in nrrd.Sizes) totalElements *= s;
        var payloadBytes = totalElements * nrrd.Datatype.BytesPerVoxel();
        if (payloadBytes <= 0) throw MiqException.InvalidDimensions();
        if (storage.Length < payloadOffset + payloadBytes) throw MiqException.TruncatedData();

        var descriptor = BuildMiqHeader(nrrd, payloadOffset);

        return new MiqImage
        {
            Header = descriptor.Header,
            Storage = storage,
            PayloadOffset = payloadOffset,
            ElementStrides = descriptor.Strides,
        };
    }

    // ── Axis layout ──────────────────────────────────────────────────────────

    private static (int[] spatialAxes, int? volumeAxis) AxisLayout(NrrdParsedHeader nrrd)
    {
        var nAxes = nrrd.Sizes.Length;

        if (nrrd.SpaceDirections is not { } dirs)
        {
            // No space directions: assume the first 3 axes are spatial.
            if (nAxes < 3) throw MiqException.InvalidDimensions();
            if (nAxes > 4)
                throw new MiqException("NRRD files with more than 4 dimensions are not supported.");
            return (new[] { 0, 1, 2 }, nAxes == 4 ? 3 : (int?)null);
        }

        var spatialAxes = new List<int>();
        var nonSpatialAxes = new List<int>();
        for (var i = 0; i < dirs.Length; i++)
        {
            if (dirs[i] != null) spatialAxes.Add(i);
            else nonSpatialAxes.Add(i);
        }

        if (spatialAxes.Count != 3)
            throw new MiqException(
                $"NRRD file does not appear to be a previewable volume (found {spatialAxes.Count} spatial axes, expected 3).");
        if (nonSpatialAxes.Count > 1)
            throw new MiqException("NRRD files with multiple non-spatial axes are not supported.");

        return (spatialAxes.ToArray(), nonSpatialAxes.Count > 0 ? nonSpatialAxes[0] : (int?)null);
    }

    // ── Affine ───────────────────────────────────────────────────────────────

    private static (float pixX, float pixY, float pixZ, float[] srowX, float[] srowY, float[] srowZ)
        Affine(NrrdParsedHeader nrrd, int[] spatialAxes)
    {
        var (rx, ry, rz) = nrrd.ToRasSigns;

        if (nrrd.SpaceDirections is { } dirs)
        {
            var v0 = dirs[spatialAxes[0]] ?? new[] { 1f, 0f, 0f };
            var v1 = dirs[spatialAxes[1]] ?? new[] { 0f, 1f, 0f };
            var v2 = dirs[spatialAxes[2]] ?? new[] { 0f, 0f, 1f };

            var pixX = (float)Math.Sqrt(v0[0] * v0[0] + v0[1] * v0[1] + v0[2] * v0[2]);
            var pixY = (float)Math.Sqrt(v1[0] * v1[0] + v1[1] * v1[1] + v1[2] * v1[2]);
            var pixZ = (float)Math.Sqrt(v2[0] * v2[0] + v2[1] * v2[1] + v2[2] * v2[2]);

            var ox = Safe(nrrd.SpaceOrigin, 0) * rx;
            var oy = Safe(nrrd.SpaceOrigin, 1) * ry;
            var oz = Safe(nrrd.SpaceOrigin, 2) * rz;

            // srowX[i] = R component of storage axis i's world direction;
            // srowY[i] = A component, srowZ[i] = S component.
            var srowX = new[] { v0[0] * rx, v1[0] * rx, v2[0] * rx, ox };
            var srowY = new[] { v0[1] * ry, v1[1] * ry, v2[1] * ry, oy };
            var srowZ = new[] { v0[2] * rz, v1[2] * rz, v2[2] * rz, oz };

            return (pixX, pixY, pixZ, srowX, srowY, srowZ);
        }

        // Fallback: use spacings if available, identity orientation.
        var px = Math.Abs(SafeOr(nrrd.Spacings, spatialAxes[0], 1f));
        var py = Math.Abs(SafeOr(nrrd.Spacings, spatialAxes[1], 1f));
        var pz = Math.Abs(SafeOr(nrrd.Spacings, spatialAxes[2], 1f));

        return (px, py, pz,
            new[] { px * rx, 0f, 0f, 0f },
            new[] { 0f, py * ry, 0f, 0f },
            new[] { 0f, 0f, pz * rz, 0f });
    }

    private static float Safe(float[]? a, int i) => a != null && i < a.Length ? a[i] : 0f;
    private static float SafeOr(float[]? a, int i, float fallback) =>
        a != null && i < a.Length ? a[i] : fallback;

    private static (float rx, float ry, float rz) SpaceToRasSigns(string? space) => space switch
    {
        "right-anterior-superior" or "ras" => (1, 1, 1),
        "left-anterior-superior" or "las" => (-1, 1, 1),
        "left-posterior-superior" or "lps" => (-1, -1, 1),
        "right-posterior-superior" or "rps" => (1, -1, 1),
        "right-anterior-inferior" or "rai" => (1, 1, -1),
        "left-anterior-inferior" or "lai" => (-1, 1, -1),
        "left-posterior-inferior" or "lpi" => (-1, -1, -1),
        "right-posterior-inferior" or "rpi" => (1, -1, -1),
        _ => (1, 1, 1),
    };

    // ── Field value parsers ──────────────────────────────────────────────────

    private static MiqDatatype ParseDatatype(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "int8" or "int8_t" or "signed char" => MiqDatatype.Int8,
            "uint8" or "uint8_t" or "uchar" or "unsigned char" => MiqDatatype.Uint8,
            "int16" or "int16_t" or "short" or "short int"
                or "signed short" or "signed short int" => MiqDatatype.Int16,
            "uint16" or "uint16_t" or "ushort"
                or "unsigned short" or "unsigned short int" => MiqDatatype.Uint16,
            "int32" or "int32_t" or "int" or "signed int" => MiqDatatype.Int32,
            "uint32" or "uint32_t" or "uint" or "unsigned int" => MiqDatatype.Uint32,
            "float" => MiqDatatype.Float32,
            "double" => MiqDatatype.Float64,
            "int64" or "int64_t" or "longlong" or "long long"
                or "signed long long" or "signed long long int"
                or "uint64" or "uint64_t" or "ulonglong"
                or "unsigned long long" or "unsigned long long int" => throw new MiqException(
                    "NRRD 64-bit integer types are not supported; convert to float or int32 before previewing."),
            _ => throw new MiqException($"Unrecognised NRRD type '{value}'."),
        };

    private static int[] ParseIntList(string value, string field)
    {
        var parts = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var result = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out result[i]))
                throw new MiqException($"NRRD '{field}' contains a non-integer value: '{value}'.");
        if (result.Length == 0)
            throw new MiqException($"NRRD '{field}' contains a non-integer value: '{value}'.");
        return result;
    }

    private static float[]? ParseSpacingList(string value)
    {
        var parts = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        var result = new float[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out result[i]))
                return null;
        return result;
    }

    /// Parses `space directions` into per-axis direction vectors; null entries are "none".
    private static float[]?[] ParseSpaceDirections(string value, int axisCount)
    {
        var result = new List<float[]?>();
        var cursor = value.AsSpan();
        while (result.Count < axisCount)
        {
            cursor = cursor.TrimStart();
            if (cursor.IsEmpty) break;
            if (cursor.StartsWith("none".AsSpan(), StringComparison.Ordinal))
            {
                result.Add(null);
                cursor = cursor.Slice(4);
            }
            else if (TryParseParenthesisedFloats(cursor, out var components, out var consumed))
            {
                result.Add(components.Length == 3 ? components : null);
                cursor = cursor.Slice(consumed);
            }
            else
            {
                // Skip the next whitespace-delimited token.
                var ws = 0;
                while (ws < cursor.Length && !char.IsWhiteSpace(cursor[ws])) ws++;
                cursor = cursor.Slice(ws);
            }
        }
        while (result.Count < axisCount) result.Add(null);
        return result.ToArray();
    }

    /// Parses a parenthesised vector like `(x, y, z)`, returning its first 3 floats.
    private static float[]? ParseVector(string value)
    {
        if (!TryParseParenthesisedFloats(value.Trim().AsSpan(), out var components, out _))
            return null;
        return components.Length >= 3 ? components.Take(3).ToArray() : null;
    }

    /// Parses the comma-separated floats inside a leading `(...)` group. Returns the
    /// parsed components (no arity enforcement) and the number of chars consumed up
    /// to and including the closing `)`. False when the span doesn't begin with a
    /// closed `(...)` group.
    private static bool TryParseParenthesisedFloats(
        ReadOnlySpan<char> value, out float[] components, out int consumed)
    {
        components = Array.Empty<float>();
        consumed = 0;
        if (value.IsEmpty || value[0] != '(') return false;
        var close = value.IndexOf(')');
        if (close < 0) return false;

        var inner = value.Slice(1, close - 1).ToString();
        var parts = inner.Split(',');
        var list = new List<float>();
        foreach (var p in parts)
            if (float.TryParse(p.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
                list.Add(f);

        components = list.ToArray();
        consumed = close + 1;
        return true;
    }
}
