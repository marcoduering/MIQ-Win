namespace MIQ.Rendering;

/// <summary>
/// Maps integer segmentation labels to display RGB. Two colour schemes:
/// a deterministic hash-based <em>random</em> palette (categorical, distinct
/// per label, stable across every plane/slice/timepoint without a pre-scan), and
/// a curated <em>FreeSurfer</em> palette — the canonical colours for the common
/// <c>aseg</c> + <c>aparc</c> (Desikan-Killiany) structures, with any label not
/// in the table falling back to the random palette. Label 0 is background (black,
/// the default canvas). There is no macOS counterpart; this is Windows-only.
/// </summary>
public sealed class SegmentationLut
{
    /// Upper bound on distinct labels (in the sampled center slices) for a volume
    /// to be treated as a segmentation; above this the data is assumed to be
    /// intensity. Chosen to clear standard FreeSurfer with margin while rejecting
    /// dense 8-bit intensity images: a whole aparc+aseg has only ~110 distinct
    /// labels (aseg ~45), and a center-slice sample can never exceed the volume's
    /// total, so 160 leaves headroom; a typical uint8 anatomical spans most of
    /// 0..255 (~200+ distinct) and is rejected. Non-const + internal so tests can
    /// adjust it. (Rich parcellations beyond ~160 distinct, e.g. Destrieux or
    /// subfield segs, fall back to grayscale — out of scope for the common subset.)
    internal static int MaxLabels = 160;

    private readonly bool _useFreeSurfer;
    private readonly bool _monochromeWhite;

    /// <param name="useFreeSurfer">Use the canonical FreeSurfer palette (else random).</param>
    /// <param name="monochromeWhite">Render every non-zero label white — used for a
    /// binary mask (a single non-zero label), where a coloured palette adds nothing.</param>
    public SegmentationLut(bool useFreeSurfer, bool monochromeWhite = false)
    {
        _useFreeSurfer = useFreeSurfer;
        _monochromeWhite = monochromeWhite;
    }

    /// Writes the 3 RGB bytes for <paramref name="label"/> into
    /// <paramref name="dst"/> at <paramref name="offset"/>.
    public void Write(int label, byte[] dst, int offset)
    {
        if (label == 0) { dst[offset] = 0; dst[offset + 1] = 0; dst[offset + 2] = 0; return; }

        var c = _monochromeWhite ? ((byte)255, (byte)255, (byte)255)
            : _useFreeSurfer && FreeSurfer.TryGetValue(label, out var fs) ? fs
            : RandomColor(label);
        dst[offset] = c.Item1;
        dst[offset + 1] = c.Item2;
        dst[offset + 2] = c.Item3;
    }

    public static bool IsFreeSurferLabel(int label) => FreeSurfer.ContainsKey(label);

    // A FreeSurfer label that is BOTH distinctive (a naive sequential labelling
    // never reaches it — left-hemisphere aseg only goes 2..31) AND always present
    // in a whole-brain segmentation, so it is reliable proof of FreeSurfer:
    //   41..54  right-hemisphere core structures (white matter / cortex / ventricle
    //           / cerebellum / thalamus / caudate / putamen / pallidum / hippocampus
    //           / amygdala) — anchored by 41 & 42, which are always segmented;
    //   251..255 corpus callosum (always present);
    //   1000+   cortical parcellation (always present in aparc).
    // Optional labels (e.g. 77/80 hypointensities, 85 optic-chiasm, 58 accumbens)
    // are deliberately excluded — they may be absent, so they can't be relied on.
    private static bool IsFreeSurferSignature(int label) =>
        (label >= 41 && label <= 54)
        || (label >= 251 && label <= 255)
        || label >= 1000;

