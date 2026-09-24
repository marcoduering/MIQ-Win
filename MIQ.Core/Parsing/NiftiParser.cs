namespace MIQ.Parsing;

/// <summary>
/// NIfTI-1 (348-byte header) and NIfTI-2 (540-byte header) parser.
/// Direct port of MIQCore's <c>MIQParser+NIfTI.swift</c>.
/// </summary>
public static class NiftiParser
{
    /// <param name="compressed">Whether the file was gzipped. Only the format label
    /// depends on it: <see cref="MiqFileKind"/>'s display name says "NIfTI-1", and
    /// the header version is only known here while the compression is only known to
    /// the caller.</param>
    public static MiqImage Parse(byte[] data, bool compressed = false)
    {
        var header = ParseHeader(data, compressed);

        // ValidateSlicePlaneExtent (in ParseHeader) bounds any pairwise product of
        // width/height/depth, so this triple product can't wrap; subtracting
        // VoxOffset from data.Length rather than adding it to volumeBytes keeps
        // the comparison overflow-free too (same pattern as MghParser/MifParser).
        // Volume 0 only, not x Volumes: this belongs HERE, not in ParseHeader
        // (called on a 1 KB probe by the vol-0-first partial loads, which must
        // keep working on a file containing only volume 0), and volume 0 is what
        // every first render decodes regardless of load path. A file truncated
        // beyond volume 0 already degrades gracefully — Voxel() bounds-checks
        // every read against PayloadCount and zero-fills out of range — so this
        // guard's job is purely to reject early, before slice extraction attempts
        // to allocate a plane for data that was never there.
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

    /// Every caller whose header ends up on a displayed image must pass
    /// <paramref name="compressed"/> — the vol-0-first partial loads keep this
    /// header as the image's, unlike macOS, where the probe only sizes the read.
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

        // Header-only representability guards: safe here (and reached by the
        // vol-0-first partial loads, which call ParseHeader on a 1 KB probe)
        // precisely because neither asserts the payload is present.
        var bpv = header.Datatype.BytesPerVoxel();
        MiqParser.ValidateDimensionExtent(header.Dimensions, bpv);
        MiqParser.ValidateSlicePlaneExtent(header.Width, header.Height, header.Depth);
        return header;
    }

    // ── NIfTI-1 (348-byte header) ────────────────────────────────────────────

    private static MiqHeader ParseNifti1Header(byte[] data, bool littleEndian, string? formatLabel)
    {
        if (data.Length < 348) throw MiqException.TruncatedData();

        var dim = MiqBinaryReader.Int16Array(data, 40, count: 8, littleEndian);
        var dimensions = ParseDimensions(Array.ConvertAll(dim, v => (int)v));

        var datatype = ReadAndValidateDatatype(data, datatypeOffset: 70, bitpixOffset: 72, littleEndian);

        var pixdim = MiqBinaryReader.Float32Array(data, 76, count: 8, littleEndian);
        // float→double widening is exact, so the check below sees the stored value.
        var voxOffset = NarrowVoxOffset(MiqBinaryReader.Float32(data, 108, littleEndian));
        var (sclSlope, sclInter) = NormalizeScaling(
            MiqBinaryReader.Float32(data, 112, littleEndian),
            MiqBinaryReader.Float32(data, 116, littleEndian));
        var qformCode = MiqBinaryReader.Int16(data, 252, littleEndian);
        var sformCode = MiqBinaryReader.Int16(data, 254, littleEndian);
        var quaternB = MiqBinaryReader.Float32(data, 256, littleEndian);
        var quaternC = MiqBinaryReader.Float32(data, 260, littleEndian);
        var quaternD = MiqBinaryReader.Float32(data, 264, littleEndian);
        var srowX = MiqBinaryReader.Float32Array(data, 280, count: 4, littleEndian);
        var srowY = MiqBinaryReader.Float32Array(data, 296, count: 4, littleEndian);
        var srowZ = MiqBinaryReader.Float32Array(data, 312, count: 4, littleEndian);
        var qfac = pixdim.Length > 0 ? pixdim[0] : 1f;

        return new MiqHeader
        {
            LittleEndian = littleEndian,
            Dimensions = dimensions,
            Pixdim = new[] { pixdim[0], pixdim[1], pixdim[2], pixdim[3] },
            Datatype = datatype,
            VoxOffset = Math.Max(352, voxOffset),
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
            OrientationFrame = NiftiOrientationFrame(
                sformCode, srowX, srowY, srowZ, qformCode, quaternB, quaternC, quaternD, qfac),
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
            OrientationFrame = NiftiOrientationFrame(
                sformCode, srowX, srowY, srowZ, qformCode, quaternB, quaternC, quaternD, qfac),
        };
    }

