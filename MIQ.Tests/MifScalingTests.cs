using System.Text;
using MIQ.Parsing;
using MIQ.Rendering;
using Xunit;

namespace MIQ.Tests;

// MRtrix `scaling: offset,scale` (value = offset + scale × stored). Previously
// ignored, so the voxel readout showed raw stored integers and a negative scale
// rendered inverted.
public class MifScalingTests
{
    /// 2×2×2 uint8 MIF whose first voxel (or, with <paramref name="fill"/>, every
    /// voxel) stores 10. <paramref name="scaling"/> null omits the field;
    /// otherwise it is written verbatim.
    static byte[] MifFile(string? scaling, bool fill = false)
    {
        var header = new StringBuilder();
        header.Append("mrtrix image\n");
        header.Append("dim: 2,2,2\n");
        header.Append("vox: 1,1,1\n");
        header.Append("layout: +0,+1,+2\n");
        header.Append("datatype: UInt8\n");
        if (scaling is not null) header.Append("scaling: ").Append(scaling).Append('\n');
        header.Append("file: .\n");
        header.Append("END\n");

        var bytes = Encoding.ASCII.GetBytes(header.ToString());
        var file = new byte[bytes.Length + 8];
        bytes.CopyTo(file, 0);
        for (var i = 0; i < (fill ? 8 : 1); i++) file[bytes.Length + i] = 10;
        return file;
    }

    static MiqImage Parse(string? scaling, bool fill = false) =>
        MifParser.Parse(MifFile(scaling, fill));

    static float FirstVoxel(MiqImage image) => new MiqVolume(image).VoxelValue(0, 0, 0, 0);

    [Fact]
    public void Scaling_AppliesOffsetThenScale()
    {
        var image = Parse("5,2"); // offset 5, scale 2
        Assert.Equal(2f, image.Header.SclSlope);
        Assert.Equal(5f, image.Header.SclInter);
        Assert.Equal(25f, FirstVoxel(image)); // 5 + 2 × 10
    }

    [Fact]
    public void Scaling_ShowsInMetadata()
    {
        var lines = new MiqMetadata(Parse("5,2").Header, "MRtrix MIF", null).AsDisplayLines();
        Assert.Contains(lines, e => e.Label == "Scaling" && e.Value == "x 2 + 5");
    }

    // A uniform label-like volume colours when unscaled; non-identity scaling
    // marks it as intensity data, so it must fall back to windowing.
    [Fact]
    public void Scaling_NonIdentity_DisablesSegmentationColouring()
    {
        var auto = new MiqRenderingOptions(Segmentation: MiqSegmentationColoring.Auto);
        Assert.NotNull(new MiqVolume(Parse(null, fill: true)).BuildSegmentationLut(auto));
        Assert.Null(new MiqVolume(Parse("0,0.5", fill: true)).BuildSegmentationLut(auto));
    }

    [Theory]
    [InlineData(null)]          // absent
    [InlineData("0,1")]         // identity
    [InlineData("1,0")]         // zero scale
    [InlineData("nan,2")]       // non-finite
    [InlineData("5")]           // wrong arity
    [InlineData("abc,def")]     // malformed
    public void Scaling_AbsentOrUnusable_IsUnscaled(string? scaling)
    {
        var image = Parse(scaling);
        Assert.Equal(0f, image.Header.SclSlope);
        Assert.Equal(0f, image.Header.SclInter);
        Assert.Equal(10f, FirstVoxel(image));
    }
}
