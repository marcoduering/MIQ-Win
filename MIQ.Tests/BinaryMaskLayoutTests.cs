using System.Text;
using MIQ.Parsing;
using MIQ.Rendering;
using Xunit;

namespace MIQ.Tests;

// The volume-0 label scans take the contiguous path whenever volume 0 is the first
// W·H·D payload elements in *any* order, not only when ElementStrides is null.
// MifParser always sets strides, so keying off null sent every MIF mask down the
// per-voxel walk (~8× slower on a 256³ mask in MIQCore). These pin that dense MIF
// layouts take the fast path with the same verdict, and that a volume-interleaved
// layout still does not. Port of MIQ@8323423's BinaryMaskLayoutTests.
public class BinaryMaskLayoutTests
{
    static readonly MiqRenderingOptions Auto = new(Segmentation: MiqSegmentationColoring.Auto);

    // Distinct sizes so a stride mix-up can't cancel out. The box covers all three
    // center planes (x = 6, y = 5, z = 4); voxel (0,0,0) lies on none of them.
    static readonly int[] Dims = [12, 10, 8];

    static bool InBox(int x, int y, int z) => x is >= 3 and < 9 && y is >= 3 and < 7 && z is >= 2 and < 6;

    /// A uint8 MIF whose voxel (x, y, z, t), in `dim:` order, holds value(x, y, z, t),
    /// stored in the given layout (MRtrix storage ranks, all ascending).
    static MiqVolume MakeMif(int[] dims, int[] layout, Func<int, int, int, int, byte> value)
    {
        var n = dims.Length;
        var byRank = Enumerable.Range(0, n).OrderBy(a => layout[a]).ToArray();
        var strides = new int[n];
        var stride = 1;
        foreach (var axis in byRank) { strides[axis] = stride; stride *= dims[axis]; }

        var volumes = n > 3 ? dims[3] : 1;
        var payload = new byte[stride];
        for (var t = 0; t < volumes; t++)
            for (var z = 0; z < dims[2]; z++)
                for (var y = 0; y < dims[1]; y++)
                    for (var x = 0; x < dims[0]; x++)
                    {
                        var index = x * strides[0] + y * strides[1] + z * strides[2] + (n > 3 ? t * strides[3] : 0);
                        payload[index] = value(x, y, z, t);
                    }

        var header = Encoding.ASCII.GetBytes(
            "mrtrix image\n"
            + $"dim: {string.Join(",", dims)}\n"
            + $"vox: {string.Join(",", dims.Select(_ => "1"))}\n"
            + $"layout: {string.Join(",", layout.Select(r => "+" + r))}\n"
            + "datatype: UInt8\nfile: .\nEND\n");
        return new MiqVolume(MifParser.Parse([.. header, .. payload]));
    }

    [Theory]
    [InlineData(new[] { 0, 1, 2 })]
    [InlineData(new[] { 2, 0, 1 })]
    [InlineData(new[] { 1, 2, 0 })]
    [InlineData(new[] { 1, 0, 2 })]
    public void DenseMifMask_TakesContiguousScan(int[] layout)
    {
        var mask = MakeMif(Dims, layout, (x, y, z, _) => InBox(x, y, z) ? (byte)5 : (byte)0);
        Assert.True(mask.Volume0IsContiguous);
        Assert.True(mask.BuildSegmentationLut(Auto)!.IsMonochromeWhite);

        // A second label off the center planes must still be found by the full scan.
        var twoLabels = MakeMif(Dims, layout, (x, y, z, _) =>
            x == 0 && y == 0 && z == 0 ? (byte)7 : InBox(x, y, z) ? (byte)5 : (byte)0);
        var lut = twoLabels.BuildSegmentationLut(Auto);
        Assert.NotNull(lut);
        Assert.False(lut!.IsMonochromeWhite);
    }

    [Fact]
    public void VolumeSlowest4DMif_ScansOnlyVolumeZero()
    {
        // Volume 0 is a clean mask; volume 1 carries another label. The contiguous
        // scan stops at W·H·D elements, so volume 1 must not leak into the verdict.
        var volume = MakeMif([.. Dims, 2], [0, 1, 2, 3], (x, y, z, t) =>
            !InBox(x, y, z) ? (byte)0 : t == 0 ? (byte)5 : (byte)9);
        Assert.True(volume.Volume0IsContiguous);
        Assert.True(volume.BuildSegmentationLut(Auto)!.IsMonochromeWhite);
    }

    [Fact]
    public void VolumeFastest4DMif_FallsBackToPerVoxelWalk()
    {
        // DWI-style layout: the volume axis varies fastest, so the first W·H·D
        // elements interleave both timepoints. A contiguous scan would see volume
        // 1's label and misreport a multi-label volume.
        var volume = MakeMif([.. Dims, 2], [1, 2, 3, 0], (x, y, z, t) =>
            !InBox(x, y, z) ? (byte)0 : t == 0 ? (byte)5 : (byte)9);
        Assert.False(volume.Volume0IsContiguous);
        Assert.True(volume.BuildSegmentationLut(Auto)!.IsMonochromeWhite);
    }

    [Fact]
    public void RowMajorImage_IsContiguous()
    {
        var header = new MiqHeader
        {
            LittleEndian = true, Dimensions = [2, 2, 2], Pixdim = [1f, 1f, 1f, 1f],
            Datatype = MiqDatatype.Uint8, VoxOffset = 0, SclSlope = 0f, SclInter = 0f,
            QformCode = 0, SformCode = 0, SrowX = [1, 0, 0], SrowY = [0, 1, 0], SrowZ = [0, 0, 1],
        };
        var vol = new MiqVolume(new MiqImage { Header = header, Storage = new byte[8], PayloadOffset = 0 });
        Assert.True(vol.Volume0IsContiguous);
    }
}
