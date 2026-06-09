namespace MIQ.Parsing;

/// Anatomical world axis (RAS basis). Port of MIQCore's AnatomicalAxis.
public enum AnatomicalAxis { RightLeft, AnteriorPosterior, SuperiorInferior }

/// Per-storage-axis anatomical role. <see cref="Positive"/> true means +storage
/// matches +R/+A/+S. Port of MIQCore's StorageAxisOrientation.
public readonly struct StorageAxisOrientation(AnatomicalAxis axis, bool positive)
{
    public AnatomicalAxis Axis { get; } = axis;
    public bool Positive { get; } = positive;

    public string Letter => (Axis, Positive) switch
    {
        (AnatomicalAxis.RightLeft, true) => "R",
        (AnatomicalAxis.RightLeft, false) => "L",
        (AnatomicalAxis.AnteriorPosterior, true) => "A",
        (AnatomicalAxis.AnteriorPosterior, false) => "P",
        (AnatomicalAxis.SuperiorInferior, true) => "S",
        _ => "I",
    };

    public StorageAxisOrientation Opposite => new(Axis, !Positive);
}

public enum SlicePlane { Coronal, Sagittal, Axial }

/// Edge labels for one slice. Port of MIQCore's SliceOrientationLabels.
public sealed class SliceOrientationLabels(
    string leading, string trailing, string top, string bottom, bool isUnknown = false)
{
    public string Leading { get; } = leading;
    public string Trailing { get; } = trailing;
    public string Top { get; } = top;
    public string Bottom { get; } = bottom;
    public bool IsUnknown { get; } = isUnknown;

    public static readonly SliceOrientationLabels Unknown = new("?", "?", "?", "?", isUnknown: true);
}

/// Authoritative per-storage-axis anatomical mapping. Port of MIQCore's
/// OrientationFrame. Built at parse time from sform (preferred) or qform.
public sealed class OrientationFrame
{
    public IReadOnlyList<StorageAxisOrientation> Axes { get; }

    private OrientationFrame(IReadOnlyList<StorageAxisOrientation> axes) => Axes = axes;

    /// 3-letter storage orientation, e.g. "RAS".
    public string Label => string.Concat(Axes.Select(a => a.Letter));

    public SliceOrientationLabels DisplayLabels(SlicePlane plane)
    {
        var (h, v) = plane switch
        {
            SlicePlane.Coronal => (0, 2),
            SlicePlane.Sagittal => (1, 2),
            _ => (0, 1), // axial
        };
        var ha = Axes[h];
        var va = Axes[v];
        // Stored plan: hReversed=false, vReversed=true. Trailing column is the
        // +storage end of h; top row is the +storage end of v.
        return new SliceOrientationLabels(
            leading: ha.Opposite.Letter,
            trailing: ha.Letter,
            top: va.Letter,
            bottom: va.Opposite.Letter);
    }

    /// Build from three sform-style row vectors (one per world axis x,y,z).
    public static OrientationFrame? From(
        IReadOnlyList<float> srowX, IReadOnlyList<float> srowY, IReadOnlyList<float> srowZ)
    {
        var axes = new StorageAxisOrientation[3];
        for (var col = 0; col < 3; col++)
        {
            var x = col < srowX.Count ? srowX[col] : 0f;
            var y = col < srowY.Count ? srowY[col] : 0f;
            var z = col < srowZ.Count ? srowZ[col] : 0f;
            if (x * x + y * y + z * z == 0f) return null;
            axes[col] = Anatomy(x, y, z);
        }
        if (axes.Select(a => a.Axis).Distinct().Count() != 3) return null;
        return new OrientationFrame(axes);
    }

    /// Build from a NIfTI qform quaternion. qfac is sign of pixdim[0].
    public static OrientationFrame? FromQuaternion(float b, float c, float d, float qfac)
    {
        var aSquared = Math.Max(0f, 1 - b * b - c * c - d * d);
        var a = (float)Math.Sqrt(aSquared);
        var qs = qfac < 0 ? -1f : 1f;

        var r00 = a * a + b * b - c * c - d * d;
        var r01 = 2 * (b * c - a * d);
        var r02 = 2 * (b * d + a * c) * qs;
        var r10 = 2 * (b * c + a * d);
        var r11 = a * a + c * c - b * b - d * d;
        var r12 = 2 * (c * d - a * b) * qs;
        var r20 = 2 * (b * d - a * c);
        var r21 = 2 * (c * d + a * b);
        var r22 = (a * a + d * d - b * b - c * c) * qs;

        return From(new[] { r00, r01, r02 }, new[] { r10, r11, r12 }, new[] { r20, r21, r22 });
    }

    private static StorageAxisOrientation Anatomy(float x, float y, float z)
    {
        float ax = Math.Abs(x), ay = Math.Abs(y), az = Math.Abs(z);
        if (ax >= ay && ax >= az) return new(AnatomicalAxis.RightLeft, x >= 0);
        if (ay >= ax && ay >= az) return new(AnatomicalAxis.AnteriorPosterior, y >= 0);
        return new(AnatomicalAxis.SuperiorInferior, z >= 0);
    }
}
