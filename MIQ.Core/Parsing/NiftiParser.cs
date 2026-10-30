namespace MIQ.Parsing;

/// <summary>
/// NIfTI-1 (348-byte header) and NIfTI-2 (540-byte header) parser.
/// Direct port of MIQCore's <c>MIQParser+NIfTI.swift</c>.
/// </summary>
public static class NiftiParser
{
    /// <param name="compressed">Whether the file was gzipped; only affects the
    /// format label.</param>
    public static MiqImage Parse(byte[] data, bool compressed = false)
    {
        var header = ParseHeader(data, compressed);

        // Require volume 0 only: reads past it are already bounds-checked and
        // zero-filled. Lives here, not in ParseHeader, which partial loads call on
        // a 1 KB probe. Subtracting on the left keeps the comparison overflow-free.
        var volumeBytes = (long)header.Width * header.Height * header.Depth
                           * header.Datatype.BytesPerVoxel();
        if (data.Length - (long)header.VoxOffset < volumeBytes)
            throw MiqException.TruncatedData();

        return new MiqImage
        {
            Header = header,
            Storage = data,
            PayloadOffset = header.VoxOffset,
        };
    }

    /// A split pair: the 348-byte <c>.hdr</c> plus the <c>.img</c> voxels (already
    /// gunzipped if it was <c>.img.gz</c>). The header's magic tells a NIfTI-1 pair
    /// (<c>ni1</c>) from plain Analyze 7.5, whose bytes past `descrip` are not a
    /// qform/sform: Analyze gets no orientation frame (stored orientation).
    /// vox_offset is an offset into the .img, usually 0.
    public static MiqImage ParsePair(byte[] hdr, byte[] img, bool imgCompressed)
    {
        var header = ParsePairHeader(hdr, imgCompressed);

        var volumeBytes = (long)header.Width * header.Height * header.Depth
                          * header.Datatype.BytesPerVoxel();
        if (img.Length - (long)header.VoxOffset < volumeBytes)
            throw MiqException.TruncatedData();

        return new MiqImage
        {
            Header = header,
            Storage = img,
            PayloadOffset = header.VoxOffset,
        };
    }

    /// The header of a split pair, validated exactly as <see cref="ParsePair"/> does.
    /// Also decides whether a <c>.hdr</c> is ours (<see cref="DetachedData.LooksLikeAnalyzeHeader"/>).
    internal static MiqHeader ParsePairHeader(byte[] hdr, bool imgCompressed)
    {
        if (hdr.Length < 4) throw MiqException.TruncatedData();
        var headerSizeLE = MiqBinaryReader.Int32(hdr, 0, littleEndian: true);
        var headerSizeBE = MiqBinaryReader.Int32(hdr, 0, littleEndian: false);
        if (headerSizeLE != 348 && headerSizeBE != 348)
            throw MiqException.InvalidHeaderSize(headerSizeLE);

        var isNifti = hdr.Length >= 348 && hdr[344] == 'n' && hdr[345] == 'i' && hdr[346] == '1';
        var label = (isNifti ? "NIfTI-1 pair" : "Analyze 7.5") + (imgCompressed ? ", compressed .img" : "");
        var header = ParseNifti1Header(hdr, littleEndian: headerSizeLE == 348, label, pair: true, analyze: !isNifti);
        MiqParser.ValidateDimensionExtent(header.Dimensions, header.Datatype.BytesPerVoxel());
        MiqParser.ValidateSlicePlaneExtent(header.Width, header.Height, header.Depth);
        return header;
    }

    /// Pass <paramref name="compressed"/> correctly: partial loads keep this header
    /// as the displayed image's.
    public static MiqHeader ParseHeader(byte[] data, bool compressed = false)
    {
        if (data.Length < 4) throw MiqException.TruncatedData();

        var headerSizeLE = MiqBinaryReader.Int32(data, 0, littleEndian: true);
        var headerSizeBE = MiqBinaryReader.Int32(data, 0, littleEndian: false);

        // NIfTI-1 keeps the file-kind label (null → "NIfTI-1" via DisplayName);
        // NIfTI-2 shares those file kinds, so it names itself.
        MiqHeader header;
        if (headerSizeLE == 348 || headerSizeBE == 348)
            header = ParseNifti1Header(data, littleEndian: headerSizeLE == 348,
                compressed ? MiqFileKind.NiiGz.DisplayName() : null);
        else if (headerSizeLE == 540 || headerSizeBE == 540)
            header = ParseNifti2Header(data, littleEndian: headerSizeLE == 540,
                compressed ? "Compressed NIfTI-2" : "NIfTI-2");
        else
            throw MiqException.InvalidHeaderSize(headerSizeLE);

        // Header-only guards; neither asserts the payload is present.
        var bpv = header.Datatype.BytesPerVoxel();
        MiqParser.ValidateDimensionExtent(header.Dimensions, bpv);
        MiqParser.ValidateSlicePlaneExtent(header.Width, header.Height, header.Depth);
        return header;
    }

