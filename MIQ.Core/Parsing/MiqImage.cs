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
    /// True when Storage contains only volume 0's data (multi-volume .nii.gz
    /// quick-load). Volumes > 0 are safe to access but return 0 for all voxels.
    public bool IsPartial { get; init; }

    /// True when only volume 0 is loaded AND the full data exceeds the in-memory
    /// byte[] limit, so background expansion to the complete volume is impossible
    /// (a 4-D series too large to materialise). Implies <see cref="IsPartial"/>;
    /// the viewer keeps showing volume 0 and suppresses the scrubber.
    public bool ExpansionBlocked { get; init; }

    public int PayloadCount => Math.Max(0, Storage.Length - PayloadOffset);

    /// Single byte relative to the payload start.
    public byte Byte(int payloadOffset) => Storage[PayloadOffset + payloadOffset];

    /// Signed element strides per axis [x, y, z, t]. Null → standard row-major.
    /// MIF uses custom strides derived from its layout field; other formats leave this null.
    public int[]? ElementStrides { get; init; }

    /// Element index of voxel [0,0,0,0] when any stride is negative.
    /// Always 0 for standard row-major formats.
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
