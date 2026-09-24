using System.Globalization;
using System.Text;

namespace MIQ.Parsing;

/// <summary>
/// MRtrix MIF/MIF.GZ parser.
/// Direct port of MIQCore's <c>MIQParser+MIF.swift</c> / <c>MIFAxisLayout.swift</c>.
///
/// MIF files are ASCII key-value pairs terminated by an "END" line, followed by
/// binary voxel data at the byte offset given in the <c>file</c> field.
/// The <c>layout</c> field assigns each axis a signed storage rank:
/// abs(rank) = storage order (0 = fastest-varying), sign = traversal direction.
/// The sign feeds the <see cref="OrientationFrame"/>, not the strides; the rank
/// permutes the spatial axes into memory order. See <see cref="BuildOrientationFrame"/>.
/// </summary>
public static class MifParser
{
    public static MiqImage Parse(byte[] data, string? formatLabel = null)
    {
        var (fields, transformRows, headerEndOffset) = ParseHeaderFields(data);
        return BuildImage(data, fields, transformRows, headerEndOffset, formatLabel);
    }

    // ── Header text parsing ──────────────────────────────────────────────────

    /// <summary>
    /// Splits the header into single-valued fields (last occurrence wins) plus the
    /// <c>transform:</c> rows — written as three repeated lines, and the only
    /// repeated key this parser consumes.
    /// </summary>
    private static (Dictionary<string, string> fields, List<string> transformRows, int headerEndOffset)
        ParseHeaderFields(byte[] data)
    {
        var endOffset = FindEndMarker(data);
        if (endOffset < 0)
            throw new MiqException("MIF header: END marker not found.");

        var headerText = Encoding.ASCII.GetString(data, 0, endOffset);
        var lines = headerText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        if (lines.Length == 0 || lines[0].Trim() != "mrtrix image")
            throw new MiqException("MIF header: missing 'mrtrix image' magic.");

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var transformRows = new List<string>();
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line == "END" || line.Length == 0 || line[0] == '#') continue;
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            var key   = line.Substring(0, colon).Trim();
            var value = line.Substring(colon + 1).Trim();
            if (key.Length == 0) continue;
            if (string.Equals(key, "transform", StringComparison.OrdinalIgnoreCase))
                transformRows.Add(value);
            else
                fields[key] = value;
        }

        return (fields, transformRows, endOffset);
    }

    // Find "END" on its own line followed by \n (or \r\n).
    // Returns the byte offset immediately after the terminating newline.
    private static int FindEndMarker(byte[] data)
    {
        for (var i = 0; i < data.Length - 2; i++)
        {
            if (data[i] != 'E' || data[i + 1] != 'N' || data[i + 2] != 'D') continue;
            if (i != 0 && data[i - 1] != '\n') continue;   // must be at start of line

            var j = i + 3;
            if (j < data.Length && data[j] == '\r') j++;   // skip optional CR
            if (j < data.Length && data[j] == '\n') return j + 1;
        }
        return -1;
    }

    // ── Image construction ───────────────────────────────────────────────────

    private static MiqImage BuildImage(
        byte[] data, Dictionary<string, string> fields, List<string> transformRows,
        int headerEndOffset, string? formatLabel)
    {
        if (!fields.TryGetValue("dim", out var dimStr))
            throw new MiqException("MIF header: missing 'dim' field.");
        var dim = ParseIntList(dimStr);
        if (dim.Length < 3)
            throw MiqException.InvalidDimensions();
        foreach (var d in dim)
            if (d <= 0) throw MiqException.InvalidDimensions();

        if (!fields.TryGetValue("vox", out var voxStr))
            throw new MiqException("MIF header: missing 'vox' field.");
        var vox = ParseFloatList(voxStr);
        if (vox.Length < 3)
            throw new MiqException("MIF header: 'vox' field has fewer than 3 values.");

        if (!fields.TryGetValue("layout", out var layoutStr))
            throw new MiqException("MIF header: missing 'layout' field.");
        var layout = ParseLayout(layoutStr);
        if (layout.Length != dim.Length)
            throw new MiqException("MIF header: 'layout' count does not match 'dim' count.");

        if (!fields.TryGetValue("datatype", out var dtStr))
            throw new MiqException("MIF header: missing 'datatype' field.");
        var (datatype, littleEndian, isBit) = ParseDatatype(dtStr);

        // Axes 0/1/2 are spatial and form the slice planes.
        MiqParser.ValidateDimensionExtent(dim, datatype.BytesPerVoxel());
        MiqParser.ValidateSlicePlaneExtent(dim[0], dim[1], dim[2]);

        if (!fields.TryGetValue("file", out var fileStr))
            throw new MiqException("MIF header: missing 'file' field.");
        var payloadOffset = ParseFileSpec(fileStr, headerEndOffset);

        // Over EVERY declared axis (a 5-D MIF stores all of them). The product is
        // bounded by ValidateDimensionExtent; subtracting on the left keeps the
        // comparison overflow-free and rejects an offset past the end.
        long totalElements = 1;
        foreach (var d in dim) totalElements *= d;
        // Bit packs 8 voxels per byte with no alignment: ceil(n/8) bytes, NOT
        // n * BytesPerVoxel() (which reports the post-unpack width of 1).
        var payloadBytes = isBit
            ? (totalElements + 7) / 8
            : totalElements * datatype.BytesPerVoxel();
        if (data.Length - (long)payloadOffset < payloadBytes)
            throw MiqException.TruncatedData();

        // Unpack Bit to one byte per voxel here, so downstream sees plain uint8.
        // Strides index elements, so bit i → byte i preserves indexing for any layout.
        if (isBit)
        {
            data = UnpackBits(data, payloadOffset, totalElements);
            payloadOffset = 0;
        }

        var (rawStrides, baseElementIndex) = ComputeStrides(dim, layout);

        // MIF's `dim:` order is not its storage order. Present the three spatial axes
        // in MEMORY order (fastest first), like every other format — and like
        // `mrconvert x.mif x.nii.gz`, which bakes the layout into the NIfTI array
        // order. Using `dim:` order disagrees with fsleyes/nibabel/FSL. Non-spatial
        // axes stay in place.
        var order = SpatialMemoryOrder(layout);
        var storedDim     = PermuteSpatial(dim, order);
        var storedVox     = PermuteSpatial(vox, order);
        var elementStrides = PermuteSpatial(rawStrides, order);

        // vox may contain NaN for non-spatial dimensions (e.g. time axis); substitute 1.
        static float SafeVox(float v) => MiqCompat.IsFinite(v) && v > 0f ? v : 1f;
        var pixdim = new float[]
        {
            1f, SafeVox(storedVox[0]), SafeVox(storedVox[1]), SafeVox(storedVox[2]),
        };

        var orientationFrame = BuildOrientationFrame(transformRows, layout, order);

        var dimensions = new int[4];
        for (var i = 0; i < 4; i++) dimensions[i] = i < storedDim.Length ? storedDim[i] : 1;

        var header = new MiqHeader
        {
            LittleEndian  = littleEndian,
            Dimensions    = dimensions,
            Pixdim        = pixdim,
            Datatype      = datatype,
            VoxOffset     = payloadOffset,
            SclSlope      = 0f,
            SclInter      = 0f,
            QformCode     = 0,
            SformCode     = 0,
            SrowX         = new float[] { 0f, 0f, 0f, 0f },
            SrowY         = new float[] { 0f, 0f, 0f, 0f },
            SrowZ         = new float[] { 0f, 0f, 0f, 0f },
            FormatLabel   = formatLabel,
            // Datatype now reads uint8 for a Bit file; report what the header said.
            DatatypeLabel = isBit ? "bit" : null,
            OrientationFrame = orientationFrame,
        };

        return new MiqImage
        {
            Header           = header,
            Storage          = data,
            PayloadOffset    = payloadOffset,
            ElementStrides   = elementStrides,
            BaseElementIndex = baseElementIndex,
        };
    }

    /// Expands a bit-packed payload to one byte per voxel (0 or 1).
    ///
    /// Bit ordering is MSB-first WITHIN each byte -- voxel i lives at
    /// <c>data[i/8] &amp; (0x80 >> (i%8))</c>, matching MRtrix BITMASK (0x01U &lt;&lt; 7).
    /// Verified empirically against a maskfilter-produced mask: MSB-first yields a
    /// coherent mask whose x-transition count matches y and z, while LSB-first triples
    /// it and combs every boundary at an 8-voxel period.
    private static byte[] UnpackBits(byte[] data, int payloadOffset, long totalElements)
    {
        // The unpacked buffer is one byte per voxel, so it must fit a single byte[].
        // ValidateDimensionExtent only rules out long overflow, not the CLR array cap.
        if (totalElements > MiqParser.MaxArrayBytes)
            throw new MiqException("MIF: bit volume is too large to unpack.");

        var n = (int)totalElements;
        var unpacked = new byte[n];
        var src = payloadOffset;
        var i = 0;

        // Whole bytes: 8 voxels each, bit 7 (0x80) being the LOWEST element index.
        for (var end = n - 7; i < end; src++)
        {
            int b = data[src];
            unpacked[i]     = (byte)((b >> 7) & 1);
            unpacked[i + 1] = (byte)((b >> 6) & 1);
            unpacked[i + 2] = (byte)((b >> 5) & 1);
            unpacked[i + 3] = (byte)((b >> 4) & 1);
            unpacked[i + 4] = (byte)((b >> 3) & 1);
            unpacked[i + 5] = (byte)((b >> 2) & 1);
            unpacked[i + 6] = (byte)((b >> 1) & 1);
            unpacked[i + 7] = (byte)(b & 1);
            i += 8;
        }

        // Tail: the last byte carries 1-7 real bits; the remainder is padding we drop.
        for (; i < n; i++)
            unpacked[i] = (byte)((data[payloadOffset + (i >> 3)] >> (7 - (i & 7))) & 1);

        return unpacked;
    }

    // ── Orientation ──────────────────────────────────────────────────────────

    /// <summary>
    /// Anatomical frame for the three spatial axes: <c>transform:</c> composed with
    /// <c>layout:</c>. Never inferred from the axis index — that only holds for what
    /// <c>mrconvert</c> writes, not e.g. <c>mrtransform -replace</c>.
    ///
    /// Transform column i is image axis i's world (RAS) direction, like a NIfTI
    /// sform, so <see cref="OrientationFrame.From"/> does the lookup. Only the 3×3
    /// rotation is read. The layout composes in both halves: a reversed axis negates
    /// its column (strides are always positive), and columns are permuted into the
    /// same memory <paramref name="order"/> as dimensions and strides.
    ///
    /// Absent <c>transform:</c> means identity. A degenerate transform yields null
    /// (orientation unknown) rather than failing the parse.
    /// </summary>
    private static OrientationFrame? BuildOrientationFrame(
        List<string> transformRows, LayoutComponent[] layout, int[] order)
    {
        var rows = new[]
        {
            new[] { 1f, 0f, 0f },
            new[] { 0f, 1f, 0f },
            new[] { 0f, 0f, 1f },
        };

        if (transformRows.Count > 0)
        {
            if (transformRows.Count != 3) return null;
            for (var r = 0; r < 3; r++)
            {
                // Lenient parse: a malformed optional field costs the orientation,
                // not the preview.
                var values = TryParseFloatList(transformRows[r]);
                if (values is null || values.Length < 3) return null;
                for (var c = 0; c < 3; c++)
                {
                    if (!MiqCompat.IsFinite(values[c])) return null;
                    rows[r][c] = values[c];
                }
            }
        }

        for (var axis = 0; axis < 3; axis++)
        {
            if (!layout[axis].Reversed) continue;
            for (var r = 0; r < 3; r++) rows[r][axis] = -rows[r][axis];
        }

        return OrientationFrame.From(
            PermuteSpatial(rows[0], order),
            PermuteSpatial(rows[1], order),
            PermuteSpatial(rows[2], order));
    }

    /// The three spatial axes sorted by storage rank, fastest-varying first.
    private static int[] SpatialMemoryOrder(LayoutComponent[] layout)
    {
        var order = new[] { 0, 1, 2 };
        Array.Sort(order, (a, b) => layout[a].Order.CompareTo(layout[b].Order));
        return order;
    }

    /// Reorders the first three entries by <paramref name="order"/>, leaving any
    /// further (non-spatial) entries where they are. Callers pass arrays already
    /// validated to hold at least the three spatial axes.
    private static T[] PermuteSpatial<T>(T[] values, int[] order)
    {
        var result = (T[])values.Clone();
        for (var i = 0; i < 3; i++) result[i] = values[order[i]];
        return result;
    }

    // ── Stride computation (port of MIFAxisLayout.swift) ────────────────────

    private static (int[] strides, int baseIndex) ComputeStrides(int[] dim, LayoutComponent[] layout)
    {
        var n = layout.Length;

        // Sort axes by storage rank (layout[axis].Order = 0 is fastest-varying).
        var sorted = new int[n];
        for (var i = 0; i < n; i++) sorted[i] = i;
        Array.Sort(sorted, (a, b) => layout[a].Order.CompareTo(layout[b].Order));

        // Strides are ALWAYS POSITIVE. The layout sign lives only in the
        // OrientationFrame; folding it in here too would apply the reversal twice
        // (an upside-down reoriented view). Matches MIQCore's MIQImage.
        //
        // Indexed by `dim:` order; the caller permutes into memory order.
        var strides = new int[n];
        var stride  = 1;
        foreach (var axis in sorted)
        {
            strides[axis] = stride;
            stride *= dim[axis];
        }

        return (strides, 0);
    }

    // ── Field parsers ────────────────────────────────────────────────────────

    private static int ParseFileSpec(string fileStr, int headerEndOffset)
    {
        var parts = fileStr.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            throw new MiqException($"MIF header: empty 'file' field.");

        if (parts[0] != ".")
            throw new MiqException("MIF header: separate data files are not supported.");

        if (parts.Length == 1)
            return headerEndOffset; // data starts right after END\n

        if (!int.TryParse(parts[1], out var offset) || offset < 0)
            throw new MiqException($"MIF header: invalid file offset '{parts[1]}'.");

        return offset;
    }

    /// Returns the datatype the renderer will see: MRtrix `Bit` becomes
    /// <see cref="MiqDatatype.Uint8"/> with <c>isBit</c> set (unpacked in
    /// <see cref="BuildImage"/>). No sub-byte MiqDatatype, because every
    /// BytesPerVoxel() consumer assumes an integral width.
    private static (MiqDatatype datatype, bool littleEndian, bool isBit) ParseDatatype(string s)
    {
        s = s.Trim();
        var le = true;  // default: little-endian (standard on Windows / x86-64)

        if (s.EndsWith("LE", StringComparison.OrdinalIgnoreCase))
        {
            le = true;
            s  = s.Substring(0, s.Length - 2);
        }
        else if (s.EndsWith("BE", StringComparison.OrdinalIgnoreCase))
        {
            le = false;
            s  = s.Substring(0, s.Length - 2);
        }

        // Bit has no endianness suffix.
        if (string.Equals(s, "bit", StringComparison.OrdinalIgnoreCase))
            return (MiqDatatype.Uint8, le, true);

        var datatype = s.ToLowerInvariant() switch
        {
            "uint8"   or "uint8_t"  => MiqDatatype.Uint8,
            "int8"    or "int8_t"   => MiqDatatype.Int8,
            "uint16"  or "uint16_t" => MiqDatatype.Uint16,
            "int16"   or "int16_t"  => MiqDatatype.Int16,
            "uint32"  or "uint32_t" => MiqDatatype.Uint32,
            "int32"   or "int32_t"  => MiqDatatype.Int32,
            "float32"               => MiqDatatype.Float32,
            "float64"               => MiqDatatype.Float64,
            _ => throw new MiqException($"MIF: unsupported datatype '{s}'."),
        };

        return (datatype, le, false);
    }

    private static string[] SplitList(string s) =>
        s.Split(new[] { ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

    private static int[] ParseIntList(string s)
    {
        var parts = SplitList(s);
        var result = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i].Trim(), out result[i]))
                throw new MiqException($"MIF: invalid integer '{parts[i]}'.");
        }
        return result;
    }

    private static float[] ParseFloatList(string s)
    {
        var parts = SplitList(s);
        var result = new float[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!TryParseFloat(parts[i], out result[i]))
                throw new MiqException($"MIF: invalid float '{parts[i].Trim()}'.");
        }
        return result;
    }

    /// Lenient sibling of <see cref="ParseFloatList"/>, for optional fields whose
    /// malformation must not fail the parse: null instead of an exception.
    private static float[]? TryParseFloatList(string s)
    {
        var parts = SplitList(s);
        var result = new float[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!TryParseFloat(parts[i], out result[i])) return null;
        }
        return result;
    }

    private static bool TryParseFloat(string token, out float value)
    {
        var t = token.Trim();
        if (t.Equals("nan", StringComparison.OrdinalIgnoreCase)) { value = float.NaN; return true; }
        return float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static LayoutComponent[] ParseLayout(string s)
    {
        var parts = SplitList(s);
        var result = new LayoutComponent[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            var t        = parts[i].Trim();
            var reversed = t.Length > 0 && t[0] == '-';
            var digits   = t.TrimStart('+', '-');
            if (!int.TryParse(digits, out var order))
                throw new MiqException($"MIF: invalid layout component '{parts[i]}'.");
            result[i] = new LayoutComponent(order, reversed);
        }
        return result;
    }

    // ── Internal types ───────────────────────────────────────────────────────

    private readonly struct LayoutComponent
    {
        public int  Order    { get; }
        public bool Reversed { get; }

        public LayoutComponent(int order, bool reversed)
        {
            Order    = order;
            Reversed = reversed;
        }
    }
}
