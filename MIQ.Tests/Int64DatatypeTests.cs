using System.Buffers.Binary;
using System.Text;
using MIQ.Parsing;
using MIQ.Rendering;
using Xunit;

namespace MIQ.Tests;

// 64-bit integer voxels (NIfTI 1024/1280, MIF Int64/UInt64, NRRD int64/uint64)
// used to be rejected, which broke numpy-generated label maps (int64 is NumPy's
// default integer dtype). Port of MIQ@2cf1764.
public class Int64DatatypeTests
{
    const int N1MinOffset = 352;

    /// NIfTI-1, little-endian, 8 bytes per voxel; voxel i holds valueAt(i).
    static byte[] Nifti1(MiqDatatype datatype, int w, int h, int d, Func<int, ulong> valueAt)
    {
        var n = w * h * d;
        var file = new byte[N1MinOffset + n * 8];
        var hdr = file.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(hdr.Slice(0), 348);
        BinaryPrimitives.WriteInt16LittleEndian(hdr.Slice(40), 3);
        BinaryPrimitives.WriteInt16LittleEndian(hdr.Slice(42), (short)w);
        BinaryPrimitives.WriteInt16LittleEndian(hdr.Slice(44), (short)h);
        BinaryPrimitives.WriteInt16LittleEndian(hdr.Slice(46), (short)d);
        BinaryPrimitives.WriteInt16LittleEndian(hdr.Slice(70), (short)datatype);
        BinaryPrimitives.WriteInt16LittleEndian(hdr.Slice(72), 64);
        for (var i = 0; i < 4; i++)
            BinaryPrimitives.WriteSingleLittleEndian(hdr.Slice(76 + i * 4), 1f);
        BinaryPrimitives.WriteSingleLittleEndian(hdr.Slice(108), N1MinOffset);
        for (var i = 0; i < n; i++)
            BinaryPrimitives.WriteUInt64LittleEndian(hdr.Slice(N1MinOffset + i * 8), valueAt(i));
        return file;
    }

    /// Same volume as int16, the reference the 64-bit label maps must match.
    static byte[] Nifti1Int16(int w, int h, int d, Func<int, short> valueAt)
    {
        var n = w * h * d;
        var file = new byte[N1MinOffset + n * 2];
        var hdr = file.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(hdr.Slice(0), 348);
        BinaryPrimitives.WriteInt16LittleEndian(hdr.Slice(40), 3);
        BinaryPrimitives.WriteInt16LittleEndian(hdr.Slice(42), (short)w);
        BinaryPrimitives.WriteInt16LittleEndian(hdr.Slice(44), (short)h);
        BinaryPrimitives.WriteInt16LittleEndian(hdr.Slice(46), (short)d);
        BinaryPrimitives.WriteInt16LittleEndian(hdr.Slice(70), (short)MiqDatatype.Int16);
        BinaryPrimitives.WriteInt16LittleEndian(hdr.Slice(72), 16);
        for (var i = 0; i < 4; i++)
            BinaryPrimitives.WriteSingleLittleEndian(hdr.Slice(76 + i * 4), 1f);
        BinaryPrimitives.WriteSingleLittleEndian(hdr.Slice(108), N1MinOffset);
        for (var i = 0; i < n; i++)
            BinaryPrimitives.WriteInt16LittleEndian(hdr.Slice(N1MinOffset + i * 2), valueAt(i));
        return file;
    }

    // ── Decode ──────────────────────────────────────────────────────────────

    [Fact]
    public void Nifti_Int64_DecodesBeyondInt32Range()
    {
        // -5e9 is below int32.MinValue and exact in float, so a 32-bit read can't pass.
        const long v = -5_000_000_000L;
        var image = NiftiParser.Parse(Nifti1(MiqDatatype.Int64, 2, 2, 2, _ => unchecked((ulong)v)));
        Assert.Equal(MiqDatatype.Int64, image.Header.Datatype);
        Assert.Equal("int64", image.Header.Datatype.Label());
        Assert.Equal(-5e9f, new MiqVolume(image).VoxelValue(1, 1, 1, 0));
    }

    [Fact]
    public void Nifti_Uint64_DecodesAboveInt64Max()
    {
        // 2^63 reads as long.MinValue if the unsigned read is missed.
        const ulong v = 1UL << 63;
        var image = NiftiParser.Parse(Nifti1(MiqDatatype.Uint64, 2, 2, 2, _ => v));
        Assert.Equal(MiqDatatype.Uint64, image.Header.Datatype);
        Assert.Equal(9.223372E18f, new MiqVolume(image).VoxelValue(1, 1, 1, 0));
    }