    /// True when the sampled labels look like a FreeSurfer parcellation: at least
    /// a few non-background labels, a majority of which are in the canonical table,
    /// AND at least one is a FreeSurfer signature structure (see
    /// <see cref="IsFreeSurferSignature"/>). The signature guard is what stops a
    /// generic small-integer scheme — e.g. a 1=CSF / 2=GM / 3=WM tissue
    /// segmentation, whose 2 and 3 coincide with FreeSurfer's white-matter and
    /// cortex labels — from being mistaken for FreeSurfer and borrowing its
    /// colours. Such files fall through to the random palette instead.
    public static bool LooksLikeFreeSurfer(ICollection<int> labels)
    {
        var nonZero = 0;
        var known = 0;
        var hasSignature = false;
        foreach (var l in labels)
        {
            if (l == 0) continue;
            nonZero++;
            if (!IsFreeSurferLabel(l)) continue;
            known++;
            if (IsFreeSurferSignature(l)) hasSignature = true;
        }
        return nonZero >= 3 && known * 2 >= nonZero && hasSignature;
    }

    // Deterministic per-label colour: hash the label to a hue (and small
    // saturation/value jitter) so adjacent labels separate visually, the same
    // label is identical in every plane/slice, and no pre-scan of the volume is
    // needed. Knuth multiplicative hash spreads sequential ids well.
    private static (byte r, byte g, byte b) RandomColor(int label)
    {
        unchecked
        {
            var h = (uint)label * 2654435761u;
            var hue = ((h >> 8) & 0xFFFF) / 65535f;        // 0..1
            // Saturation floored well above 0 so colours stay chromatic and never
            // approach white (which is reserved for binary masks).
            var sat = 0.65f + (h & 0xFF) / 255f * 0.30f;   // 0.65..0.95
            var val = 0.75f + ((h >> 24) & 0x3F) / 63f * 0.20f; // 0.75..0.95
            return HsvToRgb(hue, sat, val);
        }
    }

