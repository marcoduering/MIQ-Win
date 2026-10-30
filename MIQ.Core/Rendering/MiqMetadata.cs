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

    private readonly string? _comment;

    public MiqMetadata(MiqHeader header, string? formatName, string? orientation)
    {
        _h = header;
        _format = formatName;
        // A NIfTI whose qform disagrees with the sform it is shown by: name the
        // qform's label too, since qform-preferring tools show it in that orientation.
        _orientation = orientation is { } o && header.ConflictingQformOrientation is { } q
            ? $"{o} (qform: {q})"
            : orientation;
        _comment = header.Comment is { } c ? DisplayComment(c) : null;
    }

    /// At most this many comment lines are shown, plus a "…" line when cut. The
    /// panel applies the same limit again to *wrapped* lines, which only it can
    /// measure.
    public const int MaxCommentLines = 3;

    /// Caps the comment to <see cref="MaxCommentLines"/> lines, adding a "…" line
    /// after them when there are more (on its own line, since an ellipsis at the
    /// end of a line reads as part of the text).
    internal static string DisplayComment(string comment)
    {
        var lines = comment.Split(['\n'], StringSplitOptions.RemoveEmptyEntries);
        var shown = lines.Take(MaxCommentLines).ToList();
        if (lines.Length > MaxCommentLines) shown.Add("…");
        return string.Join("\n", shown);
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

        entries.Add(new("Orientation", _orientation ?? "?"));

        entries.Add(new("Datatype", _h.DatatypeLabel ?? _h.Datatype.Label()));
        entries.Add(new("Volumes", _h.Volumes.ToString(inv)));

        if (Scaling() is { } s)
            entries.Add(new("Scaling", s));

        if (_comment is { } c)
            entries.Add(new("Comment", c));

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
        // Six significant digits, matching the macOS readout (and the live Value row).
        return string.Format(CultureInfo.InvariantCulture,
            "x {0:G6} {1} {2:G6}", slope, sign, Math.Abs(intercept));
    }
}
