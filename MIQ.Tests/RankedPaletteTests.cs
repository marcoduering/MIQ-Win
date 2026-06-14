using MIQ.Rendering;
using Xunit;

namespace MIQ.Tests;

public class RankedPaletteTests
{
    static (byte r, byte g, byte b) Color(SegmentationLut lut, int label)
    {
        var buf = new byte[3];
        lut.Write(label, buf, 0);
        return (buf[0], buf[1], buf[2]);
    }

    // Standard RGB → hue (degrees, 0..360).
    static float HueDeg(byte r, byte g, byte b)
    {
        float rf = r / 255f, gf = g / 255f, bf = b / 255f;
        float max = Math.Max(rf, Math.Max(gf, bf));
        float min = Math.Min(rf, Math.Min(gf, bf));
        float delta = max - min;
        if (delta < 1e-4f) return 0f;
        float hue;
        if (max == rf) hue = (gf - bf) / delta % 6;
        else if (max == gf) hue = (bf - rf) / delta + 2;
        else hue = (rf - gf) / delta + 4;
        hue *= 60f;
        if (hue < 0) hue += 360f;
        return hue;
    }

    // Circular distance on the hue wheel.
    static float HueDist(float a, float b)
    {
        float d = Math.Abs(a - b) % 360f;
        return d > 180f ? 360f - d : d;
    }

    [Theory]
    [InlineData(new[] { 1, 15, 29 })]
    [InlineData(new[] { 1, 5 })]
    [InlineData(new[] { 10, 20, 30, 40 })]
    public void Spread_MinPairwiseHueGapApproxEqualToThreeSixtyOverN(int[] labelArray)
    {
        var labels = new HashSet<int>(labelArray);
        var lut = SegmentationLut.Random(labels);
        int n = labelArray.Length;
        float expected = 360f / n;

        var hues = labelArray
            .Select(l => { var (r, g, b) = Color(lut, l); return HueDeg(r, g, b); })
            .ToArray();

        float minGap = float.MaxValue;
        for (int i = 0; i < hues.Length; i++)
            for (int j = i + 1; j < hues.Length; j++)
                minGap = Math.Min(minGap, HueDist(hues[i], hues[j]));

        Assert.True(minGap >= expected - 12f,
            $"Min hue gap {minGap:F1}° < {expected - 12f:F1}° for n={n}");
    }

    [Fact]
    public void Determinism_SameLabelSetProducesIdenticalColors()
    {
        var labels = new HashSet<int> { 1, 15, 29, 42, 100 };
        var lut1 = SegmentationLut.Random(labels);
        var lut2 = SegmentationLut.Random(labels);
        foreach (var l in labels)
            Assert.Equal(Color(lut1, l), Color(lut2, l));
    }

    [Fact]
    public void UnknownLabelFallback_IsNonBlackAndConsistentAcrossSets()
    {
        // Label 999 is absent from both sets; the per-label hash fallback must
        // produce the same color regardless of which set the LUT was built from.
        var lut1 = SegmentationLut.Random(new HashSet<int> { 1, 2, 3 });
        var lut2 = SegmentationLut.Random(new HashSet<int> { 10, 20, 30 });
        var c1 = Color(lut1, 999);
        var c2 = Color(lut2, 999);
        Assert.Equal(c1, c2);
        Assert.True(c1.r > 0 || c1.g > 0 || c1.b > 0, "Fallback color for unknown label must be non-black");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(10)]
    [InlineData(20)]
    [InlineData(40)]
    public void NoCollisions_NDistinctLabelsProduceNDistinctColors(int n)
    {
        var labels = new HashSet<int>(Enumerable.Range(1, n));
        var lut = SegmentationLut.Random(labels);
        var colors = labels.Select(l => Color(lut, l)).ToHashSet();
        Assert.Equal(n, colors.Count);
    }
}
