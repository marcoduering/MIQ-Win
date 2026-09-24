namespace MIQ.Rendering;

/// Percentile windowing for volumetric intensity data: maps a finite-valued
/// float buffer to 8-bit grayscale. Direct port of MIQCore's IntensityWindow.
public static class IntensityWindow
{
    public readonly struct Bounds(float low, float high)
    {
        public float Low { get; } = low;
        public float High { get; } = high;
    }

    /// Derives window bounds from a pooled value set. Returns null if no finite
    /// values are present.
    public static Bounds? GetBounds(IReadOnlyList<float> values, double lowerPercentile, double upperPercentile)
    {
        var finite = new List<float>(values.Count);
        foreach (var v in values)
            if (MiqCompat.IsFinite(v)) finite.Add(v);
        if (finite.Count == 0) return null;

        // Prefer a non-zero subset if substantial; the /20 ratio guards against
        // rejecting legitimate dim regions when most voxels are background.
        var nonZero = new List<float>(finite.Count);
        foreach (var v in finite)
            if (Math.Abs(v) > 1e-6f) nonZero.Add(v);
        var source = nonZero.Count >= Math.Max(64, finite.Count / 20) ? nonZero : finite;
        source.Sort();

        var lower = Percentile(source, (float)lowerPercentile / 100f);
        var upper = Percentile(source, (float)upperPercentile / 100f);
        var minV = Min(finite, lower);
        var maxV = Max(finite, upper);
        var windowLow = lower < upper ? lower : minV;
        var windowHigh = lower < upper ? upper : maxV;
        return new Bounds(windowLow, windowHigh);
    }

    // Divisor for Apply. No absolute floor on the width: float data can span far
    // less than 1e-6 (ADC maps in SI units sit around 1e-9), and the old
    // 1e-6 floor squashed such a window into one or two grey levels. A degenerate
    // window (High <= Low) clips every value to Low, so the numerator is 0 and any
    // positive divisor gives black; 1 just keeps the division finite. Output is
    // unchanged for every window >= 1e-6 (port of MIQ@d6dabd5).
    private static float Range(Bounds bounds)
    {
        var width = bounds.High - bounds.Low;
        return width > 0 ? width : 1f;
    }

    /// Applies precomputed bounds, producing 8-bit grayscale. Specialised overload
    /// for float[] to avoid per-element interface dispatch on the hot path.
    public static byte[] Apply(float[] values, Bounds bounds)
    {
        var range = Range(bounds);
        var outp = new byte[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            var value = values[i];
            if (!MiqCompat.IsFinite(value)) { outp[i] = 0; continue; }
            var clipped = Math.Max(bounds.Low, Math.Min(bounds.High, value));
            var unit = Math.Max(0f, Math.Min(1f, (clipped - bounds.Low) / range));
            outp[i] = (byte)MiqCompat.RoundToInt(unit * 255f);
        }
        return outp;
    }

    /// Applies precomputed bounds, producing 8-bit grayscale.
    public static byte[] Apply(IReadOnlyList<float> values, Bounds bounds)
    {
        var range = Range(bounds);
        var outp = new byte[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            var value = values[i];
            if (!MiqCompat.IsFinite(value)) { outp[i] = 0; continue; }
            var clipped = Math.Max(bounds.Low, Math.Min(bounds.High, value));
            var unit = Math.Max(0f, Math.Min(1f, (clipped - bounds.Low) / range));
            outp[i] = (byte)MiqCompat.RoundToInt(unit * 255f);
        }
        return outp;
    }

    private static float Min(List<float> v, float fallback)
    {
        var m = float.PositiveInfinity;
        foreach (var x in v) if (x < m) m = x;
        return MiqCompat.IsFinite(m) ? m : fallback;
    }

    private static float Max(List<float> v, float fallback)
    {
        var m = float.NegativeInfinity;
        foreach (var x in v) if (x > m) m = x;
        return MiqCompat.IsFinite(m) ? m : fallback;
    }

    private static float Percentile(List<float> sorted, float p)
    {
        if (sorted.Count == 0) return 0f;
        if (sorted.Count == 1) return sorted[0];

        var clamped = Math.Max(0f, Math.Min(1f, p));
        var position = clamped * (sorted.Count - 1);
        var lowerIndex = (int)Math.Floor((double)position);
        var upperIndex = (int)Math.Ceiling((double)position);
        if (lowerIndex == upperIndex) return sorted[lowerIndex];

        var fraction = position - lowerIndex;
        return sorted[lowerIndex] * (1 - fraction) + sorted[upperIndex] * fraction;
    }
}
