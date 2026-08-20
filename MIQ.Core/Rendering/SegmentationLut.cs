namespace MIQ.Rendering;

/// <summary>
/// Maps integer segmentation labels to display RGB. Two colour schemes:
/// a rank-based <em>random</em> palette (labels sorted, hues evenly spaced at
/// 360/n° apart with a coprime stride so value-adjacent labels are hue-distant;
/// unknown labels fall back to the per-label hash), and a curated
/// <em>FreeSurfer</em> palette — the canonical colours for <c>aseg</c>, Desikan-
/// Killiany <c>aparc</c>, Destrieux <c>aparc.a2009s</c>, and wmparc's gyral WM
/// parcels, with any label not in the table falling back to the per-label hash.
/// Label 0 is background (black, the default canvas). There is no macOS
/// counterpart; this is Windows-only.
/// </summary>
public sealed class SegmentationLut
{
    /// Upper bound on distinct labels (in the sampled center slices) for a volume
    /// to be treated as a segmentation. NOT a discriminator between label maps and
    /// intensity images — real label counts and intensity value counts overlap
    /// completely (greyscale spans 42-127 distinct in testing; genuine label maps
    /// span 11-136; a Destrieux parcellation at 131 has MORE distinct values than
    /// a normalised T1 at 127), so no count threshold separates them in either
    /// direction. That job belongs to piecewise-constancy (see
    /// <see cref="MiqVolume.IsPiecewiseConstant"/>). This cap exists only as a
    /// resource guard against pathological inputs and as a test seam (non-const +
    /// internal so tests can lower it). Set well above dense atlases (Schaefer-1000,
    /// HCP-MMP 360) so legitimate rich parcellations are never silently rejected.
    internal static int MaxLabels = 4096;

    private readonly bool _useFreeSurfer;
    private readonly bool _monochromeWhite;
    // Null for FreeSurfer / monochromeWhite instances; populated for Random instances.
    private readonly Dictionary<int, (byte r, byte g, byte b)>? _rankedPalette;

    /// <param name="useFreeSurfer">Use the canonical FreeSurfer palette (else random).</param>
    /// <param name="monochromeWhite">Render every non-zero label white — used for a
    /// binary mask (a single non-zero label), where a coloured palette adds nothing.</param>
    public SegmentationLut(bool useFreeSurfer, bool monochromeWhite = false)
    {
        _useFreeSurfer = useFreeSurfer;
        _monochromeWhite = monochromeWhite;
    }

    private SegmentationLut(Dictionary<int, (byte r, byte g, byte b)> rankedPalette)
    {
        _useFreeSurfer = false;
        _monochromeWhite = false;
        _rankedPalette = rankedPalette;
    }

    /// Build a rank-based random palette: labels are sorted, hues spread evenly
    /// at 360/n° intervals, with a coprime stride so value-adjacent labels map
    /// to hue-distant slots. Colors are a pure function of the label set —
    /// deterministic per file, but a label's color depends on its rank among
    /// present labels, not its raw value.
    public static SegmentationLut Random(ISet<int> labels)
        => new SegmentationLut(BuildRankedPalette(labels));

    public bool IsFreeSurfer => _useFreeSurfer;
    public bool IsMonochromeWhite => _monochromeWhite;