    // ── NIfTI-1 (348-byte header) ────────────────────────────────────────────

    /// <param name="pair">vox_offset points into a separate .img, so it is not
    /// floored to the header end.</param>
    /// <param name="analyze">Plain Analyze 7.5: the qform/sform slots hold other
    /// fields (orient, originator), so no orientation is read from them.</param>
    private static MiqHeader ParseNifti1Header(byte[] data, bool littleEndian, string? formatLabel,
        bool pair = false, bool analyze = false)
    {
        if (data.Length < 348) throw MiqException.TruncatedData();

        var dim = MiqBinaryReader.Int16Array(data, 40, count: 8, littleEndian);
        var dimensions = ParseDimensions(Array.ConvertAll(dim, v => (int)v));

        var datatype = ReadAndValidateDatatype(data, datatypeOffset: 70, bitpixOffset: 72, littleEndian);

        var pixdim = MiqBinaryReader.Float32Array(data, 76, count: 8, littleEndian);
        // float→double widening is exact, so the check below sees the stored value.
        var voxOffset = NarrowVoxOffset(MiqBinaryReader.Float32(data, 108, littleEndian));
        // SPM Analyze uses funused1 as a slope, but funused2 is unused, not an
        // intercept. Ignore it before normalization so junk cannot discard the slope.
        var (sclSlope, sclInter) = NormalizeScaling(
            MiqBinaryReader.Float32(data, 112, littleEndian),
            analyze ? 0f : MiqBinaryReader.Float32(data, 116, littleEndian));
        var qformCode = analyze ? (short)0 : MiqBinaryReader.Int16(data, 252, littleEndian);
        var sformCode = analyze ? (short)0 : MiqBinaryReader.Int16(data, 254, littleEndian);
        var quaternB = analyze ? 0f : MiqBinaryReader.Float32(data, 256, littleEndian);
        var quaternC = analyze ? 0f : MiqBinaryReader.Float32(data, 260, littleEndian);
        var quaternD = analyze ? 0f : MiqBinaryReader.Float32(data, 264, littleEndian);
        var srowX = analyze ? new float[4] : MiqBinaryReader.Float32Array(data, 280, count: 4, littleEndian);
        var srowY = analyze ? new float[4] : MiqBinaryReader.Float32Array(data, 296, count: 4, littleEndian);
        var srowZ = analyze ? new float[4] : MiqBinaryReader.Float32Array(data, 312, count: 4, littleEndian);
        var qfac = pixdim.Length > 0 ? pixdim[0] : 1f;
        var (frame, conflictingQform) = NiftiOrientationFrame(
            sformCode, srowX, srowY, srowZ, qformCode, quaternB, quaternC, quaternD, qfac);

        return new MiqHeader
        {
            LittleEndian = littleEndian,
            Dimensions = dimensions,
            Pixdim = new[] { pixdim[0], pixdim[1], pixdim[2], pixdim[3] },
            Datatype = datatype,
            VoxOffset = pair ? voxOffset : Math.Max(352, voxOffset),
            SclSlope = sclSlope,
            SclInter = sclInter,
            QformCode = qformCode,
            SformCode = sformCode,
            SrowX = srowX,
            SrowY = srowY,
            SrowZ = srowZ,
            QuaternB = quaternB,
            QuaternC = quaternC,
            QuaternD = quaternD,
            Qfac = qfac,
            FormatLabel = formatLabel,
            OrientationFrame = frame,
            ConflictingQformOrientation = conflictingQform,
            Comment = MiqParser.HeaderComment(data, offset: 148, length: 80),
        };
    }

    // ── NIfTI-2 (540-byte header) ────────────────────────────────────────────
    // Field types widen relative to NIfTI-1: dim→int64, pixdim→float64,
    // vox_offset→int64, scl_slope/inter→float64, srow→float64, form_codes→int32.

