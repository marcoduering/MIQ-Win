using MIQ.Parsing;
using MIQ.Rendering;
using Xunit;

namespace MIQ.Tests;

// Synthetic coverage for the piecewise-constancy gate (MiqVolume.IsPiecewiseConstant)
// and the raised MaxLabels cap. No real FreeSurfer corpus is available in this
// environment, so these fixtures build minimal in-memory MiqImage/MiqHeader
// instances directly rather than going through a file parser. Block-based label
// fixtures (not per-voxel cycling) so the label set stays piecewise-constant, as
// called for by MIQ's synthetic-fixture guidance for this detector.
public class SegmentationDetectionTests
{
    static MiqVolume MakeVolume(int w, int h, int d, Func<int, int, int, int> valueAt)
    {
        var storage = new byte[w * h * d * 2]; // Int16, little-endian
        for (var z = 0; z < d; z++)
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var idx = x + w * (y + h * z);
                    var v = (short)valueAt(x, y, z);
                    storage[idx * 2] = (byte)(v & 0xFF);
                    storage[idx * 2 + 1] = (byte)((v >> 8) & 0xFF);
                }

        var header = new MiqHeader
        {
            LittleEndian = true,
            Dimensions = new[] { w, h, d },
            Pixdim = new[] { 1f, 1f, 1f, 1f },
            Datatype = MiqDatatype.Int16,
            VoxOffset = 0,
            SclSlope = 0f,
            SclInter = 0f,
            QformCode = 0,
            SformCode = 0,
            SrowX = new float[] { 1, 0, 0 },
            SrowY = new float[] { 0, 1, 0 },
            SrowZ = new float[] { 0, 0, 1 },
        };
        var image = new MiqImage { Header = header, Storage = storage, PayloadOffset = 0 };
        return new MiqVolume(image);
    }

    static readonly MiqRenderingOptions Auto = new(Segmentation: MiqSegmentationColoring.Auto);

    // Contiguous 4^3 blocks each holding one label — piecewise-constant, the
    // shape a real segmentation has. Must still be detected as a label volume.
    [Fact]
    public void BlockSegmentation_IsDetectedAsLabelVolume()
    {
        const int w = 48, block = 4, blocksPerAxis = 48 / block, numLabels = 50;
        int LabelAt(int x, int y, int z)
        {
            var bx = x / block; var by = y / block; var bz = z / block;
            return 1 + (bx + by * blocksPerAxis + bz * blocksPerAxis * blocksPerAxis) % numLabels;
        }
        var vol = MakeVolume(w, w, w, LabelAt);
        var lut = vol.BuildSegmentationLut(Auto);
        Assert.NotNull(lut);
    }

    // Every voxel a different small integer value with no spatial constancy —
    // the false-positive shape from the bug report (a normalised anatomical
    // re-quantized to a small integer range). Must NOT be coloured, even though
    // the distinct-value count (20) is far under both the old and new caps.
    [Fact]
    public void NoisyLowCardinalityIntensity_IsNotDetectedAsLabelVolume()
    {
        const int w = 48;
        int ValueAt(int x, int y, int z) => 1 + (x * 7 + y * 13 + z * 29) % 20; // never 0
        var vol = MakeVolume(w, w, w, ValueAt);
        var lut = vol.BuildSegmentationLut(Auto);
        Assert.Null(lut);
    }

    // Same noisy pattern, but half the volume is true background (0) — proves
    // the background-dominant case doesn't dilute the ratio enough to sneak
    // a noisy foreground half past the gate.
    [Fact]
    public void NoisyIntensityWithBackground_IsNotDetectedAsLabelVolume()
    {
        const int w = 48;
        int ValueAt(int x, int y, int z) => x < w / 2 ? 0 : 1 + (x * 7 + y * 13 + z * 29) % 20;
        var vol = MakeVolume(w, w, w, ValueAt);
        var lut = vol.BuildSegmentationLut(Auto);
        Assert.Null(lut);
    }

    // Isolated single-voxel labels, never horizontally adjacent to another
    // foreground voxel, so the pooled sample never reaches the 256-pair floor.
    // Must abstain (accept), not reject for lack of evidence.
    [Fact]
    public void SparseForeground_TooFewPairs_Abstains()
    {
        const int w = 48, h = 48, d = 48;
        int ValueAt(int x, int y, int z)
        {
            if (y == h / 2 && x == 2 && z == 2) return 5;
            if (y == h / 2 && x == 40 && z == 40) return 9;
            return 0;
        }
        var vol = MakeVolume(w, h, d, ValueAt);
        var lut = vol.BuildSegmentationLut(Auto);
        Assert.NotNull(lut);
    }

    // A block mask (single foreground label, real background) must still
    // resolve to the monochrome-white LUT via the full-volume confirm scan —
    // the piecewise-constancy gate must not intercept it (ratio is exactly 0
    // for a single-label foreground, so it always passes through).
    [Fact]
    public void BinaryMask_StillRendersMonochromeWhite()
    {
        const int w = 48;
        int ValueAt(int x, int y, int z) => x < w / 2 ? 0 : 7;
        var vol = MakeVolume(w, w, w, ValueAt);
        var lut = vol.BuildSegmentationLut(Auto);
        Assert.NotNull(lut);
        Assert.True(lut!.IsMonochromeWhite);
    }

    // A dense parcellation (>160 distinct sampled labels, well under the new
    // 4096 cap) must still be detected — proves the count cap was demoted from
    // a discriminator to a resource guard. Block size 8 keeps the pooled
    // boundary-crossing ratio in the same range as real segmentations (~0.1-0.15).
    [Fact]
    public void DenseParcellation_AboveOldCapBelowNewCap_IsDetectedAsLabelVolume()
    {
        const int w = 64, block = 8, blocksPerAxis = 64 / block, numLabels = 300;
        int LabelAt(int x, int y, int z)
        {
            var bx = x / block; var by = y / block; var bz = z / block;
            return 1 + (bx + by * blocksPerAxis + bz * blocksPerAxis * blocksPerAxis) % numLabels;
        }
        var vol = MakeVolume(w, w, w, LabelAt);
        var lut = vol.BuildSegmentationLut(Auto);
        Assert.NotNull(lut);
    }

    [Fact]
    public void Destrieux_LabelsAreRecognizedAndUseCuratedColor()
    {
        Assert.True(SegmentationLut.IsFreeSurferLabel(11101)); // ctx_lh_G_and_S_frontomargin
        Assert.True(SegmentationLut.IsFreeSurferLabel(12175)); // ctx_rh_S_temporal_transverse
        Assert.True(SegmentationLut.LooksLikeFreeSurfer(new HashSet<int> { 11101, 11106, 11128 }));

        var lut = new SegmentationLut(useFreeSurfer: true);
        var buf = new byte[3];
        lut.Write(11101, buf, 0);
        Assert.Equal((23, 220, 60), (buf[0], buf[1], buf[2]));
    }

    [Fact]
    public void WmParc_LabelsUseInvertedDesikanColor()
    {
        Assert.True(SegmentationLut.IsFreeSurferLabel(3001)); // wm-lh-bankssts
        Assert.True(SegmentationLut.IsFreeSurferLabel(4035)); // wm-rh-insula

        var lut = new SegmentationLut(useFreeSurfer: true);
        var buf = new byte[3];
        lut.Write(3001, buf, 0); // Cortical bankssts (25,100,40) inverted
        Assert.Equal((230, 155, 215), (buf[0], buf[1], buf[2]));
    }

    // Port of MIQ's segmentationWmparcGyralWhiteMatterColorIsCanonical: wm-lh/rh
    // -unknown (3000/4000, index 0 of the inversion loop) and the two leftover
    // "not assigned to a gyral parcel" labels (5001/5002) must be recognized too
    // — these were the specific entries missing from the first port pass.
    [Fact]
    public void WmParc_UnknownAndUnsegmentedLabelsAreRecognized()
    {
        Assert.True(SegmentationLut.IsFreeSurferLabel(3000)); // wm-lh-unknown
        Assert.True(SegmentationLut.IsFreeSurferLabel(4000)); // wm-rh-unknown
        Assert.True(SegmentationLut.IsFreeSurferLabel(5001)); // Left-UnsegmentedWhiteMatter
        Assert.True(SegmentationLut.IsFreeSurferLabel(5002)); // Right-UnsegmentedWhiteMatter

        var lut = new SegmentationLut(useFreeSurfer: true);
        var buf = new byte[3];
        lut.Write(3000, buf, 0); // Cortical unknown (25,5,25) inverted
        Assert.Equal((230, 250, 230), (buf[0], buf[1], buf[2]));
        lut.Write(5001, buf, 0);
        Assert.Equal((20, 30, 40), (buf[0], buf[1], buf[2]));
        lut.Write(5002, buf, 0);
        Assert.Equal((20, 30, 40), (buf[0], buf[1], buf[2]));
    }

    // Port of MIQ's segmentationFreeSurferTableIsHemisphereSymmetric: every
    // parcel family is one index-parallel colour array applied to both
    // hemispheres, so lh and rh must always agree.
    [Fact]
    public void FreeSurferTable_IsHemisphereSymmetric()
    {
        var lut = new SegmentationLut(useFreeSurfer: true);
        (byte r, byte g, byte b) Color(int label)
        {
            var buf = new byte[3];
            lut.Write(label, buf, 0);
            return (buf[0], buf[1], buf[2]);
        }

        foreach (var (lhBase, rhBase, count) in new[] { (1000, 2000, 36), (11100, 12100, 76), (3000, 4000, 36) })
            for (var i = 0; i < count; i++)
            {
                Assert.True(SegmentationLut.IsFreeSurferLabel(lhBase + i));
                Assert.True(SegmentationLut.IsFreeSurferLabel(rhBase + i));
                Assert.Equal(Color(lhBase + i), Color(rhBase + i));
            }
    }

    // Port of MIQ's segmentationAtlasAboveLegacyLabelCapIsDetected: pins that
    // MaxLabels is the only thing standing between the old (rejecting) and new
    // (accepting) behaviour for a dense-but-piecewise-constant atlas — i.e. that
    // the cap, not the piecewise gate, is what changed for this shape of file.
    [Fact]
    public void DenseParcellation_RejectedUnderOldCap_AcceptedUnderNewCap()
    {
        const int w = 64, block = 8, blocksPerAxis = 64 / block, numLabels = 300;
        int LabelAt(int x, int y, int z)
        {
            var bx = x / block; var by = y / block; var bz = z / block;
            return 1 + (bx + by * blocksPerAxis + bz * blocksPerAxis * blocksPerAxis) % numLabels;
        }
        var vol = MakeVolume(w, w, w, LabelAt);

        var original = SegmentationLut.MaxLabels;
        try
        {
            SegmentationLut.MaxLabels = 160;
            Assert.Null(vol.BuildSegmentationLut(Auto));

            SegmentationLut.MaxLabels = 4096;
            Assert.NotNull(vol.BuildSegmentationLut(Auto));
        }
        finally
        {
            SegmentationLut.MaxLabels = original;
        }
    }
}
