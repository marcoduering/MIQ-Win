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

    /// Magnitude at or below which a value counts as background for the preferred
    /// foreground subset (see <see cref="GetBounds"/>).
    internal const float NonZeroFloor = 1e-6f;

    /// Derives window bounds from a pooled value set. Returns null if no finite
    /// values are present.
    public static Bounds? GetBounds(IReadOnlyList<float> values, double lowerPercentile, double upperPercentile)
    {
        var finite = new List<float>(values.Count);
        foreach (var v in values)
            if (MiqCompat.IsFinite(v)) finite.Add(v);
        if (finite.Count == 0) return null;

        // Window over the foreground when it's substantial; the /20 ratio keeps a
        // dim region from being rejected when most voxels are background.
        //
        // Two tiers, tried in order. The NonZeroFloor also drops near-zero
        // interpolation residue, and wins whenever it leaves enough voxels (every
        // ordinary image, unchanged from before). Only when it doesn't (data in
        // tiny units: an SI-unit ADC map sits around 1e-9, entirely below the
        // floor) do exactly-non-zero values stand in, so such a map windows over
        // its tissue instead of over tissue plus background zeros. Deliberately
        // not a floor scaled by the data's maximum: one outlier voxel (1e30 from a
        // failed fit) would lift that above real tissue. Port of MIQCore's bounds.
        var minimumSubset = Math.Max(64, finite.Count / 20);
        var aboveFloor = new List<float>(finite.Count);
        var nonZeroCount = 0;
        foreach (var v in finite)
        {
            if (v == 0f) continue;
            nonZeroCount++;
            if (Math.Abs(v) > NonZeroFloor) aboveFloor.Add(v);
        }

        List<float> source;
        if (aboveFloor.Count >= minimumSubset)
            source = aboveFloor;
        else if (nonZeroCount >= minimumSubset)
        {
            // Tiny-unit fallback: the only case that pays a second pass.
            source = new List<float>(nonZeroCount);
            foreach (var v in finite)
                if (v != 0f) source.Add(v);
        }
        else
            source = finite;
        source.Sort();

        var lower = Percentile(source, (float)lowerPercentile / 100f);
        var upper = Percentile(source, (float)upperPercentile / 100f);
        var minV = Min(finite, lower);
        var maxV = Max(finite, upper);
        var windowLow = lower < upper ? lower : minV;
        var windowHigh = lower < upper ? upper : maxV;
        return new Bounds(windowLow, windowHigh);
    }

    // Divisor for Apply. No absolute floor: float data can span far less than
    // 1e-6 (SI-unit ADC maps ~1e-9). A degenerate window (High <= Low) gives a
    // zero numerator, so 1 just keeps the division finite.
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
