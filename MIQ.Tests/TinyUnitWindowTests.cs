using MIQ.Rendering;
using Xunit;

namespace MIQ.Tests;

// IntensityWindow.GetBounds preferred a foreground subset of |v| > 1e-6. Data in
// tiny units (an SI-unit ADC map, ~1e-9) sits entirely below that floor, so its
// window was computed over tissue plus the background zeros. Exactly-non-zero
// values now stand in when the floored subset is too small. Port of MIQ@bcf3e18.
public class TinyUnitWindowTests
{
    /// The previous GetBounds, verbatim: the reference for data the change must
    /// leave alone.
    static IntensityWindow.Bounds? LegacyBounds(IReadOnlyList<float> values, double lo, double hi)
    {
        var finite = values.Where(float.IsFinite).ToList();
        if (finite.Count == 0) return null;
        var nonZero = finite.Where(v => Math.Abs(v) > 1e-6f).ToList();
        var source = nonZero.Count >= Math.Max(64, finite.Count / 20) ? nonZero : finite;
        source.Sort();
        float Pct(float p)
        {
            if (source.Count == 1) return source[0];
            var pos = Math.Max(0f, Math.Min(1f, p)) * (source.Count - 1);
            int l = (int)Math.Floor((double)pos), u = (int)Math.Ceiling((double)pos);
            if (l == u) return source[l];
            var f = pos - l;
            return source[l] * (1 - f) + source[u] * f;
        }
        var lower = Pct((float)lo / 100f);
        var upper = Pct((float)hi / 100f);
        return lower < upper
            ? new IntensityWindow.Bounds(lower, upper)
            : new IntensityWindow.Bounds(finite.Min(), finite.Max());
    }

    /// A center slice in tiny units: 60% exact-zero background, tissue spread over
    /// 0.5e-9 … 3e-9. Every value sits below the 1e-6 floor.
    static float[] TinyUnitSlice(float? outlier = null)
    {
        var values = new float[10_000];
        for (var i = 0; i < values.Length; i++)
            values[i] = i % 5 < 3 ? 0f : 0.5e-9f + (i % 997) / 996f * 2.5e-9f;
        if (outlier is { } o) values[1] = o;
        return values;
    }

    [Fact]
    public void TinyUnitData_WindowsOverItsTissue()
    {
        var b = IntensityWindow.GetBounds(TinyUnitSlice(), 2, 98);
        Assert.NotNull(b);
        // Lower bound comes from the tissue, not from the background zeros.
        Assert.True(b!.Value.Low > 0.5e-9f, $"low {b.Value.Low}");
        Assert.True(b.Value.High < 3e-9f, $"high {b.Value.High}");

        // Before, the zeros pulled the lower bound down to 0.
        Assert.Equal(0f, LegacyBounds(TinyUnitSlice(), 2, 98)!.Value.Low);
    }

    [Fact]
    public void TinyUnitWindow_IgnoresAnOutlier()
    {
        // A failed-fit voxel must not drag the window: the fallback is a plain
        // non-zero test, not a floor scaled by the data's maximum.
        var b = IntensityWindow.GetBounds(TinyUnitSlice(outlier: 1e30f), 2, 98);
        Assert.NotNull(b);
        Assert.True(b!.Value.Low > 0.5e-9f, $"low {b.Value.Low}");
        Assert.True(b.Value.High < 3e-9f, $"high {b.Value.High}");
    }

    [Fact]
    public void Floor_StillDropsNearZeroResidue_OnOrdinaryData()
    {
        // Ordinary-unit signal plus interpolation residue in (0, 1e-6]: the floor
        // tier qualifies, so the residue stays out of the window, as before.
        var values = new float[10_000];
        for (var i = 0; i < values.Length; i++)
            values[i] = i % 2 == 0 ? (i % 7 + 1) * 1e-8f : 100 + i % 500;
        var b = IntensityWindow.GetBounds(values, 2, 98);
        Assert.NotNull(b);
        Assert.True(b!.Value.Low >= 100, $"low {b.Value.Low}");
        AssertSame(LegacyBounds(values, 2, 98), b);
    }

    public static TheoryData<string> OrdinaryShapes => new() { "anatomical", "sparse", "all-zero", "few-voxels" };

    [Theory]
    [MemberData(nameof(OrdinaryShapes))]
    public void OrdinaryData_IsBitIdenticalToBefore(string shape)
    {
        var rng = new Random(7);
        float[] values = shape switch
        {
            // Mostly foreground in ordinary units.
            "anatomical" => Enumerable.Range(0, 20_000)
                .Select(i => i % 4 == 0 ? 0f : (float)(rng.NextDouble() * 1500)).ToArray(),
            // Foreground below the /20 ratio: both before and after, all finite values.
            "sparse" => Enumerable.Range(0, 20_000)
                .Select(i => i % 50 == 0 ? (float)(rng.NextDouble() * 10) : 0f).ToArray(),
            "all-zero" => new float[4096],
            // Fewer than 64 non-zero voxels.
            _ => Enumerable.Range(0, 500).Select(i => i < 30 ? 3e-9f * (i + 1) : 0f).ToArray(),
        };
        AssertSame(LegacyBounds(values, 2, 98), IntensityWindow.GetBounds(values, 2, 98));
    }

    static void AssertSame(IntensityWindow.Bounds? expected, IntensityWindow.Bounds? actual)
    {
        Assert.Equal(expected.HasValue, actual.HasValue);
        if (expected is not { } e) return;
        Assert.Equal(e.Low, actual!.Value.Low);
        Assert.Equal(e.High, actual.Value.High);
    }
}
