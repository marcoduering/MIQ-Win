using System.Buffers.Binary;
using MIQ.Parsing;
using Xunit;

namespace MIQ.Tests;

// Negative-path coverage for the parser guards.
//
// golden.json proves that files which are *accepted* still render identically. It
// says nothing about rejection logic, which is what every guard here is — and a
// guard that becomes too strict fails silently, because the valid file it starts
// turning away was never in the corpus. So each guard gets both halves: the input
// it exists to reject, and a neighbouring input it must still accept.
//
// Deliberately not a mutation fuzzer. Plugin.cs wraps the whole cold path in a
// blanket catch, so "never crashes the host" is guaranteed by the caller; what
// needs pinning is which inputs are refused and which are not. Fixtures are
// synthetic byte arrays — no corpus, nothing machine-specific.
public class ParserGuardTests
{
    // ── NIfTI-1 ─────────────────────────────────────────────────────────────
    // 348-byte header, little-endian. Payload starts at max(352, vox_offset).

    const int N1HeaderBytes = 348;
    const int N1MinOffset = 352;

    static byte[] Nifti1Header(
        short ndim = 3, short w = 4, short h = 4, short d = 4, short volumes = 1,
        MiqDatatype datatype = MiqDatatype.Int16, short? bitpix = null,
        float voxOffset = N1MinOffset, int sizeofHdr = 348)
    {
        var hdr = new byte[N1HeaderBytes];
        BinaryPrimitives.WriteInt32LittleEndian(hdr.AsSpan(0), sizeofHdr);

        BinaryPrimitives.WriteInt16LittleEndian(hdr.AsSpan(40), ndim);
        BinaryPrimitives.WriteInt16LittleEndian(hdr.AsSpan(42), w);
        BinaryPrimitives.WriteInt16LittleEndian(hdr.AsSpan(44), h);
        BinaryPrimitives.WriteInt16LittleEndian(hdr.AsSpan(46), d);
        BinaryPrimitives.WriteInt16LittleEndian(hdr.AsSpan(48), volumes);

        BinaryPrimitives.WriteInt16LittleEndian(hdr.AsSpan(70), (short)datatype);
        BinaryPrimitives.WriteInt16LittleEndian(
            hdr.AsSpan(72), bitpix ?? (short)(datatype.BytesPerVoxel() * 8));

        for (var i = 0; i < 8; i++) // pixdim: 1 mm isotropic
            BinaryPrimitives.WriteSingleLittleEndian(hdr.AsSpan(76 + i * 4), 1f);

        BinaryPrimitives.WriteSingleLittleEndian(hdr.AsSpan(108), voxOffset);
        return hdr;
    }

    /// Header followed by <paramref name="payloadBytes"/> of zeros at
    /// <paramref name="payloadOffset"/> (default: the standard 352).
    static byte[] Nifti1File(byte[] header, int payloadBytes, int payloadOffset = N1MinOffset)
    {
        var file = new byte[payloadOffset + payloadBytes];
        header.CopyTo(file.AsSpan());
        return file;
    }

    /// The bytes one 4×4×4 int16 volume occupies.
    const int OneVolume = 4 * 4 * 4 * 2;

    // ── NIfTI-1: inputs that must be refused ────────────────────────────────

    [Fact]
    public void Nifti1_HeaderSizeFieldUnrecognised_Throws() =>
        Assert.Throws<MiqException>(() =>
            NiftiParser.Parse(Nifti1File(Nifti1Header(sizeofHdr: 999), OneVolume)));

    [Fact]
    public void Nifti1_ShorterThanHeader_Throws() =>
        Assert.Throws<MiqException>(() => NiftiParser.Parse(new byte[100]));

    [Fact]
    public void Nifti1_NdimZero_Throws() =>
        Assert.Throws<MiqException>(() =>
            NiftiParser.Parse(Nifti1File(Nifti1Header(ndim: 0), OneVolume)));

    [Fact]
    public void Nifti1_UnknownDatatypeCode_Throws()
    {
        var hdr = Nifti1Header();
        BinaryPrimitives.WriteInt16LittleEndian(hdr.AsSpan(70), 99);
        Assert.Throws<MiqException>(() => NiftiParser.Parse(Nifti1File(hdr, OneVolume)));
    }

    [Fact]
    public void Nifti1_BitpixContradictsDatatype_Throws() =>
        Assert.Throws<MiqException>(() =>
            NiftiParser.Parse(Nifti1File(Nifti1Header(bitpix: 8), OneVolume)));

