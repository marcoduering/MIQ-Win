using System.Text;
using MIQ.Parsing;
using Xunit;

namespace MIQ.Tests;

// MIF anatomical orientation: `transform:` composed with `layout:`.
//
// Pins that anatomy is NOT derived from the axis index (right for mrconvert
// output, wrong for `mrtransform -replace`), and that neither the transform nor
// the layout alone is the orientation: the layout's sign flips an axis, its rank
// permutes the three.
//
// Ground truth is the NIfTI export (`mrconvert x.mif x.nii.gz`, axcode read off
// the affine), which bakes the layout into both letters and axis order. The
// labels below are those axcodes, compared verbatim.
public class MifOrientationTests
{
    // ── Fixtures ────────────────────────────────────────────────────────────

    /// Minimal uint8 MIF. <paramref name="transform"/> null omits the optional
    /// field entirely; rows are written verbatim so malformed ones can be tested.
    static byte[] MifFile(string layout, string[]? transform = null, int[]? dim = null)
    {
        dim ??= new[] { 2, 3, 4 };
        var header = new StringBuilder();
        header.Append("mrtrix image\n");
        header.Append("dim: ").Append(string.Join(",", dim)).Append('\n');
        header.Append("vox: ").Append(string.Join(",", dim.Select(_ => "1"))).Append('\n');
        header.Append("layout: ").Append(layout).Append('\n');
        header.Append("datatype: UInt8\n");
        foreach (var row in transform ?? Array.Empty<string>())
            header.Append("transform: ").Append(row).Append('\n');
        header.Append("file: .\n");
        header.Append("END\n");

        var bytes = Encoding.ASCII.GetBytes(header.ToString());
        var voxels = dim.Aggregate(1, (a, b) => a * b);
        var file = new byte[bytes.Length + voxels];
        bytes.CopyTo(file, 0);
        return file;
    }

    /// 60° rotation about z — the `mrtransform -replace` case. Columns:
    /// 0 → (0.5, 0.866, 0) dominant +y = A; 1 → (-0.866, 0.5, 0) dominant −x = L;
    /// 2 → (0, 0, 1) = S.
    static readonly string[] Rot60Z =
    {
        "0.5,-0.866,0,0",
        "0.866,0.5,0,0",
        "0,0,1,0",
    };

    static readonly string[] Identity = { "1,0,0,0", "0,1,0,0", "0,0,1,0" };

    /// Canonical LAS: image axis 0 points left, the other two unchanged.
    static readonly string[] LasTransform = { "-1,0,0,0", "0,1,0,0", "0,0,1,0" };

    static string Label(byte[] file) => MifParser.Parse(file).Header.OrientationFrame!.Label;

    // ── The defect ──────────────────────────────────────────────────────────

    // The header of a real `mrtransform in.mif out.mif -replace rot60.txt` file
    // (211×240×256, `mrtrix_version: 3.0.4`). Its NIfTI export reads RIA.
    // Deriving anatomy from the axis index instead gives PIR — all three letters
    // wrong, left/right among them.
    [Fact]
    public void ReplacedTransform_MatchesNiftiExport() =>
        Assert.Equal("RIA", Label(MifFile("+2,-0,-1", Rot60Z, new[] { 211, 240, 256 })));

    // ── Composition: the transform alone is not the orientation ─────────────

    // MRtrix realigns on import and writes back a canonical transform, so
    // `transform:` is RAS for essentially every mrconvert-written file and the
    // real orientation sits in the layout. Both spellings of LAS must agree.
    [Fact]
    public void CanonicalTransformStoredReversed_IsLas() =>
        Assert.Equal("LAS", Label(MifFile("-0,+1,+2", Identity)));

    [Fact]
    public void LasTransformStoredForward_IsLas() =>
        Assert.Equal("LAS", Label(MifFile("+0,+1,+2", LasTransform)));

    // An LAS transform stored reversed composes back to canonical RAS.
    [Fact]
    public void LasTransformStoredReversed_IsRas() =>
        Assert.Equal("RAS", Label(MifFile("-0,+1,+2", LasTransform)));

    // The field is optional: absent means an identity affine.
    [Fact]
    public void AbsentTransform_FallsBackToIdentity() =>
        Assert.Equal("LAS", Label(MifFile("-0,+1,+2")));

