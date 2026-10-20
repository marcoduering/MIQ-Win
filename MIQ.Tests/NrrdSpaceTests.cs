using System.Text;
using MIQ.Parsing;
using Xunit;

namespace MIQ.Tests;

// NRRD orientation comes only from an anatomical `space` combined with
// `space directions`. Port of MIQ 1.6.3's "NRRD: no assumed RAS" fix
// (MIQ@71ff902): a missing or non-anatomical space, or a missing
// `space directions`, used to be silently treated as RAS — so the reoriented
// view modes could show a confidently wrong left/right. Now such files carry
// no OrientationFrame (and fall back to stored), while the spacing is kept.
public class NrrdSpaceTests
{
    /// 4×3×2 uint8 NRRD; a null field is omitted from the header.
    static byte[] Nrrd(string? space, string? spaceDirections = "(1,0,0) (0,1,0) (0,0,1)",
                       string? spacings = null)
    {
        var lines = new[]
        {
            "NRRD0004", "type: uint8", "dimension: 3",
            space == null ? null : $"space: {space}",
            "sizes: 4 3 2",
            spaceDirections == null ? null : $"space directions: {spaceDirections}",
            spacings == null ? null : $"spacings: {spacings}",
            "kinds: domain domain domain", "endian: little", "encoding: raw",
            space == null ? null : "space origin: (0,0,0)",
        }.Where(l => l != null);
        var header = string.Join("\n", lines) + "\n\n";
        return [.. Encoding.ASCII.GetBytes(header), .. new byte[4 * 3 * 2]];
    }

    [Fact]
    public void AnatomicalSpaceWithDirections_BuildsFrame()
    {
        var h = NrrdParser.Parse(Nrrd("left-posterior-superior")).Header;

        Assert.Equal("LPS", h.OrientationFrame!.Label);
        Assert.Equal(1, h.SformCode);
    }

    [Fact]
    public void NoSpace_BuildsNoFrameButKeepsSpacing()
    {
        var h = NrrdParser.Parse(Nrrd(space: null, spaceDirections: null, spacings: "0.5 2 3")).Header;

        Assert.Null(h.OrientationFrame);
        Assert.Equal(0, h.SformCode);
        Assert.Equal(new[] { 1f, 0.5f, 2f, 3f }, h.Pixdim);
    }

    [Fact]
    public void AnatomicalSpaceWithoutDirections_BuildsNoFrame()
    {
        // `space` names the world frame, but without `space directions` nothing
        // says how the storage axes lie in it.
        var h = NrrdParser.Parse(Nrrd("left-posterior-superior", spaceDirections: null, spacings: "0.5 2 3")).Header;

        Assert.Null(h.OrientationFrame);
        Assert.Equal(0, h.SformCode);
        Assert.Equal(new[] { 1f, 0.5f, 2f, 3f }, h.Pixdim);
    }

    [Fact]
    public void NonAnatomicalSpace_BuildsNoFrameButKeepsSpacing()
    {
        var h = NrrdParser.Parse(Nrrd("scanner-xyz", "(2,0,0) (0,1,0) (0,0,1)")).Header;

        Assert.Null(h.OrientationFrame);
        Assert.Equal(0, h.SformCode);
        Assert.Equal(new[] { 1f, 2f, 1f, 1f }, h.Pixdim);
    }

    [Theory]
    [InlineData("left-posterior-superior-time")]
    [InlineData("LPST")]
    public void TimeSpace_KeepsAnatomicalFrame(string space)
    {
        // A "-time" space has 4-component directions; the time axis is (0,0,0,dt).
        var header = string.Join("\n",
            "NRRD0004", "type: uint8", "dimension: 4", $"space: {space}", "sizes: 4 3 2 2",
            "space directions: (2,0,0,0) (0,1,0,0) (0,0,1,0) (0,0,0,1.5)",
            "kinds: domain domain domain time", "endian: little", "encoding: raw",
            "space origin: (0,0,0,0)", "", "");
        byte[] data = [.. Encoding.ASCII.GetBytes(header), .. new byte[4 * 3 * 2 * 2]];

        var h = NrrdParser.Parse(data).Header;

        Assert.Equal(new[] { 4, 3, 2, 2 }, h.Dimensions);
        Assert.Equal(new[] { 1f, 2f, 1f, 1f }, h.Pixdim);
        Assert.Equal("LPS", h.OrientationFrame!.Label);
        Assert.Equal(1, h.SformCode);
    }
}
