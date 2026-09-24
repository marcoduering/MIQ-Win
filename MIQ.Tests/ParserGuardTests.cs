using System.Buffers.Binary;
using System.Text;
using MIQ.Parsing;
using MIQ.Rendering;
using Xunit;

namespace MIQ.Tests;

// Negative-path coverage for the parser guards.
//
// golden.json proves that files which are *accepted* still render identically. It
// says nothing about rejection logic, which is what every guard here is — and a
// guard that becomes too strict fails silently, because the valid file it starts
// turning away was never in the corpus. So each guard gets both halves: the input
// it exists to reject, and a neighbouring input it must still accept.
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

    // vox_offset is a float32 here. A bare (int) cast of an out-of-range value
    // yields int.MinValue, which would be floored back to 352 and render garbage.
    // Both fail if NarrowVoxOffset is reverted.
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
        // A dims product near long.MaxValue must be reported as a MiqException, not
        // overflow (upstream trapped here). Which exception doesn't matter.
        Assert.Throws<MiqException>(() =>
            MghParser.Parse(MghFile(w: 3577, h: 42799, d: 92737, nframes: 649657, payloadBytes: 0)));

    // ── MIF: the Bit datatype ───────────────────────────────────────────────
    //
    // MRtrix writes masks as `Bit`: 8 voxels per byte, MSB-first, packed
    // contiguously across the whole payload with no per-row or per-slice
    // alignment. MifParser unpacks it to one byte per voxel at the parse
    // boundary, so no sub-byte width ever reaches the renderer. These pin the two
    // decisions that separate a correct mask from noise — the bit order, and the
    // ceil(n/8) payload sizing — plus the accept half of each.

    /// Minimal MIF: ASCII header with `file: .`, so the payload follows END directly.
    static byte[] MifFile(string datatype, int[] dim, byte[] payload)
    {
        var axes = string.Join(",", Enumerable.Range(0, dim.Length).Select(i => "+" + i));
        var text = "mrtrix image\n"
                 + "dim: " + string.Join(",", dim) + "\n"
                 + "vox: " + string.Join(",", dim.Select(_ => "1")) + "\n"
                 + "layout: " + axes + "\n"
                 + "datatype: " + datatype + "\n"
                 + "file: .\n"
                 + "END\n";
        var head = Encoding.ASCII.GetBytes(text);
        var file = new byte[head.Length + payload.Length];
        head.CopyTo(file.AsSpan());
        payload.CopyTo(file.AsSpan(head.Length));
        return file;
    }

    [Fact]
    public void Mif_Bit_UnpacksMsbFirstWithinEachByte()
    {
        // 0x96 = 1001 0110. MSB-first (MRtrix) takes voxel 0 from bit 7, so the
        // elements read left-to-right as written; LSB-first would reverse each byte,
        // which on a real mask combs every boundary at an 8-voxel period.
        var image = MifParser.Parse(MifFile("Bit", new[] { 4, 2, 1 }, new byte[] { 0x96 }));

        // Unpacked to a standalone uint8 buffer, so element i is simply Storage[i].
        Assert.Equal(MiqDatatype.Uint8, image.Header.Datatype);
        Assert.Equal(0, image.PayloadOffset);
        Assert.Equal(new byte[] { 1, 0, 0, 1, 0, 1, 1, 0 }, image.Storage);
    }

    [Fact]
    public void Mif_Bit_DropsTrailingPaddingBits()
    {
        // 9 voxels = 2 bytes, so the final byte holds 1 real bit and 7 of padding.
        // Padding must not become voxels: 0xFF would otherwise add 7 stray foreground
        // values, which on a mask reads as a bright smear past the last real voxel.
        var image = MifParser.Parse(MifFile("Bit", new[] { 3, 3, 1 }, new byte[] { 0x00, 0xFF }));

        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 1 }, image.Storage);
    }

    [Fact]
    public void Mif_Bit_PayloadOneByteShort_Throws() =>
        // 9 voxels need ceil(9/8) = 2 bytes.
        Assert.Throws<MiqException>(() =>
            MifParser.Parse(MifFile("Bit", new[] { 3, 3, 1 }, new byte[1])));

    [Fact]
    public void Mif_Bit_PayloadExactlyCeilOfEighth_Parses()
    {
        // The accept half of the guard above, and the one that would fail if the
        // size were computed as n * BytesPerVoxel() — the datatype now reports uint8,
        // so that spelling would demand 9 bytes and reject every real bit file.
        var image = MifParser.Parse(MifFile("Bit", new[] { 3, 3, 1 }, new byte[2]));

        Assert.Equal(3, image.Header.Width);
        Assert.Equal(9, image.Storage.Length);
    }

    [Fact]
    public void Mif_Bit_MetadataReportsTheHeaderSpelling()
    {
        // Datatype is widened to uint8 for the renderer; the panel must still say what
        // the file says, or a Bit mask silently claims to be something it is not.
        var image = MifParser.Parse(MifFile("Bit", new[] { 4, 2, 1 }, new byte[] { 0x96 }));
        var lines = new MiqMetadata(image.Header, "MIF", null).AsDisplayLines();

        Assert.Contains(lines, l => l.Label == "Datatype" && l.Value == "bit");
    }

    [Fact]
    public void Mif_NonBitDatatype_IsUnaffected()
    {
        // The other half of the ParseDatatype change: ordinary MIF still reports its
        // own datatype, keeps the file-backed payload offset, and sets no label override.
        var image = MifParser.Parse(MifFile("UInt16LE", new[] { 2, 2, 2 }, new byte[16]));

        Assert.Equal(MiqDatatype.Uint16, image.Header.Datatype);
        Assert.Null(image.Header.DatatypeLabel);
        Assert.True(image.PayloadOffset > 0);
    }
}