    // A real mrconvert file (the layout and identity transform of the perf
    // corpus's 3_wmfod.mif, written with `-stride 0,0,0,1`). Its volume axis is the fastest-varying one, which the spatial permutation
    // must ignore — the three spatial axes are already in rank order.
    [Fact]
    public void MrconvertWrittenFile_IsUnchanged() =>
        Assert.Equal("LAS", Label(MifFile("-1,+2,+3,+0", Identity, new[] { 4, 4, 4, 2 })));

    // Rotation too small to change any dominant axis must not change any letter —
    // the accept-side half of the degenerate-transform rejections below.
    [Fact]
    public void ObliqueButUnambiguousTransform_IsRas() =>
        Assert.Equal("RAS", Label(MifFile(
            "+0,+1,+2", new[] { "0.866,-0.5,0,0", "0.5,0.866,0,0", "0,0,1,0" })));

    // ── The layout's rank permutes the volume, not just the label ───────────

    // Storage order, not `dim:` order, is what reaches the renderer: the label,
    // the dimensions and the strides all permute together, so axis i of the frame
    // is axis i of the volume being walked. A label permuted on its own would
    // mislabel the axes and misplace every reoriented slice.
    [Fact]
    public void LayoutRank_PermutesLabelDimensionsAndStridesTogether()
    {
        // Ranks 1,0,2 → memory order is axis 1, axis 0, axis 2.
        var image = MifParser.Parse(MifFile("+1,+0,+2", Identity, new[] { 2, 3, 4 }));

        Assert.Equal("ARS", image.Header.OrientationFrame!.Label);
        Assert.Equal(new[] { 3, 2, 4, 1 }, image.Header.Dimensions);
        // Fastest axis first: 1, then ×3, then ×3·2.
        Assert.Equal(new[] { 1, 3, 6 }, image.ElementStrides);
    }

    // The permutation must not break the mapping from voxel to element: every
    // voxel of the presented volume still lands on a distinct payload element,
    // and the whole payload is covered.
    [Fact]
    public void PermutedVolume_StillAddressesEveryElementExactlyOnce()
    {
        var image = MifParser.Parse(MifFile("+2,-0,-1", Rot60Z, new[] { 2, 3, 4 }));
        var (w, h, d) = (image.Header.Width, image.Header.Height, image.Header.Depth);
        Assert.Equal(new[] { 3, 4, 2 }, new[] { w, h, d });

        var seen = new HashSet<int>();
        for (var z = 0; z < d; z++)
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                    Assert.True(seen.Add(image.VoxelElementIndex(x, y, z, 0)));

        Assert.Equal(Enumerable.Range(0, w * h * d), seen.OrderBy(i => i));
    }

    // ── Degenerate transforms yield "unknown", never an invented frame ──────

    static void AssertUnknown(byte[] file) =>
        Assert.Null(MifParser.Parse(file).Header.OrientationFrame);

    [Fact]
    public void ZeroColumn_IsUnknown() =>
        AssertUnknown(MifFile("+0,+1,+2", new[] { "0,0,0,0", "0,1,0,0", "0,0,1,0" }));

    [Fact]
    public void TwoColumnsOnTheSameWorldAxis_IsUnknown() =>
        AssertUnknown(MifFile("+0,+1,+2", new[] { "1,1,0,0", "0,0,0,0", "0,0,1,0" }));

    [Fact]
    public void NonFiniteTransform_IsUnknown() =>
        AssertUnknown(MifFile("+0,+1,+2", new[] { "nan,0,0,0", "0,1,0,0", "0,0,1,0" }));

    [Fact]
    public void WrongRowCount_IsUnknown() =>
        AssertUnknown(MifFile("+0,+1,+2", new[] { "1,0,0,0", "0,1,0,0" }));

    [Fact]
    public void ShortRow_IsUnknown() =>
        AssertUnknown(MifFile("+0,+1,+2", new[] { "1,0", "0,1,0,0", "0,0,1,0" }));

    [Fact]
    public void UnparseableRow_IsUnknown() =>
        AssertUnknown(MifFile("+0,+1,+2", new[] { "1,x,0,0", "0,1,0,0", "0,0,1,0" }));

    // A malformed optional field costs the orientation, not the preview: the
    // voxels are still there and still displayable.
    [Fact]
    public void MalformedTransform_StillParsesTheVoxels()
    {
        var image = MifParser.Parse(MifFile("+0,+1,+2", new[] { "1,x,0,0" }));
        Assert.Equal(2, image.Header.Width);
        Assert.Equal(3, image.Header.Height);
        Assert.Equal(4, image.Header.Depth);
    }
}
