using MIQ.Rendering;
using Xunit;

namespace MIQ.Tests;

// IntensityWindow.Apply used an absolute 1e-6 floor on the window width, which
// squashed float data spanning less than that (SI-unit ADC maps, ~1e-9) into one
// or two grey levels. Port of MIQ 1.5.1 (MIQ@d6dabd5).
public class WindowWidthTests
{
    /// The previous implementation, verbatim — the reference for windows >= 1e-6.
    static byte[] LegacyApply(float[] values, IntensityWindow.Bounds bounds)
    {
        var range = Math.Max(bounds.High - bounds.Low, 1e-6f);
        var outp = new byte[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            var value = values[i];
            if (!float.IsFinite(value)) { outp[i] = 0; continue; }
            var clipped = Math.Max(bounds.Low, Math.Min(bounds.High, value));
            var unit = Math.Max(0f, Math.Min(1f, (clipped - bounds.Low) / range));
            outp[i] = (byte)(int)Math.Round((double)(unit * 255f), MidpointRounding.ToEven);
        }
        return outp;
    }

    [Fact]
    public void SubMicroWindow_SpansFullGreyRange()
    {
        var values = Enumerable.Range(0, 256).Select(i => i * 2e-9f / 255).ToArray();
        var bounds = IntensityWindow.GetBounds(values, 0, 100);
        Assert.NotNull(bounds);

        foreach (var pixels in new[]
                 {
                     IntensityWindow.Apply(values, bounds.Value),
                     IntensityWindow.Apply((IReadOnlyList<float>)values, bounds.Value),
                 })
        {
            Assert.Equal(0, pixels[0]);
            Assert.Equal(255, pixels[^1]);
            for (var i = 1; i < pixels.Length; i++)
                Assert.True(pixels[i - 1] <= pixels[i]);
        }
    }

    [Fact]
    public void NormalWindow_IsByteIdenticalToLegacy()
    {
        var rng = new Random(1234);
        var values = Enumerable.Range(0, 4096).Select(_ => (float)(rng.NextDouble() * 2000 - 200)).ToArray();
        var bounds = IntensityWindow.GetBounds(values, 2, 98)!.Value;
        Assert.Equal(LegacyApply(values, bounds), IntensityWindow.Apply(values, bounds));

        // Exactly at the old floor: max(1e-6, 1e-6) == 1e-6, so still identical.
        var exactFloor = new IntensityWindow.Bounds(0, 1e-6f);
        var small = Enumerable.Range(0, 64).Select(i => i * 1e-6f / 63).ToArray();
        Assert.Equal(LegacyApply(small, exactFloor), IntensityWindow.Apply(small, exactFloor));
    }

    [Fact]
    public void DegenerateWindow_RendersBlack() =>
        Assert.Equal(new byte[] { 0, 0, 0 },
            IntensityWindow.Apply(new[] { 4f, 5f, 6f }, new IntensityWindow.Bounds(5, 5)));
}
