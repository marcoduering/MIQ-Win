using System.Buffers.Binary;
using MIQ.Parsing;
using MIQ.Rendering;
using Xunit;

namespace MIQ.Tests;

// Coverage for non-finite scl_slope / scl_inter in the NIfTI header.
//
// nibabel routinely writes NaN scaling (see NiftiParser.NormalizeScaling), so such
// files must preview normally. Same both-halves discipline as ParserGuardTests:
// each non-finite input has a neighbour pinning the finite value left alone.
public class NanHandlingTests
{
    const int N1MinOffset = 352;
    const int N2MinOffset = 544;

    // 2x2x2 uint8, every voxel = 7, so a scaling slip shows up as a value change.
    const int Dim = 2;
    const byte RawValue = 7;

    static byte[] Nifti1(float sclSlope = 0f, float sclInter = 0f)
    {
        var file = new byte[N1MinOffset + Dim * Dim * Dim];
        var hdr = file.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(hdr.Slice(0), 348);
        BinaryPrimitives.WriteInt16LittleEndian(hdr.Slice(40), 3);
        BinaryPrimitives.WriteInt16LittleEndian(hdr.Slice(42), Dim);
        BinaryPrimitives.WriteInt16LittleEndian(hdr.Slice(44), Dim);
        BinaryPrimitives.WriteInt16LittleEndian(hdr.Slice(46), Dim);
        BinaryPrimitives.WriteInt16LittleEndian(hdr.Slice(70), (short)MiqDatatype.Uint8);
        BinaryPrimitives.WriteInt16LittleEndian(hdr.Slice(72), 8);
        for (var i = 0; i < 4; i++) // pixdim: qfac + 1 mm isotropic
            BinaryPrimitives.WriteSingleLittleEndian(hdr.Slice(76 + i * 4), 1f);
        BinaryPrimitives.WriteSingleLittleEndian(hdr.Slice(108), N1MinOffset);
        BinaryPrimitives.WriteSingleLittleEndian(hdr.Slice(112), sclSlope);
        BinaryPrimitives.WriteSingleLittleEndian(hdr.Slice(116), sclInter);
        for (var i = N1MinOffset; i < file.Length; i++) file[i] = RawValue;
        return file;
    }

    static byte[] Nifti2(double sclSlope = 0d, double sclInter = 0d)
    {
        var file = new byte[N2MinOffset + Dim * Dim * Dim];
        var hdr = file.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(hdr.Slice(0), 540);
        BinaryPrimitives.WriteInt16LittleEndian(hdr.Slice(12), (short)MiqDatatype.Uint8);
        BinaryPrimitives.WriteInt16LittleEndian(hdr.Slice(14), 8);
        var dims = new[] { 3L, Dim, Dim, Dim, 1L, 1L, 1L, 1L };
        for (var i = 0; i < dims.Length; i++)
            BinaryPrimitives.WriteInt64LittleEndian(hdr.Slice(16 + i * 8), dims[i]);
        for (var i = 0; i < 4; i++)
            BinaryPrimitives.WriteDoubleLittleEndian(hdr.Slice(104 + i * 8), 1d);
        BinaryPrimitives.WriteInt64LittleEndian(hdr.Slice(168), N2MinOffset);
        BinaryPrimitives.WriteDoubleLittleEndian(hdr.Slice(176), sclSlope);
        BinaryPrimitives.WriteDoubleLittleEndian(hdr.Slice(184), sclInter);
        for (var i = N2MinOffset; i < file.Length; i++) file[i] = RawValue;
        return file;
    }

    static float FirstVoxel(MiqImage image) => new MiqVolume(image).VoxelValue(0, 0, 0, 0);

    [Theory]
    [InlineData(float.NaN, 0f)]                 // nibabel's marker for "undefined"
    [InlineData(float.PositiveInfinity, 0f)]
    [InlineData(2f, float.NaN)]                 // a finite slope is unusable with a NaN intercept
    [InlineData(float.NaN, float.NaN)]
    public void Nifti1_NonFiniteScaling_IsTreatedAsNoScaling(float slope, float inter)
    {
        // Must render the stored values, not NaN (`slope != 0` is true for NaN).
        var image = NiftiParser.Parse(Nifti1(sclSlope: slope, sclInter: inter));
        Assert.Equal(RawValue, FirstVoxel(image));
    }

    [Fact]
    public void Nifti1_FiniteScaling_StillApplies()
    {
        // The other half: a real slope/intercept must survive untouched.
        var image = NiftiParser.Parse(Nifti1(sclSlope: 2f, sclInter: 1f));
        Assert.Equal(RawValue * 2f + 1f, FirstVoxel(image));
    }

    [Fact]
    public void Nifti1_SlopeZero_RemainsUnscaled()
    {
        // The format's existing spelling for "unscaled" — the state non-finite
        // scaling is normalised INTO — must keep behaving as it always did.
        var image = NiftiParser.Parse(Nifti1(sclSlope: 0f, sclInter: 5f));
        Assert.Equal(RawValue, FirstVoxel(image));
    }

    [Fact]
    public void Nifti2_NonFiniteScaling_IsTreatedAsNoScaling()
    {
        // NIfTI-2 reads scaling from different offsets as float64 — its own code path.
        var image = NiftiParser.Parse(Nifti2(sclSlope: double.NaN));
        Assert.Equal(RawValue, FirstVoxel(image));
    }

    [Fact]
    public void Nifti2_FiniteScaling_StillApplies()
    {
        var image = NiftiParser.Parse(Nifti2(sclSlope: 3d, sclInter: 2d));
        Assert.Equal(RawValue * 3f + 2f, FirstVoxel(image));
    }
}