    [Theory]
    [InlineData("Int64LE", MiqDatatype.Int64)]
    [InlineData("UInt64BE", MiqDatatype.Uint64)]
    public void Mif_64BitTypes_Parse(string mifType, MiqDatatype expected)
    {
        var le = mifType.EndsWith("LE");
        var header = Encoding.ASCII.GetBytes(
            $"mrtrix image\ndim: 2,2,2\nvox: 1,1,1\nlayout: +0,+1,+2\ndatatype: {mifType}\nfile: .\nEND\n");
        var payload = new byte[8 * 8];
        for (var i = 0; i < 8; i++)
        {
            var span = payload.AsSpan(i * 8);
            if (le) BinaryPrimitives.WriteInt64LittleEndian(span, 6_000_000_000L + i);
            else BinaryPrimitives.WriteInt64BigEndian(span, 6_000_000_000L + i);
        }
        var image = MifParser.Parse([.. header, .. payload]);
        Assert.Equal(expected, image.Header.Datatype);
        Assert.Equal(6e9f, new MiqVolume(image).VoxelValue(0, 0, 0, 0));
    }

    [Theory]
    [InlineData("int64", MiqDatatype.Int64)]
    [InlineData("signed long long int", MiqDatatype.Int64)]
    [InlineData("uint64_t", MiqDatatype.Uint64)]
    [InlineData("unsigned long long", MiqDatatype.Uint64)]
    public void Nrrd_64BitTypes_Parse(string nrrdType, MiqDatatype expected)
    {
        var header = Encoding.ASCII.GetBytes(
            $"NRRD0004\ntype: {nrrdType}\ndimension: 3\nsizes: 2 2 2\nencoding: raw\nendian: little\n\n");
        var payload = new byte[8 * 8];
        for (var i = 0; i < 8; i++)
            BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(i * 8), 42 + i);
        var image = NrrdParser.Parse([.. header, .. payload]);
        Assert.Equal(expected, image.Header.Datatype);
        Assert.Equal(49f, new MiqVolume(image).VoxelValue(1, 1, 1, 0));
    }

    // ── Label detection ─────────────────────────────────────────────────────

    static readonly MiqRenderingOptions Auto = new(Segmentation: MiqSegmentationColoring.Auto);

    // FreeSurfer-style labels in contiguous 4³ blocks (piecewise constant).
    static readonly int[] LabelsFS = [0, 2, 3, 41, 42, 10, 11, 17, 251];
    const int N = 16;

    static int BlockLabel(int i)
    {
        int x = i % N, y = i / N % N, z = i / (N * N);
        return LabelsFS[(x / 4 + y / 4 * 4 + z / 4 * 16) % LabelsFS.Length];
    }

    [Theory]
    [InlineData(MiqDatatype.Int64)]
    [InlineData(MiqDatatype.Uint64)]
    public void LabelMap_DetectedSameAsInt16Equivalent(MiqDatatype datatype)
    {
        var reference = new MiqVolume(NiftiParser.Parse(Nifti1Int16(N, N, N, i => (short)BlockLabel(i))))
            .BuildSegmentationLut(Auto);
        Assert.NotNull(reference);

        var lut = new MiqVolume(NiftiParser.Parse(Nifti1(datatype, N, N, N, i => (ulong)BlockLabel(i))))
            .BuildSegmentationLut(Auto);
        Assert.NotNull(lut);
        Assert.Equal(reference!.IsFreeSurfer, lut!.IsFreeSurfer);
        Assert.Equal(reference.IsMonochromeWhite, lut.IsMonochromeWhite);
        Assert.True(lut.IsFreeSurfer);
    }

    static bool InBox(int i)
    {
        int x = i % N, y = i / N % N, z = i / (N * N);
        return x is >= 4 and < 12 && y is >= 4 and < 12 && z is >= 4 and < 12;
    }

    [Fact]
    public void Int64BinaryMask_IsConfirmedByFullScan()
    {
        // The center slices see one label, so ScanVolume0's 64-bit arm decides.
        var mask = new MiqVolume(NiftiParser.Parse(
            Nifti1(MiqDatatype.Int64, N, N, N, i => InBox(i) ? 5UL : 0UL)));
        Assert.True(mask.BuildSegmentationLut(Auto)!.IsMonochromeWhite);

        // A second label at (0,0,0), off every center plane, must be found by it.
        var twoLabels = new MiqVolume(NiftiParser.Parse(
            Nifti1(MiqDatatype.Int64, N, N, N, i => i == 0 ? 7UL : InBox(i) ? 5UL : 0UL)));
        var lut = twoLabels.BuildSegmentationLut(Auto);
        Assert.NotNull(lut);
        Assert.False(lut!.IsMonochromeWhite);
    }
}
