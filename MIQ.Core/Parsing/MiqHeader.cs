namespace MIQ.Parsing;

/// <summary>
/// Format-agnostic image header. Mirrors MIQCore's <c>MIQHeader</c>.
///
/// <see cref="OrientationFrame"/> is resolved at parse time; it drives edge
/// labels, the reoriented view modes and the metadata panel's orientation.
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

    /// Overrides the datatype name shown in the metadata panel when a parser widens
    /// the stored type (MIF `Bit` → uint8). Null = use Datatype.Label().
    public string? DatatypeLabel { get; init; }

    /// Authoritative anatomical mapping, or null if undeterminable.
    public OrientationFrame? OrientationFrame { get; init; }

    /// NIfTI only: the qform's orientation label when both sform and qform are
    /// valid and imply different axes beyond an oblique tie-break (the sform
    /// drives <see cref="OrientationFrame"/>). Null otherwise.
    public string? ConflictingQformOrientation { get; init; }

    /// Free-text description from the header (NIfTI `descrip`, MIF `comments:`,
    /// NRRD `content:`, MetaImage `Comment`), cleaned by
    /// <see cref="MiqParser.HeaderComment(IEnumerable{string})"/>; lines are separated
    /// by "\n". Null when the format has none or it is empty. Can name a patient
    /// (MRtrix copies DICOM patient fields into `comments:`).
    public string? Comment { get; init; }

    /// How MIQ's x, y, z, t axes map to the file's own axes, for the voxel-index
    /// readout. Set by MIF and NRRD; null for the formats whose axes are MIQ's
    /// (NIfTI, MGH, MetaImage).
    public FileIndexAxes? FileIndexAxes { get; init; }

    public int Width => Dimensions.Count > 0 ? Dimensions[0] : 1;
    public int Height => Dimensions.Count > 1 ? Dimensions[1] : 1;
    public int Depth => Dimensions.Count > 2 ? Dimensions[2] : 1;
    public int Volumes => Math.Max(1, Dimensions.Count > 3 ? Dimensions[3] : 1);

    /// The voxel at MIQ's (x, y, z, t) as the file indexes it, counted from 0, in
    /// the file's axis order: one value per file axis for MIF and NRRD (a size-1
    /// axis included), otherwise three for a 3-D image and four for 4-D. This is
    /// the raw array index (nibabel, fsleyes, pynrrd), independent of the view
    /// orientation; tools that realign on load (mrview for NIfTI/MGH) or fold a
    /// list axis into the pixel (ITK) can count differently.
    public int[] FileVoxelIndex(int x, int y, int z, int t)
    {
        int[] cursor = [x, y, z, t];
        if (FileIndexAxes is not { } axes)
            return cursor.Take(Volumes > 1 ? 4 : 3).ToArray();

        var index = new int[axes.Rank];
        for (var axis = 0; axis < 4; axis++)
        {
            // In a 3-D file MIQ's t has no file axis to land on.
            var fileAxis = axes.FileAxis[axis];
            if (fileAxis >= axes.Rank) continue;
            var dim = axis < Dimensions.Count ? Dimensions[axis] : 1;
            index[fileAxis] = axes.Reversed[axis] ? dim - 1 - cursor[axis] : cursor[axis];
        }
        return index;
    }
}

/// MIQ's x, y, z, t axes in the file's terms. MIF stores its spatial axes in
/// layout order and MIQ reads reversed ones forwards; NRRD may put its volume
/// axis first. Both formats address voxels along the file's own axes.
public sealed class FileIndexAxes(int[] fileAxis, bool[] reversed, int rank)
{
    /// The file axis each of MIQ's x, y, z, t is (t's is ignored when it is ≥ Rank).
    public IReadOnlyList<int> FileAxis { get; } = fileAxis;
    /// Whether MIQ walks that axis against the file's index direction.
    public IReadOnlyList<bool> Reversed { get; } = reversed;
    /// The file's number of axes, size-1 axes included.
    public int Rank { get; } = rank;
}
