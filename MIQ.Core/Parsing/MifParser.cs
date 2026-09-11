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
/// The sign feeds the <see cref="OrientationFrame"/> (which axis points which
/// anatomical way), NOT the element strides — strides stay positive and the
/// reversal is applied once, at slice time, via the frame. The rank permutes the
/// three spatial axes: the volume is presented in memory order, not <c>dim:</c>
/// order, so MIF reaches the renderer laid out like every other format here.
///
/// Anatomy comes from <c>transform:</c> composed with that layout — never from
/// the axis index; see <see cref="BuildOrientationFrame"/>.
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
    /// Splits the header into single-valued fields plus the <c>transform:</c> rows.
    /// A MIF key may repeat, in which case the entries form a list — <c>transform:</c>
    /// is written as three such lines, one per world axis, and is the only repeated
    /// key this parser consumes (<c>dw_scheme</c>, <c>comments</c> and
    /// <c>command_history</c> also repeat and are ignored). Everything else keeps the
    /// last occurrence, as before.
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

        // dim is validated positive and ≥3 entries above. MRtrix axes 0/1/2 are
        // the spatial ones, so they are the trio that forms the slice planes.
        MiqParser.ValidateDimensionExtent(dim, datatype.BytesPerVoxel());
        MiqParser.ValidateSlicePlaneExtent(dim[0], dim[1], dim[2]);

        if (!fields.TryGetValue("file", out var fileStr))
            throw new MiqException("MIF header: missing 'file' field.");
        var payloadOffset = ParseFileSpec(fileStr, headerEndOffset);

        // Multiplies over EVERY declared axis, not just the first four — a 5-D MIF
        // stores all of them, so narrowing this to W·H·D·T would under-require.
        // ValidateDimensionExtent bounds the product; comparing against
        // data.Length - payloadOffset keeps the comparison itself overflow-free
        // (and still rejects a payloadOffset past the end, which goes negative).
        long totalElements = 1;
        foreach (var d in dim) totalElements *= d;
        // Bit packs 8 voxels per byte with no per-row or per-slice alignment, so the
        // payload is ceil(n/8) bytes -- NOT n * BytesPerVoxel(), which reports the
        // post-unpack width of 1. The final byte may hold up to 7 padding bits.
        var payloadBytes = isBit
            ? (totalElements + 7) / 8
            : totalElements * datatype.BytesPerVoxel();
        if (data.Length - (long)payloadOffset < payloadBytes)
            throw MiqException.TruncatedData();

        // Expand Bit to one byte per voxel HERE, at the parse boundary, so everything
        // downstream sees an ordinary contiguous uint8 payload: the strides below index
        // ELEMENTS, and unpacking bit i to byte i preserves that indexing exactly, for
        // any layout. Costs 8x the payload in memory (a mask is small) and buys an
        // untouched renderer -- see ParseDatatype for why no sub-byte MiqDatatype.
        if (isBit)
        {
            data = UnpackBits(data, payloadOffset, totalElements);
            payloadOffset = 0;
        }

        var (rawStrides, baseElementIndex) = ComputeStrides(dim, layout);

        // MIF is the one format here whose `dim:` order is NOT its storage order —
        // MRtrix realigns on import and parks the real orientation in `layout:`. The
        // three spatial axes are therefore presented in MEMORY order (fastest-varying
        // first), which is what every other format already hands the renderer, and
        // what `mrconvert x.mif x.nii.gz` writes: NIfTI has no stride indirection, so
        // MRtrix bakes the layout into the affine and the array order. Presenting the
        // `dim:` order instead would preview the same volume differently in the two
        // containers and report an orientation no other tool (fsleyes, nibabel, FSL)
        // agrees with. Non-spatial axes (volumes) keep their position — only 0/1/2
        // permute, and only relative to each other, which is exactly what the NIfTI
        // export preserves (it must put the volume axis last regardless).
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
    /// Anatomical frame for the three spatial axes: the <c>transform:</c> composed
    /// with the <c>layout:</c> directions. Anatomy is never inferred from the axis
    /// index — that shortcut only holds for what <c>mrconvert</c> writes (it
    /// normalises the transform on write, parking the real orientation in the
    /// layout); a file from <c>mrtransform -replace</c>, which does not normalise,
    /// is silently wrong under it, left/right included.
    ///
    /// The transform's COLUMN i is image axis i's direction in world (RAS) space —
    /// the same shape as a NIfTI sform, so <see cref="OrientationFrame.From"/> does
    /// the anatomy lookup and no new math is written here. Only the 3×3 rotation
    /// part is read; the translation column and the per-axis voxel size scale a
    /// whole column, which cannot change which component dominates it.
    ///
    /// The layout then composes with it, in both of its halves. Direction: strides
    /// are always positive (see <see cref="ComputeStrides"/>), so a reversed axis is
    /// read memory-first — backwards along the direction the transform gives — and
    /// its anatomy is the opposite letter. Order: the columns come out in
    /// <paramref name="order"/>, the same memory order the dimensions and strides are
    /// permuted into, so axis i of the frame is axis i of the volume the renderer
    /// walks and the label is the one the NIfTI export reports.
    ///
    /// <c>transform:</c> is optional; absent, it is the identity, and the
    /// composition reduces to the layout alone — a fallback, not a second code path.
    /// Returns null ("orientation unknown", rendered as "?") for a degenerate
    /// transform: a wrong-shaped, non-finite or zero column, or two columns dominant
    /// on the same world axis. A bad transform does not fail the parse — the voxels
    /// are still displayable, only their anatomy is unknown.
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

    /// The three spatial axes sorted by storage rank, fastest-varying first — the
    /// order MIF data actually sits in memory, and the axis order this parser
    /// presents the volume in.
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

        // Strides are ALWAYS POSITIVE: they map a canonical voxel index to its
        // memory element, with no direction flip. The axis-reversal carried by
        // the layout sign lives solely in the OrientationFrame (built above from
        // the signed direction vectors). Folding the sign into the strides too
        // would apply the reversal twice — reading the axis flipped *and*
        // labelling it flipped — which shows up as an upside-down reoriented
        // view. Matches MIQCore's MIQImage ("strides are always positive — axis-
        // reversal info lives in the orientation frame"); no base offset needed.
        //
        // Indexed by IMAGE axis (`dim:` order), as `dim` is here; the caller then
        // permutes strides and dimensions together into memory order.
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

    /// Returns the datatype the RENDERER will see, which is not always the one the
    /// header names: MRtrix `Bit` is reported as <see cref="MiqDatatype.Uint8"/> with
    /// <c>isBit</c> set, and <see cref="BuildImage"/> unpacks the payload to match.
    /// MiqDatatype deliberately gains no sub-byte member -- every consumer of
    /// BytesPerVoxel() assumes an integral width, including both hot decode loops and
    /// ScanVolume0, so the widening happens here at the parse boundary instead.
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

        // Bit carries no endianness (MRtrix writes a bare "Bit"), and the LE/BE strip
        // above cannot have consumed any of it, so this sees the header spelling as-is.
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