    [Fact]
    public void Nifti1_SlicePlanePastCap_Throws() =>
        // 30000×30000 = 9e8 elements in one plane, past MaxSlicePlaneElements.
        Assert.Throws<MiqException>(() =>
            NiftiParser.Parse(Nifti1File(Nifti1Header(w: 30000, h: 30000, d: 1), OneVolume)));

    [Fact]
    public void Nifti1_PayloadShorterThanVolume0_Throws() =>
        Assert.Throws<MiqException>(() =>
            NiftiParser.Parse(Nifti1File(Nifti1Header(), OneVolume - 1)));

    // vox_offset is a float32 here. Before the guard, a bare (int) cast of an
    // out-of-range value yielded int.MinValue (measured on net8/x64 — it does not
    // saturate), which Math.Max floored back to 352: the file parsed and rendered
    // garbage from the wrong offset rather than reporting anything. These two are
    // the tests that caught that — both fail if NarrowVoxOffset is reverted.
    [Fact]
    public void Nifti1_VoxOffsetPastIntRange_Throws() =>
        Assert.Throws<MiqException>(() =>
            NiftiParser.Parse(Nifti1File(Nifti1Header(voxOffset: 1e10f), OneVolume)));

    [Fact]
    public void Nifti1_VoxOffsetInfinite_Throws() =>
        Assert.Throws<MiqException>(() =>
            NiftiParser.Parse(Nifti1File(Nifti1Header(voxOffset: float.PositiveInfinity), OneVolume)));

    // ── NIfTI-1: neighbouring inputs that must still be accepted ────────────

    [Theory]
    [InlineData(0f)]                      // the usual "data follows the header" spelling
    [InlineData(float.NaN)]               // junk, floored — as both targets already did
    [InlineData(-8f)]                     // negative, floored
    public void Nifti1_UnusableVoxOffset_FallsBackToHeaderEnd(float voxOffset)
    {
        var image = NiftiParser.Parse(Nifti1File(Nifti1Header(voxOffset: voxOffset), OneVolume));
        Assert.Equal(N1MinOffset, image.PayloadOffset);
    }

    [Fact]
    public void Nifti1_PayloadExactlyVolume0_Parses()
    {
        // Boundary: data.Length - VoxOffset == volumeBytes. One byte less throws
        // (above), so this pins the comparison against an off-by-one.
        var image = NiftiParser.Parse(Nifti1File(Nifti1Header(), OneVolume));
        Assert.Equal(4, image.Header.Width);
        Assert.Equal(N1MinOffset, image.PayloadOffset);
    }

    [Fact]
    public void Nifti1_FourDTruncatedPastVolume0_Parses()
    {
        // The payload guard is volume-0-only by design: a 4-D file holding just its
        // first volume is exactly what the vol-0-first partial loads produce, and
        // reads past PayloadCount zero-fill. Rejecting this would break partial loading.
        var header = Nifti1Header(ndim: 4, volumes: 6);
        var image = NiftiParser.Parse(Nifti1File(header, OneVolume));
        Assert.Equal(6, image.Header.Volumes);
    }

    [Fact]
    public void Nifti1_VoxOffsetPastHeaderForExtension_Parses()
    {
        // A large-but-legal offset (header extensions live between 348 and the
        // payload) must be honoured, not clamped or refused.
        const int offset = 5000;
        var image = NiftiParser.Parse(
            Nifti1File(Nifti1Header(voxOffset: offset), OneVolume, payloadOffset: offset));
        Assert.Equal(offset, image.PayloadOffset);
    }

    // ── NIfTI-2 ─────────────────────────────────────────────────────────────
    // 540-byte header. dim[] widens to int64 and vox_offset to int64, which is
    // where truncation to int turns an unrenderable size into a plausible one.

    const int N2HeaderBytes = 540;
    const int N2MinOffset = 544;

    static byte[] Nifti2Header(
        long ndim = 3, long w = 4, long h = 4, long d = 4, long volumes = 1,
        MiqDatatype datatype = MiqDatatype.Int16, long voxOffset = N2MinOffset,
        long[]? trailingDims = null)
    {
        var hdr = new byte[N2HeaderBytes];
        BinaryPrimitives.WriteInt32LittleEndian(hdr.AsSpan(0), 540);

        BinaryPrimitives.WriteInt16LittleEndian(hdr.AsSpan(12), (short)datatype);
        BinaryPrimitives.WriteInt16LittleEndian(
            hdr.AsSpan(14), (short)(datatype.BytesPerVoxel() * 8));

        var dims = new[] { ndim, w, h, d, volumes, 1L, 1L, 1L };
        if (trailingDims != null) // dim[4..7]: junk past ndim in real files
            trailingDims.CopyTo(dims, 4);
        for (var i = 0; i < 8; i++)
            BinaryPrimitives.WriteInt64LittleEndian(hdr.AsSpan(16 + i * 8), dims[i]);

        for (var i = 0; i < 4; i++)
            BinaryPrimitives.WriteDoubleLittleEndian(hdr.AsSpan(104 + i * 8), 1d);

        BinaryPrimitives.WriteInt64LittleEndian(hdr.AsSpan(168), voxOffset);
        return hdr;
    }