    // ── Shared helpers ───────────────────────────────────────────────────────

    /// Prefer sform when present and non-degenerate; otherwise qform; else null.
    /// Port of MIQParser+NIfTI.swift's niftiOrientationFrame.
    private static OrientationFrame? NiftiOrientationFrame(
        int sformCode, IReadOnlyList<float> srowX, IReadOnlyList<float> srowY, IReadOnlyList<float> srowZ,
        int qformCode, float quaternB, float quaternC, float quaternD, float qfac)
    {
        if (sformCode > 0)
        {
            var f = OrientationFrame.From(srowX, srowY, srowZ);
            if (f is not null) return f;
        }
        if (qformCode > 0)
        {
            var f = OrientationFrame.FromQuaternion(quaternB, quaternC, quaternD, qfac);
            if (f is not null) return f;
        }
        return null;
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

    /// vox_offset is wider than the <c>int</c> it ends up in — a float32 in
    /// NIfTI-1, an int64 in NIfTI-2 — and a bare <c>(int)</c> cast of a value that
    /// doesn't fit produces an unspecified result the C# spec declines to define.
    ///
    /// Measured on net8/x64 (JIT-opaque values, so not compile-time folding):
    /// <c>(int)</c> of 1e10, +Inf, NaN and -1e10 all yield <b>int.MinValue</b> — it
    /// does <i>not</i> saturate, contrary to what this file's FIXME and CLAUDE.md
    /// both asserted for years. So the real defect was never a net8-vs-net462
    /// divergence: on <i>both</i> targets int.MinValue is floored straight back to
    /// the header size by the caller's Math.Max, and a file declaring an absurd
    /// vox_offset silently reads its payload from 352 and renders garbage instead of
    /// reporting anything. Uniformly wrong is worse than divergent, not better.
    ///
    /// Resolving it here removes the dependence on unspecified behaviour altogether,
    /// which is what actually matters: the guard holds whatever any future runtime
    /// decides that cast should mean.
    ///
    /// Non-finite and negative values return 0, which the caller floors to the
    /// header size — exactly what the cast already did on both targets, so nothing
    /// that previews today changes (and vox_offset = 0 is the common "data starts
    /// after the header" spelling). Only an offset genuinely past int range is
    /// rejected: no real file has one, and reading from the wrong place silently is
    /// worse than a clear error.
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

    /// Non-finite scl_slope/scl_inter mean "no scaling", which this format already
    /// spells as slope 0 — so normalise to 0/0 and let the existing unscaled path
    /// handle it.
    ///
    /// This is NOT corrupt-header hardening. nibabel uses NaN as its marker for
    /// "scaling undefined": it resets both fields to NaN when it loads an image (to
    /// record that the scaling was consumed by the read), that NaN lives in the
    /// header struct it writes back out, and its own reader maps a non-finite slope
    /// to "no scaling" on the way back in. A NaN slope is therefore an ordinary
    /// thing to find in a file from the most widely used NIfTI toolchain, and every
    /// reader is expected to render it normally.
    ///
    /// Without this the volume is destroyed rather than mis-scaled: MiqVolume.Voxel
    /// gates on `slope != 0`, which is TRUE for NaN, so every voxel becomes
    /// raw*NaN + inter = NaN. IntensityWindow then finds no finite value, returns a
    /// null window, and the whole file renders as a black square with no error —
    /// while the metadata panel reports "Scaling: x NaN + NaN".
    ///
    /// Both fields must be finite for the pair to be usable (a finite slope with a
    /// NaN intercept still yields NaN for every voxel), so they are dropped
    /// together. Finite pairs pass through untouched.
    private static (float slope, float inter) NormalizeScaling(float slope, float inter) =>
        MiqCompat.IsFinite(slope) && MiqCompat.IsFinite(inter) ? (slope, inter) : (0f, 0f);

    /// NIfTI-2 stores dim[] as int64. Truncating to int keeps only the low 32 bits,
    /// which turns an unrenderable size into a plausible one — 2^32+1 becomes 1 —
    /// so ValidateDimensionExtent and ValidateSlicePlaneExtent never see the value
    /// they exist to reject, and the file renders as a 1-voxel axis instead.
    ///
    /// Only the slots <see cref="ParseDimensions"/> actually reads are checked:
    /// dim[0] (ndim) and dim[1..upper]. Trailing slots past ndim are routinely junk
    /// in real files — rejecting on those would turn away valid data.
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
