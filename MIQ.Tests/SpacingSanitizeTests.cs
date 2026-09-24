using System.Buffers.Binary;
using MIQ.Parsing;
using MIQ.Rendering;
using Xunit;

namespace MIQ.Tests;

// Voxel spacing at render time. Port of MIQ 1.5.1's sanitizedSpacing
// (MIQ@d6dabd5), plus the extent floor it exposed: once spacings below 1e-6
// are no longer raised to 1e-6, ResampleTargetSize's own 1e-6 floor on the
// reference extent starts to bite and shrinks the slice.
public class SpacingSanitizeTests
{
    /// 8×6×5 int16 NIfTI-1 with a gradient payload and the given pixdim[1..3].
    static MiqImage Nifti(float px, float py, float pz, short w = 8, short h = 6, short d = 5)
    {
        const int offset = 352;
        var voxels = w * h * d;
        var file = new byte[offset + voxels * 2];
        var hdr = file.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(hdr, 348);
        BinaryPrimitives.WriteInt16LittleEndian(hdr[40..], 3);
        BinaryPrimitives.WriteInt16LittleEndian(hdr[42..], w);
        BinaryPrimitives.WriteInt16LittleEndian(hdr[44..], h);
        BinaryPrimitives.WriteInt16LittleEndian(hdr[46..], d);
        BinaryPrimitives.WriteInt16LittleEndian(hdr[48..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(hdr[70..], (short)MiqDatatype.Int16);
        BinaryPrimitives.WriteInt16LittleEndian(hdr[72..], 16);
        BinaryPrimitives.WriteSingleLittleEndian(hdr[76..], 1f);
        BinaryPrimitives.WriteSingleLittleEndian(hdr[80..], px);
        BinaryPrimitives.WriteSingleLittleEndian(hdr[84..], py);
        BinaryPrimitives.WriteSingleLittleEndian(hdr[88..], pz);
        BinaryPrimitives.WriteSingleLittleEndian(hdr[108..], offset);
        for (var i = 0; i < voxels; i++)
            BinaryPrimitives.WriteInt16LittleEndian(hdr[(offset + i * 2)..], (short)(i * 7 % 500));
        return NiftiParser.Parse(file);
    }

    static IReadOnlyDictionary<SlicePlane, CenterSlice> Render(MiqImage image) =>
        new MiqVolume(image).CenterSlices(new MiqRenderingOptions());

    [Fact]
    public void ZeroSpacing_RendersLikeUnitSpacing()
    {
        var zero = Render(Nifti(1, 1, 0));
        var unit = Render(Nifti(1, 1, 1));
        foreach (var plane in unit.Keys)
        {
            var a = zero[plane].Image.Grayscale!;
            var b = unit[plane].Image.Grayscale!;
            Assert.Equal((b.Width, b.Height), (a.Width, a.Height));
            Assert.Equal(b.Pixels, a.Pixels);
        }
    }

    [Fact]
    public void ZeroSpacing_KeepsRawValueForMetadata() =>
        Assert.Equal(0f, Nifti(1, 1, 0).Header.Pixdim[3]);

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void NonFiniteSpacing_RendersLikeUnitSpacing(float bad)
    {
        var broken = Render(Nifti(1, 1, bad));
        var unit = Render(Nifti(1, 1, 1));
        foreach (var plane in unit.Keys)
            Assert.Equal(
                (unit[plane].Image.Width, unit[plane].Image.Height),
                (broken[plane].Image.Width, broken[plane].Image.Height));
    }

    [Fact]
    public void OverflowingSpacing_DoesNotCollapseToOnePixel()
    {
        // Finite, but 8 × 1e38 overflows float to +Inf in the extent arithmetic.
        foreach (var slice in Render(Nifti(1e38f, 1e38f, 1e38f)).Values)
            Assert.True(slice.Image.Width > 1 && slice.Image.Height > 1);
    }

    [Fact]
    public void SubMicroSpacing_KeepsFullResolution()
    {
        // 256 voxels at 1e-9: the largest extent (2.56e-7) is under the old 1e-6
        // reference floor, which rendered the slice at ~66 px instead of 256.
        var tiny = Render(Nifti(1e-9f, 1e-9f, 1e-9f, w: 256, h: 4, d: 4));
        var unit = Render(Nifti(1, 1, 1, w: 256, h: 4, d: 4));
        foreach (var plane in unit.Keys)
            Assert.Equal(
                (unit[plane].Image.Width, unit[plane].Image.Height),
                (tiny[plane].Image.Width, tiny[plane].Image.Height));
    }

    [Theory]
    [InlineData(0f, 1f)]
    [InlineData(-2.5f, 2.5f)]
    [InlineData(float.NaN, 1f)]
    [InlineData(float.NegativeInfinity, 1f)]
    [InlineData(1e-9f, 1e-9f)]
    public void SanitizedSpacing_Maps(float input, float expected) =>
        Assert.Equal(expected, MiqVolume.SanitizedSpacing(input));

    [Fact]
    public void ResampleTarget_FallsBackOnNonFiniteExtent() =>
        Assert.Equal((256, 20),
            ResampleTarget.Size(256, 20, float.PositiveInfinity, 1f, float.PositiveInfinity, 512)!.Value);
}
