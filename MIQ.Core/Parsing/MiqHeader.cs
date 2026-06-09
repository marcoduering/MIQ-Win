namespace MIQ.Parsing;

/// <summary>
/// Format-agnostic image header. Mirrors MIQCore's <c>MIQHeader</c>.
///
/// Voxels are always rendered "as stored" (reorientation is out of scope), but
/// <see cref="OrientationFrame"/> is resolved at parse time so slices can carry
/// correct anatomical edge labels and the metadata panel can show the
/// storage orientation. Null when the affine is undeterminable.
/// </summary>
public sealed class MiqHeader
{
    public required bool LittleEndian { get; init; }
    public required IReadOnlyList<int> Dimensions { get; init; }
    public required IReadOnlyList<float> Pixdim { get; init; }
    public required MiqDatatype Datatype { get; init; }
    public required int VoxOffset { get; init; }
    public required float SclSlope { get; init; }
    public required float SclInter { get; init; }
    public required int QformCode { get; init; }
    public required int SformCode { get; init; }
    public required IReadOnlyList<float> SrowX { get; init; }
    public required IReadOnlyList<float> SrowY { get; init; }
    public required IReadOnlyList<float> SrowZ { get; init; }
    public float QuaternB { get; init; }
    public float QuaternC { get; init; }
    public float QuaternD { get; init; }
    public float Qfac { get; init; }

    /// Overrides the display format name; set by parsers that detect compression.
    public string? FormatLabel { get; init; }

    /// Authoritative anatomical mapping, or null if undeterminable.
    public OrientationFrame? OrientationFrame { get; init; }

    public int Width => Dimensions.Count > 0 ? Dimensions[0] : 1;
    public int Height => Dimensions.Count > 1 ? Dimensions[1] : 1;
    public int Depth => Dimensions.Count > 2 ? Dimensions[2] : 1;
    public int Volumes => Math.Max(1, Dimensions.Count > 3 ? Dimensions[3] : 1);
}
