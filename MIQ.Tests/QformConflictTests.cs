using System.Buffers.Binary;
using MIQ.Parsing;
using MIQ.Rendering;
using Xunit;

namespace MIQ.Tests;

// The Orientation row names the qform only when its axes disagree with the
// sform's. Port of MIQ's QformConflictTests (metaimage-mha branch, MIQ@9f6a604).
public class QformConflictTests
{
    /// Orientation row for a 6×4×3 uint8 NIfTI-1 with the given sform x/y rows
    /// (z row +S) and a qform rotated by <paramref name="quaternD"/> about z
    /// (0 = identity, RAS).
    static string? OrientationRow(float[] srowX, float[]? srowY = null,
                                  short qformCode = 1, float quaternD = 0)
    {
        srowY ??= [0, 1, 0, 0];
        var file = new byte[352 + 6 * 4 * 3];
        var h = file.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(h, 348);
        short[] dims = [3, 6, 4, 3];
        for (var i = 0; i < dims.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(h[(40 + i * 2)..], dims[i]);
        BinaryPrimitives.WriteInt16LittleEndian(h[70..], (short)MiqDatatype.Uint8);
        BinaryPrimitives.WriteInt16LittleEndian(h[72..], 8);
        for (var i = 0; i < 4; i++)
            BinaryPrimitives.WriteSingleLittleEndian(h[(76 + i * 4)..], 1f); // qfac = 1
        BinaryPrimitives.WriteSingleLittleEndian(h[108..], 352);
        BinaryPrimitives.WriteInt16LittleEndian(h[252..], qformCode);
        BinaryPrimitives.WriteInt16LittleEndian(h[254..], 1); // sform_code
        BinaryPrimitives.WriteSingleLittleEndian(h[264..], quaternD);
        float[][] rows = [srowX, srowY, [0, 0, 1, 0]];
        for (var r = 0; r < 3; r++)
            for (var c = 0; c < 4; c++)
                BinaryPrimitives.WriteSingleLittleEndian(h[(280 + r * 16 + c * 4)..], rows[r][c]);

        var header = NiftiParser.Parse(file).Header;
        return new MiqMetadata(header, "NIfTI-1", header.OrientationFrame?.Label).AsDisplayLines()
            .FirstOrDefault(e => e.Label == "Orientation")?.Value;
    }

    [Fact]
    public void DisagreeingAxes_NameTheQform() =>
        Assert.Equal("LAS (qform: RAS)", OrientationRow([-1, 0, 0, 0]));

    [Fact]
    public void SwappedAxes_NameTheQform() =>
        Assert.Equal("ARS (qform: RAS)", OrientationRow([0, 1, 0, 0], [1, 0, 0, 0]));

    [Fact]
    public void RoundingDifferences_AreIgnored() =>
        Assert.Equal("RAS", OrientationRow([0.9999f, 0.0001f, 0, 0], [-0.0001f, 1.0001f, 0, 0]));

    /// An oblique scan rotated ~45° about z: the sform (45.01°) reads ALS, the
    /// qform (44.99°) RAS, but the two transforms are 0.02° apart, so this is no
    /// conflict.
    [Fact]
    public void ObliqueTieBreak_IsIgnored()
    {
        var s = 45.01 * Math.PI / 180;
        var q = 44.99 * Math.PI / 180;
        Assert.Equal("ALS", OrientationRow(
            [(float)Math.Cos(s), (float)-Math.Sin(s), 0, 0],
            [(float)Math.Sin(s), (float)Math.Cos(s), 0, 0],
            quaternD: (float)Math.Sin(q / 2)));
    }

    [Fact]
    public void AgreeingOrMissingQform_ShowsOnlyTheLabel()
    {
        Assert.Equal("RAS", OrientationRow([1, 0, 0, 0]));
        Assert.Equal("LAS", OrientationRow([-1, 0, 0, 0], qformCode: 0));
    }
}
