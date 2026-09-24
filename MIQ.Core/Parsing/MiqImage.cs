namespace MIQ.Parsing;

/// <summary>
/// A parsed file: header plus the (decompressed) byte buffer and the offset
/// where voxel data begins. Mirrors MIQCore's <c>MIQImage</c>.
/// </summary>
public sealed class MiqImage
{
    public required MiqHeader Header { get; init; }
    public required byte[] Storage { get; init; }
    public required int PayloadOffset { get; init; }
    /// True when Storage contains only volume 0's data (vol-0-first NIfTI load).
    /// Volumes > 0 are safe to access but return 0 for all voxels.
    public bool IsPartial { get; init; }

    /// True when the full data exceeds the byte[] limit, so expansion is
    /// impossible. Implies <see cref="IsPartial"/>; the scrubber is suppressed.
    public bool ExpansionBlocked { get; init; }

    public int PayloadCount => Math.Max(0, Storage.Length - PayloadOffset);

    /// Single byte relative to the payload start.
    public byte Byte(int payloadOffset) => Storage[PayloadOffset + payloadOffset];

    /// Element strides per axis [x, y, z, t]. Null → standard row-major.
    /// Set by MIF (from its layout) and by NRRD with a non-default axis order.
    public int[]? ElementStrides { get; init; }

    /// Element index of voxel [0,0,0,0]. Currently 0 for every format (strides
    /// are never negative).
    public int BaseElementIndex { get; init; }

    /// Element index for voxel (x, y, z, t). Uses custom strides when present,
    /// otherwise standard row-major (x fastest, then y, z, t).
    public int VoxelElementIndex(int x, int y, int z, int t)
    {
        if (ElementStrides is { } s)
            return BaseElementIndex
                + x * s[0] + y * s[1] + z * s[2]
                + (s.Length > 3 ? t * s[3] : 0);
        var h = Header;
        return x + h.Width * (y + h.Height * (z + h.Depth * t));
    }
}