    private static MiqHeader ParseNifti2Header(byte[] data, bool littleEndian, string? formatLabel)
    {
        if (data.Length < 540) throw MiqException.TruncatedData();

        var dim = MiqBinaryReader.Int64Array(data, 16, count: 8, littleEndian);
        var dimensions = ParseDimensions(NarrowDimensions(dim));

        var datatype = ReadAndValidateDatatype(data, datatypeOffset: 12, bitpixOffset: 14, littleEndian);

        var pixdim = Array.ConvertAll(
            MiqBinaryReader.Float64Array(data, 104, count: 4, littleEndian), v => (float)v);
        var voxOffset = NarrowVoxOffset(MiqBinaryReader.Int64(data, 168, littleEndian));
        var (sclSlope, sclInter) = NormalizeScaling(
            (float)MiqBinaryReader.Float64(data, 176, littleEndian),
            (float)MiqBinaryReader.Float64(data, 184, littleEndian));
        var qformCode = MiqBinaryReader.Int32(data, 344, littleEndian);
        var sformCode = MiqBinaryReader.Int32(data, 348, littleEndian);
        var quaternB = (float)MiqBinaryReader.Float64(data, 352, littleEndian);
        var quaternC = (float)MiqBinaryReader.Float64(data, 360, littleEndian);
        var quaternD = (float)MiqBinaryReader.Float64(data, 368, littleEndian);
        var srowX = Array.ConvertAll(
            MiqBinaryReader.Float64Array(data, 400, count: 4, littleEndian), v => (float)v);
        var srowY = Array.ConvertAll(
            MiqBinaryReader.Float64Array(data, 432, count: 4, littleEndian), v => (float)v);
        var srowZ = Array.ConvertAll(
            MiqBinaryReader.Float64Array(data, 464, count: 4, littleEndian), v => (float)v);
        var qfac = pixdim.Length > 0 ? pixdim[0] : 1f;
        var (frame, conflictingQform) = NiftiOrientationFrame(
            sformCode, srowX, srowY, srowZ, qformCode, quaternB, quaternC, quaternD, qfac);

        return new MiqHeader
        {
            LittleEndian = littleEndian,
            Dimensions = dimensions,
            Pixdim = pixdim,
            Datatype = datatype,
            VoxOffset = Math.Max(544, voxOffset),
            SclSlope = sclSlope,
            SclInter = sclInter,
            QformCode = qformCode,
            SformCode = sformCode,
            SrowX = srowX,
            SrowY = srowY,
            SrowZ = srowZ,
            QuaternB = quaternB,
            QuaternC = quaternC,
            QuaternD = quaternD,
            Qfac = qfac,
            FormatLabel = formatLabel,
            OrientationFrame = frame,
            ConflictingQformOrientation = conflictingQform,
            Comment = MiqParser.HeaderComment(data, offset: 240, length: 80),
        };
    }

    // ── Shared helpers ───────────────────────────────────────────────────────

    /// Prefer sform when present and non-degenerate; otherwise qform; else null.
    /// Port of MIQParser+NIfTI.swift's niftiOrientationFrame.
    ///
    /// Also returns the qform's orientation label when both are valid and disagree
    /// (e.g. sform RAS, qform LAS): tools that prefer the qform then show the image
    /// in another orientation. The labels alone can't decide that — an axis near
    /// 45° between two world axes (an oblique scan) can tip either way on rounding —
    /// so a conflict also needs some storage axis to point more than 45° away from
    /// where the other transform puts it.
    private static (OrientationFrame? frame, string? conflictingQform) NiftiOrientationFrame(
        int sformCode, IReadOnlyList<float> srowX, IReadOnlyList<float> srowY, IReadOnlyList<float> srowZ,
        int qformCode, float quaternB, float quaternC, float quaternD, float qfac)
    {
        var qformRows = qformCode > 0
            ? OrientationFrame.QuaternionRows(quaternB, quaternC, quaternD, qfac)
            : null;
        var qformFrame = qformRows is null
            ? null
            : OrientationFrame.From(qformRows[0], qformRows[1], qformRows[2]);

        if (sformCode > 0 && OrientationFrame.From(srowX, srowY, srowZ) is { } frame)
        {
            var conflict = qformFrame is not null && qformFrame.Label != frame.Label
                && SomeAxisDiverges([srowX, srowY, srowZ], qformRows!);
            return (frame, conflict ? qformFrame!.Label : null);
        }
        return (qformFrame, null);
    }

