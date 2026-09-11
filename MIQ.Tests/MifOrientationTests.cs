using System.Text;
using MIQ.Parsing;
using Xunit;

namespace MIQ.Tests;

// MIF anatomical orientation: `transform:` composed with `layout:`.
//
// The defect these pin: deriving the letters from the axis INDEX (axis 0 = R/L,
// 1 = A/P, 2 = S/I, negated when the layout entry is negative) without ever
// reading `transform:`. That shortcut is right for anything mrconvert writes —
// it normalises the transform on write and parks the real orientation in the
// layout — and silently wrong, left/right included, for a file from
// `mrtransform -replace`, which does not normalise.
//
// Ground truth is the NIfTI export: `mrconvert x.mif x.nii.gz`, then the axcode
// nibabel/fsleyes/FSL read off the affine. NIfTI has no stride indirection, so
// MRtrix has to bake the orientation into the affine. MRtrix preserves strides
// through the conversion and NIfTI stores axes fastest-first, so the exported
// array's axes are this volume's axes in MEMORY order — while MIQ's frame is
// keyed by IMAGE axis (`dim:` order), the index its dimensions and strides use.
// The two labels are therefore the same anatomy under the layout's permutation,
// which is what ExportAxcode applies before comparing.
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

    /// 60° rotation about z — the `mrtransform -replace` fixture. Columns:
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

    /// The orientation code the NIfTI export of this file reports: MIQ's
    /// image-axis-keyed label permuted into memory order (axes sorted by
    /// abs(layout), fastest first).
    static string ExportAxcode(byte[] file, string layout)
    {
        var order = layout.Split(',')
            .Take(3)
            .Select(t => int.Parse(t.TrimStart('+', '-')))
            .ToArray();
        var frame = MifParser.Parse(file).Header.OrientationFrame!;
        return string.Concat(Enumerable.Range(0, 3)
            .OrderBy(axis => order[axis])
            .Select(axis => frame.Axes[axis].Letter));
    }

    // ── The defect ──────────────────────────────────────────────────────────

    // `mrtransform in.mif out.mif -replace rot60.txt`. The NIfTI export of this
    // volume reads RIA. Deriving anatomy from the axis index instead gives PIR —
    // all three letters wrong, left/right among them.
    [Fact]
    public void ReplacedTransform_MatchesNiftiExport()
    {
        const string layout = "+2,-0,-1";
        var file = MifFile(layout, Rot60Z);

        Assert.Equal("RIA", ExportAxcode(file, layout));
        // Same anatomy keyed by image axis: 0 → A (not reversed), 1 → L reversed
        // to R, 2 → S reversed to I.
        Assert.Equal("ARI", Label(file));
    }

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

    // The field is optional: absent means an identity affine, which composed with
    // the layout reproduces the pre-fix behaviour. A fallback, not a code path.
    [Fact]
    public void AbsentTransform_FallsBackToIdentity() =>
        Assert.Equal("LAS", Label(MifFile("-0,+1,+2")));

    // A real mrconvert file (the layout and identity-ish transform of the perf
    // corpus's 3_wmfod.mif, written with `-stride 0,0,0,1`): unchanged by the fix.
    [Fact]
    public void MrconvertWrittenFile_IsUnchanged() =>
        Assert.Equal("LAS", Label(MifFile("-1,+2,+3,+0", Identity, new[] { 4, 4, 4, 2 })));

    // Rotation too small to change any dominant axis must not change any letter —
    // the accept-side half of the degenerate-transform rejections below.
    [Fact]
    public void ObliqueButUnambiguousTransform_IsRas() =>
        Assert.Equal("RAS", Label(MifFile(
            "+0,+1,+2", new[] { "0.866,-0.5,0,0", "0.5,0.866,0,0", "0,0,1,0" })));

    // ── The layout's axis ORDER is a memory permutation, not anatomy ────────

    // It is consumed by the stride computation alone. The frame stays keyed by
    // image axis, so it still labels the axes the renderer walks; the export
    // axcode is that label permuted.
    [Fact]
    public void LayoutOrder_PermutesTheExportNotTheFrame()
    {
        const string layout = "+1,+0,+2";
        var file = MifFile(layout, Identity);

        Assert.Equal("RAS", Label(file));
        Assert.Equal("ARS", ExportAxcode(file, layout));
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