    /// Writes the 3 RGB bytes for <paramref name="label"/> into
    /// <paramref name="dst"/> at <paramref name="offset"/>.
    public void Write(int label, byte[] dst, int offset)
    {
        if (label == 0) { dst[offset] = 0; dst[offset + 1] = 0; dst[offset + 2] = 0; return; }

        var c = _monochromeWhite ? ((byte)255, (byte)255, (byte)255)
            : _useFreeSurfer && FreeSurfer.TryGetValue(label, out var fs) ? fs
            : _rankedPalette is { } rp && rp.TryGetValue(label, out var rc) ? rc
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

    // Rank-based categorical palette: present labels sorted, hues spread evenly
    // at 360/n° intervals. A coprime stride (near the golden ratio of n) maps
    // sorted index → hue slot so value-adjacent labels are hue-distant while
    // remaining a bijection (no collisions). Two-tier lightness alternation adds
    // contrast once n is large enough that hue alone crowds. Pure function of
    // the label set — deterministic per file, unknown labels fall back to RandomColor.
    private static Dictionary<int, (byte r, byte g, byte b)> BuildRankedPalette(ISet<int> labels)
    {
        var sorted = labels.Where(l => l != 0).OrderBy(l => l).ToArray();
        int n = sorted.Length;
        var palette = new Dictionary<int, (byte r, byte g, byte b)>(n);
        if (n == 0) return palette;
        int stride = CoprimeStride(n);
        for (int i = 0; i < n; i++)
        {
            int slot = (int)(((long)i * stride) % n);
            float hue = (float)slot / n;
            float val = (i & 1) == 0 ? 0.97f : 0.78f;
            palette[sorted[i]] = HsvToRgb(hue, 0.85f, val);
        }
        return palette;
    }

    private static int CoprimeStride(int n)
    {
        if (n <= 2) return 1;
        int s = Math.Max(1, MiqCompat.RoundToInt(n * 0.6180339887f));
        while (s > 1 && Gcd(s, n) != 1) s--;
        return s;
    }

    private static int Gcd(int a, int b) { while (b != 0) (a, b) = (b, a % b); return a; }

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

    // Destrieux (aparc.a2009s) cortical colours, indexed by (label % 100), 0..75.
    // Values from FreeSurferColorLUT.txt (ctx_lh_*/ctx_rh_* 11100-11175 /
    // 12100-12175; lh and rh share a colour, same convention as Cortical above).
    private static readonly (byte r, byte g, byte b)[] Destrieux =
    {
        (0, 0, 0),       // 00 Unknown
        (23, 220, 60),   // 01 G_and_S_frontomargin
        (23, 60, 180),   // 02 G_and_S_occipital_inf
        (63, 100, 60),   // 03 G_and_S_paracentral
        (63, 20, 220),   // 04 G_and_S_subcentral
        (13, 0, 250),    // 05 G_and_S_transv_frontopol
        (26, 60, 0),     // 06 G_and_S_cingul-Ant
        (26, 60, 75),    // 07 G_and_S_cingul-Mid-Ant
        (26, 60, 150),   // 08 G_and_S_cingul-Mid-Post
        (25, 60, 250),   // 09 G_cingul-Post-dorsal
        (60, 25, 25),    // 10 G_cingul-Post-ventral
        (180, 20, 20),   // 11 G_cuneus
        (220, 20, 100),  // 12 G_front_inf-Opercular
        (140, 60, 60),   // 13 G_front_inf-Orbital
        (180, 220, 140), // 14 G_front_inf-Triangul
        (140, 100, 180), // 15 G_front_middle
        (180, 20, 140),  // 16 G_front_sup
        (23, 10, 10),    // 17 G_Ins_lg_and_S_cent_ins
        (225, 140, 140), // 18 G_insular_short
        (180, 60, 180),  // 19 G_occipital_middle
        (20, 220, 60),   // 20 G_occipital_sup
        (60, 20, 140),   // 21 G_oc-temp_lat-fusifor
        (220, 180, 140), // 22 G_oc-temp_med-Lingual
        (65, 100, 20),   // 23 G_oc-temp_med-Parahip
        (220, 60, 20),   // 24 G_orbital
        (20, 60, 220),   // 25 G_pariet_inf-Angular
        (100, 100, 60),  // 26 G_pariet_inf-Supramar
        (220, 180, 220), // 27 G_parietal_sup
        (20, 180, 140),  // 28 G_postcentral
        (60, 140, 180),  // 29 G_precentral
        (25, 20, 140),   // 30 G_precuneus
        (20, 60, 100),   // 31 G_rectus
        (60, 220, 20),   // 32 G_subcallosal
        (60, 60, 220),   // 33 G_temp_sup-G_T_transv
        (220, 60, 220),  // 34 G_temp_sup-Lateral
        (65, 220, 60),   // 35 G_temp_sup-Plan_polar
        (25, 140, 20),   // 36 G_temp_sup-Plan_tempo
        (220, 220, 100), // 37 G_temporal_inf
        (180, 60, 60),   // 38 G_temporal_middle
        (61, 20, 220),   // 39 Lat_Fis-ant-Horizont
        (61, 20, 60),    // 40 Lat_Fis-ant-Vertical
        (61, 60, 100),   // 41 Lat_Fis-post
        (25, 25, 25),    // 42 Medial_wall
        (140, 20, 60),   // 43 Pole_occipital
        (220, 180, 20),  // 44 Pole_temporal
        (63, 180, 180),  // 45 S_calcarine
        (221, 20, 10),   // 46 S_central
        (221, 20, 100),  // 47 S_cingul-Marginalis
        (221, 60, 140),  // 48 S_circular_insula_ant
        (221, 20, 220),  // 49 S_circular_insula_inf
        (61, 220, 220),  // 50 S_circular_insula_sup
        (100, 200, 200), // 51 S_collat_transv_ant
        (10, 200, 200),  // 52 S_collat_transv_post
        (221, 220, 20),  // 53 S_front_inf
        (141, 20, 100),  // 54 S_front_middle
        (61, 220, 100),  // 55 S_front_sup
        (141, 60, 20),   // 56 S_interm_prim-Jensen
        (143, 20, 220),  // 57 S_intrapariet_and_P_trans
        (101, 60, 220),  // 58 S_oc_middle_and_Lunatus
        (21, 20, 140),   // 59 S_oc_sup_and_transversal
        (61, 20, 180),   // 60 S_occipital_ant
        (221, 140, 20),  // 61 S_oc-temp_lat
        (141, 100, 220), // 62 S_oc-temp_med_and_Lingual
        (221, 100, 20),  // 63 S_orbital_lateral
        (181, 200, 20),  // 64 S_orbital_med-olfact
        (101, 20, 20),   // 65 S_orbital-H_Shaped
        (101, 100, 180), // 66 S_parieto_occipital
        (181, 220, 20),  // 67 S_pericallosal
        (21, 140, 200),  // 68 S_postcentral
        (21, 20, 240),   // 69 S_precentral-inf-part
        (21, 20, 200),   // 70 S_precentral-sup-part
        (21, 20, 60),    // 71 S_suborbital
        (101, 60, 60),   // 72 S_subparietal
        (21, 180, 180),  // 73 S_temporal_inf
        (223, 220, 60),  // 74 S_temporal_sup
        (221, 60, 60),   // 75 S_temporal_transverse
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

        // Destrieux (aparc.a2009s) cortical labels: lh = 11100+i, rh = 12100+i.
        for (var i = 0; i < Destrieux.Length; i++)
        {
            d[11100 + i] = Destrieux[i];
            d[12100 + i] = Destrieux[i];
        }

        // wmparc gyral WM labels: lh = 3000+i, rh = 4000+i, indexed the same as
        // Cortical (including index 0, wm-lh/rh-unknown). FreeSurferColorLUT.txt
        // defines these as the exact (255-r, 255-g, 255-b) inverse of the matching
        // Desikan cortical colour — verified against the published table for
        // every one of the 36 structures, index 0 included — so they're derived
        // here rather than duplicated as a second hand-copied 70-entry table.
        for (var i = 0; i < Cortical.Length; i++)
        {
            var (r, g, b) = Cortical[i];
            var inv = ((byte)(255 - r), (byte)(255 - g), (byte)(255 - b));
            d[3000 + i] = inv;
            d[4000 + i] = inv;
        }

        // wmparc leftovers: white matter not assigned to a gyral parcel.
        d[5001] = (20, 30, 40); // Left-UnsegmentedWhiteMatter
        d[5002] = (20, 30, 40); // Right-UnsegmentedWhiteMatter

        return d;
    }
}