    static byte[] Nifti2File(byte[] header, int payloadBytes) =>
        Nifti1File(header, payloadBytes, payloadOffset: N2MinOffset);

    [Fact]
    public void Nifti2_Valid_Parses()
    {
        var image = NiftiParser.Parse(Nifti2File(Nifti2Header(), OneVolume));
        Assert.Equal(4, image.Header.Width);
        Assert.Equal(N2MinOffset, image.PayloadOffset);
    }

    [Fact]
    public void Nifti2_DimPastIntRange_Throws() =>
        // Truncating to int keeps the low 32 bits: 2^32+1 would become a perfectly
        // renderable 1, so the extent guards never see the value they exist to reject.
        Assert.Throws<MiqException>(() =>
            NiftiParser.Parse(Nifti2File(Nifti2Header(w: (1L << 32) + 1), OneVolume)));

    [Fact]
    public void Nifti2_NdimPastIntRange_Throws() =>
        // Same wrap on dim[0]: 2^32+3 would read back as a valid ndim of 3.
        Assert.Throws<MiqException>(() =>
            NiftiParser.Parse(Nifti2File(Nifti2Header(ndim: (1L << 32) + 3), OneVolume)));

    [Fact]
    public void Nifti2_VoxOffsetPastIntRange_Throws() =>
        Assert.Throws<MiqException>(() =>
            NiftiParser.Parse(Nifti2File(Nifti2Header(voxOffset: (1L << 32) + 544), OneVolume)));

    [Fact]
    public void Nifti2_JunkInDimSlotsPastNdim_Parses()
    {
        // Only dim[0..ndim] is meaningful; trailing slots routinely hold garbage.
        // Validating those too would turn away valid files.
        var header = Nifti2Header(ndim: 3, trailingDims: new[] { long.MaxValue, -1L, 1L << 40, long.MinValue });
        var image = NiftiParser.Parse(Nifti2File(header, OneVolume));
        Assert.Equal(4, image.Header.Width);
    }

    // ── MGH ─────────────────────────────────────────────────────────────────
    // 284-byte big-endian header, payload immediately after.

    const int MghHeaderBytes = 284;

    static byte[] MghFile(int w = 4, int h = 4, int d = 4, int nframes = 1,
                          int type = 0 /* uint8 */, int? payloadBytes = null)
    {
        var payload = payloadBytes ?? w * h * d * nframes;
        var file = new byte[MghHeaderBytes + Math.Max(0, payload)];
        BinaryPrimitives.WriteInt32BigEndian(file.AsSpan(0), 1); // version
        BinaryPrimitives.WriteInt32BigEndian(file.AsSpan(4), w);
        BinaryPrimitives.WriteInt32BigEndian(file.AsSpan(8), h);
        BinaryPrimitives.WriteInt32BigEndian(file.AsSpan(12), d);
        BinaryPrimitives.WriteInt32BigEndian(file.AsSpan(16), nframes);
        BinaryPrimitives.WriteInt32BigEndian(file.AsSpan(20), type);
        return file;
    }

    [Fact]
    public void Mgh_Valid_Parses()
    {
        var image = MghParser.Parse(MghFile());
        Assert.Equal(4, image.Header.Width);
    }

    [Fact]
    public void Mgh_PayloadShorterThanDeclared_Throws() =>
        Assert.Throws<MiqException>(() => MghParser.Parse(MghFile(payloadBytes: 4 * 4 * 4 - 1)));

    [Fact]
    public void Mgh_DimensionsThatWouldOverflowTheSizeMath_ThrowCleanly() =>
        // Upstream hit a hard trap here: `offset + payloadBytes` overflowed for a
        // dims product near long.MaxValue, crashing instead of reporting. The guards
        // are in subtraction form and the extent checks run first, so this must be
        // an ordinary MiqException — the assertion is "reports", not "which one".
        Assert.Throws<MiqException>(() =>
            MghParser.Parse(MghFile(w: 3577, h: 42799, d: 92737, nframes: 649657, payloadBytes: 0)));
}