    /// Whether some storage axis (a column of the 3×3 part) differs by more than
    /// 45° between two sform-style row sets. Scale-invariant, so voxel size
    /// doesn't count.
    private static bool SomeAxisDiverges(IReadOnlyList<float>[] a, IReadOnlyList<float>[] b)
    {
        var cos45 = Math.Sqrt(0.5);
        for (var col = 0; col < 3; col++)
        {
            double dot = 0, na = 0, nb = 0;
            for (var row = 0; row < 3; row++)
            {
                double u = col < a[row].Count ? a[row][col] : 0;
                double v = col < b[row].Count ? b[row][col] : 0;
                dot += u * v;
                na += u * u;
                nb += v * v;
            }
            var norms = Math.Sqrt(na * nb);
            if (norms > 0 && dot < cos45 * norms) return true;
        }
        return false;
    }

    private static MiqDatatype ReadAndValidateDatatype(
        byte[] data, int datatypeOffset, int bitpixOffset, bool littleEndian)
    {
        var raw = MiqBinaryReader.Int16(data, datatypeOffset, littleEndian);
        if (!MiqDatatypeExtensions.IsKnown(raw))
            throw MiqException.UnsupportedDatatype(raw);
        var datatype = (MiqDatatype)raw;
        var bitpix = MiqBinaryReader.Int16(data, bitpixOffset, littleEndian);
        if (bitpix != datatype.BytesPerVoxel() * 8)
            throw MiqException.UnsupportedDatatype(raw);
        return datatype;
    }

    /// vox_offset (float32 in NIfTI-1, int64 in NIfTI-2) narrowed to int without
    /// relying on an unspecified out-of-range cast (net8/x64 yields int.MinValue,
    /// which the caller would silently floor to the header size, reading garbage).
    ///
    /// Non-finite and negative values return 0, which the caller floors to the
    /// header size (vox_offset = 0 is the common "data follows the header"
    /// spelling). Only an offset past int range is rejected.
    private static int NarrowVoxOffset(double value)
    {
        if (double.IsNaN(value) || value < 0) return 0;
        if (value > int.MaxValue) throw MiqException.InvalidVoxOffset(value);
        return (int)value;
    }

    private static int NarrowVoxOffset(long value)
    {
        if (value < 0) return 0;
        if (value > int.MaxValue) throw MiqException.InvalidVoxOffset(value);
        return (int)value;
    }

    /// Non-finite scl_slope/scl_inter mean "no scaling" (slope 0). nibabel routinely
    /// writes NaN here to mark scaling as undefined, so this is normal input, not
    /// corruption. Without it, `slope != 0` is true for NaN and every voxel becomes
    /// NaN (a black square). Both must be finite, so they are dropped together.
    private static (float slope, float inter) NormalizeScaling(float slope, float inter) =>
        MiqCompat.IsFinite(slope) && MiqCompat.IsFinite(inter) ? (slope, inter) : (0f, 0f);

    /// NIfTI-2 stores dim[] as int64; truncating would turn 2^32+1 into 1 and hide
    /// it from the extent guards. Only the slots <see cref="ParseDimensions"/> reads
    /// are checked — trailing slots past ndim are often junk in real files.
    private static int[] NarrowDimensions(long[] dim)
    {
        var ndim = dim[0];
        // Mirrors ParseDimensions' clamp so exactly the used slots are validated.
        var upper = ndim < 1 ? 0 : (int)Math.Max(3, Math.Min(7, ndim));

        var narrowed = new int[dim.Length];
        for (var i = 0; i < dim.Length; i++)
        {
            if (i <= upper && (dim[i] > int.MaxValue || dim[i] < int.MinValue))
                throw MiqException.InvalidDimensions();
            narrowed[i] = (int)dim[i]; // unused trailing slots: value is irrelevant
        }
        return narrowed;
    }

    private static int[] ParseDimensions(int[] dim)
    {
        var ndim = dim[0];
        if (ndim < 1) throw MiqException.InvalidDimensions();

        var dimensions = new List<int>();
        var upper = Math.Max(3, Math.Min(7, ndim));
        for (var idx = 1; idx <= upper; idx++)
            dimensions.Add(Math.Max(1, dim[idx]));
        while (dimensions.Count < 4)
            dimensions.Add(1);
        return dimensions.ToArray();
    }
}
