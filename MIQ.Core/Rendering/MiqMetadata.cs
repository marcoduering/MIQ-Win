using System.Globalization;
using MIQ.Parsing;

namespace MIQ.Rendering;

public sealed class MetadataEntry(string label, string value)
{
    public string Label { get; } = label;
    public string Value { get; } = value;
}

/// Header → display-ready label/value pairs. Port of MIQCore's MIQMetadata
/// (asDisplayLines), with a leading Format line as the QL view does.
public sealed class MiqMetadata
{
    private readonly MiqHeader _h;
    private readonly string? _format;
    private readonly string? _orientation;

    public MiqMetadata(MiqHeader header, string? formatName, string? orientation)
    {
        _h = header;
        _format = formatName;
        _orientation = orientation;
    }

    public IReadOnlyList<MetadataEntry> AsDisplayLines()
    {
        var inv = CultureInfo.InvariantCulture;
        var entries = new List<MetadataEntry>();

        if (_format is { } f)
            entries.Add(new("Format", f));

        entries.Add(new("Dimensions", $"{_h.Width} x {_h.Height} x {_h.Depth}"));

        var x = Pixdim(1);
        var y = Pixdim(2);
        var z = Pixdim(3);
        entries.Add(new("Spacing", string.Format(inv, "{0:0.00} x {1:0.00} x {2:0.00} mm", x, y, z)));

        if (_orientation is { } o)
            entries.Add(new("Orientation", o));

        entries.Add(new("Datatype", _h.Datatype.Label()));
        entries.Add(new("Volumes", _h.Volumes.ToString(inv)));

        if (Scaling() is { } s)
            entries.Add(new("Scaling", s));

        return entries;
    }

    private float Pixdim(int i) => i < _h.Pixdim.Count ? _h.Pixdim[i] : 1f;

    private string? Scaling()
    {
        double slope = _h.SclSlope;
        double intercept = _h.SclInter;
        const double eps = 1e-6;

        if (Math.Abs(slope) <= eps) return null;
        if (Math.Abs(slope - 1) <= eps && Math.Abs(intercept) <= eps) return null;

        var sign = intercept < 0 ? "-" : "+";
        return string.Format(CultureInfo.InvariantCulture,
            "x {0:0.000} {1} {2:0.000}", slope, sign, Math.Abs(intercept));
    }
}
