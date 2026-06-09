namespace MIQ.Parsing;

/// <summary>
/// NIfTI-1 (348-byte header) and NIfTI-2 (540-byte header) parser.
/// Direct port of MIQCore's <c>MIQParser+NIfTI.swift</c>.
/// </summary>
public static class NiftiParser
{
    public static MiqImage Parse(byte[] data, string? formatLabel = null)
    {
        var header = ParseHeader(data, formatLabel);
        if (data.Length < header.VoxOffset)
            throw MiqException.TruncatedData();
        return new MiqImage
        {
            Header = header,
            Storage = data,
            PayloadOffset = header.VoxOffset,
        };
    }

    public static MiqHeader ParseHeader(byte[] data, string? formatLabel = null)
    {
        if (data.Length < 4) throw MiqException.TruncatedData();

        var headerSizeLE = MiqBinaryReader.Int32(data, 0, littleEndian: true);
        var headerSizeBE = MiqBinaryReader.Int32(data, 0, littleEndian: false);

        if (headerSizeLE == 348 || headerSizeBE == 348)
            return ParseNifti1Header(data, littleEndian: headerSizeLE == 348, formatLabel);
        if (headerSizeLE == 540 || headerSizeBE == 540)
            return ParseNifti2Header(data, littleEndian: headerSizeLE == 540, formatLabel);

        throw MiqException.InvalidHeaderSize(headerSizeLE);
    }

    // ── NIfTI-1 (348-byte header) ────────────────────────────────────────────

    private static MiqHeader ParseNifti1Header(byte[] data, bool littleEndian, string? formatLabel)
    {
        if (data.Length < 348) throw MiqException.TruncatedData();

        var dim = MiqBinaryReader.Int16Array(data, 40, count: 8, littleEndian);
        var dimensions = ParseDimensions(Array.ConvertAll(dim, v => (int)v));

        var datatype = ReadAndValidateDatatype(data, datatypeOffset: 70, bitpixOffset: 72, littleEndian);

        var pixdim = MiqBinaryReader.Float32Array(data, 76, count: 8, littleEndian);
        var voxOffset = (int)MiqBinaryReader.Float32(data, 108, littleEndian);
        var sclSlope = MiqBinaryReader.Float32(data, 112, littleEndian);
        var sclInter = MiqBinaryReader.Float32(data, 116, littleEndian);
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
        var dimensions = ParseDimensions(Array.ConvertAll(dim, v => (int)v));

        var datatype = ReadAndValidateDatatype(data, datatypeOffset: 12, bitpixOffset: 14, littleEndian);

        var pixdim = Array.ConvertAll(
            MiqBinaryReader.Float64Array(data, 104, count: 4, littleEndian), v => (float)v);
        var voxOffset = (int)MiqBinaryReader.Int64(data, 168, littleEndian);
        var sclSlope = (float)MiqBinaryReader.Float64(data, 176, littleEndian);
        var sclInter = (float)MiqBinaryReader.Float64(data, 184, littleEndian);
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
