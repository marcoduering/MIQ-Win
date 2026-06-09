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
/// MRtrix axis convention: axis 0 = L(−)/R(+), 1 = P(−)/A(+), 2 = I(−)/S(+).
/// The sign feeds the <see cref="OrientationFrame"/> (which axis points which
/// anatomical way), NOT the element strides — strides stay positive and the
/// reversal is applied once, at slice time, via the frame.
/// </summary>
public static class MifParser
{
    public static MiqImage Parse(byte[] data, string? formatLabel = null)
    {
        var (fields, headerEndOffset) = ParseHeaderFields(data);
        return BuildImage(data, fields, headerEndOffset, formatLabel);
    }

    // ── Header text parsing ──────────────────────────────────────────────────

    private static (Dictionary<string, string> fields, int headerEndOffset) ParseHeaderFields(byte[] data)
    {
        var endOffset = FindEndMarker(data);
        if (endOffset < 0)
            throw new MiqException("MIF header: END marker not found.");

        var headerText = Encoding.ASCII.GetString(data, 0, endOffset);
        var lines = headerText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        if (lines.Length == 0 || lines[0].Trim() != "mrtrix image")
            throw new MiqException("MIF header: missing 'mrtrix image' magic.");

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line == "END" || line.Length == 0 || line[0] == '#') continue;
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            var key   = line.Substring(0, colon).Trim();
            var value = line.Substring(colon + 1).Trim();
            if (key.Length > 0)
                fields[key] = value;
        }

        return (fields, endOffset);
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
        byte[] data, Dictionary<string, string> fields, int headerEndOffset, string? formatLabel)
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
        var (datatype, littleEndian) = ParseDatatype(dtStr);

        if (!fields.TryGetValue("file", out var fileStr))
            throw new MiqException("MIF header: missing 'file' field.");
        var payloadOffset = ParseFileSpec(fileStr, headerEndOffset);

        long totalElements = 1;
        foreach (var d in dim) totalElements *= d;
        var requiredBytes = payloadOffset + totalElements * datatype.BytesPerVoxel();
        if (data.Length < requiredBytes)
            throw MiqException.TruncatedData();

        var (elementStrides, baseElementIndex) = ComputeStrides(dim, layout);
        // vox may contain NaN for non-spatial dimensions (e.g. time axis); substitute 1.
        static float SafeVox(float v) => MiqCompat.IsFinite(v) && v > 0f ? v : 1f;
        var sx = SafeVox(vox[0]);
        var sy = SafeVox(vox[1]);
        var sz = SafeVox(vox[2]);
        var pixdim = new float[] { 1f, sx, sy, sz };

        // MRtrix axis convention: axis 0 = L(−)/R(+), 1 = P(−)/A(+), 2 = I(−)/S(+).
        // Build column-direction vectors so OrientationFrame.From can determine anatomy.
        var sign0 = layout[0].Reversed ? -1f : 1f;
        var sign1 = layout[1].Reversed ? -1f : 1f;
        var sign2 = layout[2].Reversed ? -1f : 1f;
        var srowX = new[] { sign0 * sx, 0f, 0f, 0f };
        var srowY = new[] { 0f, sign1 * sy, 0f, 0f };
        var srowZ = new[] { 0f, 0f, sign2 * sz, 0f };
        var orientationFrame = OrientationFrame.From(srowX, srowY, srowZ);

        var dimensions = new int[4];
        for (var i = 0; i < 4; i++) dimensions[i] = i < dim.Length ? dim[i] : 1;

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

    private static (MiqDatatype datatype, bool littleEndian) ParseDatatype(string s)
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

        return (datatype, le);
    }

    private static int[] ParseIntList(string s)
    {
        var parts = s.Split(new[] { ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
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
        var parts = s.Split(new[] { ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        var result = new float[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            var t = parts[i].Trim();
            if (t.Equals("nan", StringComparison.OrdinalIgnoreCase)) { result[i] = float.NaN; continue; }
            if (!float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out result[i]))
                throw new MiqException($"MIF: invalid float '{t}'.");
        }
        return result;
    }

    private static LayoutComponent[] ParseLayout(string s)
    {
        var parts = s.Split(new[] { ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
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