    private static (byte r, byte g, byte b) HsvToRgb(float h, float s, float v)
    {
        var i = (int)Math.Floor(h * 6f) % 6;
        if (i < 0) i += 6;
        var f = h * 6f - (float)Math.Floor(h * 6f);
        var p = v * (1f - s);
        var q = v * (1f - f * s);
        var t = v * (1f - (1f - f) * s);
        var (r, g, b) = i switch
        {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q),
        };
        return (Byte(r), Byte(g), Byte(b));
    }

    private static byte Byte(float unit) => (byte)MiqCompat.Clamp(MiqCompat.RoundToInt(unit * 255f), 0, 255);

    // --- Canonical FreeSurfer colours (subset: aseg + Desikan aparc) ----------
    // Values from FreeSurferColorLUT.txt. Right-hemisphere cortical labels
    // (2000+) share their left-hemisphere colour, so the cortical palette is
    // stored once and applied to both 1000+ and 2000+ in the static ctor.

    // Desikan-Killiany cortical colours, indexed by (label % 1000), 0..35.
    // Declared before FreeSurfer (below) because its initializer reads this —
    // static fields initialize in textual order, so the order matters.
    private static readonly (byte r, byte g, byte b)[] Cortical =
    {
        (25, 5, 25),     // 0  unknown
        (25, 100, 40),   // 1  bankssts
        (125, 100, 160), // 2  caudalanteriorcingulate
        (100, 25, 0),    // 3  caudalmiddlefrontal
        (120, 70, 50),   // 4  corpuscallosum
        (220, 20, 100),  // 5  cuneus
        (220, 20, 10),   // 6  entorhinal
        (180, 220, 140), // 7  fusiform
        (220, 60, 220),  // 8  inferiorparietal
        (180, 40, 120),  // 9  inferiortemporal
        (140, 20, 140),  // 10 isthmuscingulate
        (20, 30, 140),   // 11 lateraloccipital
        (35, 75, 50),    // 12 lateralorbitofrontal
        (225, 140, 140), // 13 lingual
        (200, 35, 75),   // 14 medialorbitofrontal
        (160, 100, 50),  // 15 middletemporal
        (20, 220, 60),   // 16 parahippocampal
        (60, 220, 60),   // 17 paracentral
        (220, 180, 140), // 18 parsopercularis
        (20, 100, 50),   // 19 parsorbitalis
        (220, 60, 20),   // 20 parstriangularis
        (120, 100, 60),  // 21 pericalcarine
        (220, 20, 20),   // 22 postcentral
        (220, 180, 220), // 23 posteriorcingulate
        (60, 20, 220),   // 24 precentral
        (160, 140, 180), // 25 precuneus
        (80, 20, 140),   // 26 rostralanteriorcingulate
        (75, 50, 125),   // 27 rostralmiddlefrontal
        (20, 220, 160),  // 28 superiorfrontal
        (20, 180, 140),  // 29 superiorparietal
        (140, 220, 220), // 30 superiortemporal
        (80, 160, 20),   // 31 supramarginal
        (100, 0, 100),   // 32 frontalpole
        (70, 70, 70),    // 33 temporalpole
        (150, 150, 200), // 34 transversetemporal
        (255, 192, 32),  // 35 insula
    };

    private static readonly Dictionary<int, (byte r, byte g, byte b)> FreeSurfer = BuildFreeSurfer();

    private static Dictionary<int, (byte r, byte g, byte b)> BuildFreeSurfer()
    {
        var d = new Dictionary<int, (byte r, byte g, byte b)>
        {
            // aseg subcortical / structural labels
            [2] = (245, 245, 245),   // Left-Cerebral-White-Matter
            [3] = (205, 62, 78),     // Left-Cerebral-Cortex
            [4] = (120, 18, 134),    // Left-Lateral-Ventricle
            [5] = (196, 58, 250),    // Left-Inf-Lat-Vent
            [7] = (220, 248, 164),   // Left-Cerebellum-White-Matter
            [8] = (230, 148, 34),    // Left-Cerebellum-Cortex
            [10] = (0, 118, 14),     // Left-Thalamus
            [11] = (122, 186, 220),  // Left-Caudate
            [12] = (236, 13, 176),   // Left-Putamen
            [13] = (12, 48, 255),    // Left-Pallidum
            [14] = (204, 182, 142),  // 3rd-Ventricle
            [15] = (42, 204, 164),   // 4th-Ventricle
            [16] = (119, 159, 176),  // Brain-Stem
            [17] = (220, 216, 20),   // Left-Hippocampus
            [18] = (103, 255, 255),  // Left-Amygdala
            [24] = (60, 60, 60),     // CSF
            [26] = (255, 165, 0),    // Left-Accumbens-area
            [28] = (165, 42, 42),    // Left-VentralDC
            [30] = (160, 32, 240),   // Left-vessel
            [31] = (0, 200, 200),    // Left-choroid-plexus
            [41] = (245, 245, 245),  // Right-Cerebral-White-Matter
            [42] = (205, 62, 78),    // Right-Cerebral-Cortex
            [43] = (120, 18, 134),   // Right-Lateral-Ventricle
            [44] = (196, 58, 250),   // Right-Inf-Lat-Vent
            [46] = (220, 248, 164),  // Right-Cerebellum-White-Matter
            [47] = (230, 148, 34),   // Right-Cerebellum-Cortex
            [49] = (0, 118, 14),     // Right-Thalamus
            [50] = (122, 186, 220),  // Right-Caudate
            [51] = (236, 13, 176),   // Right-Putamen
            [52] = (13, 48, 255),    // Right-Pallidum
            [53] = (220, 216, 20),   // Right-Hippocampus
            [54] = (103, 255, 255),  // Right-Amygdala
            [58] = (255, 165, 0),    // Right-Accumbens-area
            [60] = (165, 42, 42),    // Right-VentralDC
            [62] = (160, 32, 240),   // Right-vessel
            [63] = (0, 200, 221),    // Right-choroid-plexus
            [72] = (120, 190, 150),  // 5th-Ventricle
            [77] = (200, 70, 255),   // WM-hypointensities
            [80] = (164, 108, 226),  // non-WM-hypointensities
            [85] = (234, 169, 30),   // Optic-Chiasm
            [251] = (0, 0, 64),      // CC_Posterior
            [252] = (0, 0, 112),     // CC_Mid_Posterior
            [253] = (0, 0, 160),     // CC_Central
            [254] = (0, 0, 208),     // CC_Mid_Anterior
            [255] = (0, 0, 255),     // CC_Anterior
        };

        // Desikan aparc cortical labels: lh = 1000+i, rh = 2000+i, same colour.
        for (var i = 0; i < Cortical.Length; i++)
        {
            d[1000 + i] = Cortical[i];
            d[2000 + i] = Cortical[i];
        }
        return d;
    }
}
