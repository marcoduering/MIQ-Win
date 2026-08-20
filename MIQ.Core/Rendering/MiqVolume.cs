using System.Threading.Tasks;
using MIQ.Parsing;

namespace MIQ.Rendering;

/// View-orientation mode. <c>Stored</c> renders the volume's axes exactly as
/// stored (legacy behaviour). <c>Neurological</c>/<c>Radiological</c> reorient
/// and relabel each plane to a canonical anatomical view; they differ only by
/// the in-plane R/L flip on coronal & axial (sagittal is identical in both).
/// Port of MIQCore's ViewOrientation (persisted as "stored"/"ras"/"las").
public enum MiqOrientation { Stored, Neurological, Radiological }

/// Label-colouring mode for integer segmentation volumes. <c>Off</c> always
/// percentile-windows (legacy). <c>Auto</c> colours a detected label volume —
/// canonical FreeSurfer colours when the labels look like a FreeSurfer parcellation,
/// otherwise deterministic random colours. <c>Random</c> forces random colours and
/// never consults the FreeSurfer table. Detection (see
/// <see cref="MiqVolume.BuildSegmentationLut"/>) only ever fires for integer,
/// identity-scaled data with few distinct values, so intensity images are unaffected.
public enum MiqSegmentationColoring { Off, Auto, Random }

// Percentiles are computed over voxels pooled from all three center slices
// (see CenterSlices) so every plane shares one intensity window. 2/98 clips
// the histogram tails (noise, sparse hyper-intensities) harder than 1/99 for
// better mid-range grayscale contrast.
public readonly record struct MiqRenderingOptions(
    double LowerPercentile = 2.0,
    double UpperPercentile = 98.0,
    MiqOrientation Orientation = MiqOrientation.Stored,
    MiqSegmentationColoring Segmentation = MiqSegmentationColoring.Off);

/// Resolved slicing for one plane under the active orientation: which storage
/// axis is perpendicular (Slice) and which map to display horizontal (H) /
/// vertical (V), whether each display direction reverses storage order, and the
/// edge labels. Port of MIQCore's SliceAxisPlan.
public readonly record struct SlicePlan(
    int SliceAxis, int HAxis, int VAxis,
    bool HReversed, bool VReversed,
    SliceOrientationLabels Labels);

public sealed class CenterSlice(SliceImage image, SliceOrientationLabels labels)
{
    public SliceImage Image { get; } = image;
    public SliceOrientationLabels Labels { get; } = labels;
}

/// Wraps a parsed image and extracts the three center slices with a shared
/// intensity window. Grayscale datatypes are percentile-windowed; rgb24/rgba32
/// are rendered as opaque RGB (alpha dropped) and bypass windowing. When a
/// <see cref="SegmentationLut"/> is supplied (see <see cref="BuildSegmentationLut"/>),
/// grayscale label values are mapped to RGB through the LUT instead of windowed.
/// Port of MIQCore's MIQVolume.
public sealed class MiqVolume(MiqImage image, MiqOrientation orientation = MiqOrientation.Stored)
{
    private readonly MiqImage _image = image;
    private readonly MiqOrientation _orientation = orientation;
    private MiqHeader H => _image.Header;

    public int Width => H.Width;
    public int Height => H.Height;
    public int Depth => H.Depth;
    public int Volumes => H.Volumes;
    /// False while only volume 0 is in memory (partial .nii.gz quick-load).
    public bool IsExpanded => !_image.IsPartial;
    /// RGB datatypes (rgb24/rgba32) have no scalar voxel value; the live
    /// "Voxel value" metadata readout is suppressed for them.
    public bool IsRgb => H.Datatype is MiqDatatype.Rgb24 or MiqDatatype.Rgba32;

    /// Bounds-checked, scl-scaled scalar read of a single voxel — the value the
    /// crosshair "Voxel value" metadata row displays. Out-of-range coordinates or
    /// timepoints return 0 (matching the decode path). Not meaningful for RGB
    /// data; callers should gate on <see cref="IsRgb"/>. Port of MIQVolume.voxel.
    public float VoxelValue(int x, int y, int z, int t) => Voxel(x, y, z, t);

    // Stored-orientation plan per plane (from OrientationResolver.storedPlan):
    // (sliceAxis, hAxis, vAxis); hReversed always false, vReversed always true.
    private static (int slice, int h, int v) StoredPlan(SlicePlane plane) => plane switch
    {
        SlicePlane.Coronal => (1, 0, 2),
        SlicePlane.Sagittal => (0, 1, 2),
        _ => (2, 0, 1), // axial
    };

    /// Resolve the slicing plan for a plane under the active orientation. Port
    /// of MIQCore's OrientationResolver. Stored mode — or any file with no
    /// anatomical OrientationFrame — falls back to the legacy stored plan
    /// (hReversed=false, vReversed=true) with frame-derived (or unknown) labels.
    /// Reoriented modes pick storage axes by anatomy and use hardcoded canonical
    /// labels; see <see cref="ReorientedPlan"/>.
    public SlicePlan PlanFor(SlicePlane plane)
    {
        var frame = H.OrientationFrame;
        if (_orientation == MiqOrientation.Stored || frame is null)
        {
            var (s, h, v) = StoredPlan(plane);
            var labels = frame?.DisplayLabels(plane) ?? SliceOrientationLabels.Unknown;
            return new SlicePlan(s, h, v, HReversed: false, VReversed: true, labels);
        }
        return ReorientedPlan(plane, _orientation == MiqOrientation.Neurological, frame);
    }

    // Faithful port of OrientationResolver.reorientedPlan + anatomicalTarget.
    // Each plane's slice/H/V storage axes are chosen by anatomy; the edge labels
    // are FIXED per (plane, mode) by convention — deliberately NOT derived from
    // the frame's DisplayLabels (those describe the *stored* axes and would lie
    // here, e.g. a RAS volume's stored sagittal reads "P|A", the reverse of the
    // canonical "A|P"). The H/V reversal flags are then computed so the pixels
    // obey those fixed labels. Sagittal has no in-plane R/L, so it is identical
    // in both modes; coronal & axial differ only by the horizontal R/L flip.
    private static SlicePlan ReorientedPlan(SlicePlane plane, bool neuro, OrientationFrame frame)
    {
        int AxisFor(AnatomicalAxis a)
        {
            for (var i = 0; i < frame.Axes.Count; i++)
                if (frame.Axes[i].Axis == a) return i;
            return 0; // OrientationFrame.From guarantees all three are present
        }
        var rl = AxisFor(AnatomicalAxis.RightLeft);
        var ap = AxisFor(AnatomicalAxis.AnteriorPosterior);
        var si = AxisFor(AnatomicalAxis.SuperiorInferior);

        int sliceAxis, hAxis, vAxis;
        bool hTargetPositive, vTargetPositive;
        SliceOrientationLabels labels;
        switch (plane)
        {
            case SlicePlane.Sagittal:
                // Anterior on viewer's LEFT, Posterior right, Superior top.
                sliceAxis = rl; hAxis = ap; vAxis = si;
                hTargetPositive = false; vTargetPositive = true;
                labels = new SliceOrientationLabels("A", "P", "S", "I");
                break;
            case SlicePlane.Coronal:
                sliceAxis = ap; hAxis = rl; vAxis = si;
                hTargetPositive = neuro; vTargetPositive = true;
                labels = neuro
                    ? new SliceOrientationLabels("L", "R", "S", "I")
                    : new SliceOrientationLabels("R", "L", "S", "I");
                break;
            default: // Axial
                sliceAxis = si; hAxis = rl; vAxis = ap;
                hTargetPositive = neuro; vTargetPositive = true;
                labels = neuro
                    ? new SliceOrientationLabels("L", "R", "A", "P")
                    : new SliceOrientationLabels("R", "L", "A", "P");
                break;
        }

        // hReversed: storage h-direction opposes the target → flip columns.
        // vReversed: storage v-direction matches the target → flip rows (display
        // rows grow downward, so the +v end reaches the top only via reversal).
        var hReversed = frame.Axes[hAxis].Positive != hTargetPositive;
        var vReversed = frame.Axes[vAxis].Positive == vTargetPositive;
        return new SlicePlan(sliceAxis, hAxis, vAxis, hReversed, vReversed, labels);
    }

    public IReadOnlyDictionary<SlicePlane, CenterSlice> CenterSlices(
        MiqRenderingOptions options, int maxDimension = 512)
        => CenterInteractiveState(options, maxDimension).Slices;

    /// Single-decode path for the initial interactive preview. Prepares the three
    /// center slices ONCE, then derives from that one decode: the segmentation LUT
    /// (when the file is a detected label volume), the shared intensity window (only
    /// when there is no LUT), and the three finished CenterSlices. This replaces the
    /// BuildSegmentationLut + SharedWindow + ExtractSlice×3 sequence the plugin used,
    /// which re-decoded the same center slices 2–3× over; the output is identical
    /// (the per-call ExtractSlice always re-derived these exact center slices).
    public (SegmentationLut? Lut, IntensityWindow.Bounds? Window,
            IReadOnlyDictionary<SlicePlane, CenterSlice> Slices)
        CenterInteractiveState(MiqRenderingOptions options, int maxDimension = 512)
    {
        var planes = new[] { SlicePlane.Coronal, SlicePlane.Sagittal, SlicePlane.Axial };
        var prepared = new PreparedSlice[planes.Length];
        // Decode the three center planes in parallel. PrepareSlice is read-only over
        // the immutable Storage buffer and writes only its own freshly-allocated arrays
        // into a distinct slot, so there is no shared mutable state to guard. The pool
        // + finalize steps below run on the calling thread in fixed plane order, so the
        // window and output stay byte-identical regardless of decode completion order.
        Parallel.Invoke(
            () => prepared[0] = PrepareSlice(planes[0]),
            () => prepared[1] = PrepareSlice(planes[1]),
            () => prepared[2] = PrepareSlice(planes[2]));

        // Label detection reuses the prepared gray arrays — no extra decode.
        SegmentationLut? lut = null;
        if (SegmentationEligible(options) && CollectLabels(prepared) is { } labels
            && (IsPiecewiseConstant(prepared) ?? true))
            lut = FinishSegmentationLut(labels, options);

        // Label volumes map through the LUT, not the intensity window.
        IntensityWindow.Bounds? window = null;
        if (lut is null)
        {
            var pooled = new List<float>();
            foreach (var p in prepared)
                if (p.Gray is { } g) pooled.AddRange(g); // RGB slices bypass pooling
            window = IntensityWindow.GetBounds(pooled, options.LowerPercentile, options.UpperPercentile);
        }

        var result = new Dictionary<SlicePlane, CenterSlice>();
        for (var i = 0; i < planes.Length; i++)
            result[planes[i]] = new CenterSlice(
                Finalize(prepared[i], window, lut, maxDimension), prepared[i].Cfg.Labels);
        return (lut, window, result);
    }

    // Turn a prepared slice into a finished image: native RGB → opaque RgbImage
    // (no window); a label slice with a LUT → RGB via the LUT; otherwise grayscale
    // → windowed GrayscaleImage. All paths are FOV-resampled (nearest-neighbour,
    // so labels are never blended across boundaries).
    private static SliceImage Finalize(
        PreparedSlice p, IntensityWindow.Bounds? window, SegmentationLut? lut, int maxDimension)
    {
        var cfg = p.Cfg;
        if (p.Rgb is { } rgb)
            return SliceImage.FromRgb(new RgbImage(cfg.SliceWidth, cfg.SliceHeight, rgb)
                .ResampledForPixelSpacing(cfg.PixelSpacingX, cfg.PixelSpacingY, p.MaxExt, maxDimension));

        var values = p.Gray!;
        if (lut is { } colors)
        {
            var seg = new byte[values.Length * 3];
            for (var i = 0; i < values.Length; i++)
            {
                var v = values[i];
                var label = MiqCompat.IsFinite(v) ? MiqCompat.RoundToInt(v) : 0;
                colors.Write(label, seg, i * 3);
            }
            return SliceImage.FromRgb(new RgbImage(cfg.SliceWidth, cfg.SliceHeight, seg)
                .ResampledForPixelSpacing(cfg.PixelSpacingX, cfg.PixelSpacingY, p.MaxExt, maxDimension));
        }

        var pixels = window is { } b ? IntensityWindow.Apply(values, b) : new byte[values.Length];
        return SliceImage.Gray(new GrayscaleImage(cfg.SliceWidth, cfg.SliceHeight, pixels)
            .ResampledForPixelSpacing(cfg.PixelSpacingX, cfg.PixelSpacingY, p.MaxExt, maxDimension));
    }

    internal sealed class SliceConfig
    {
        public int SliceWidth, SliceHeight, OuterCount, InnerCount;
        public float PixelSpacingX, PixelSpacingY;
        public int SliceAxis, HAxis, VAxis, HDim, VDim;
        public bool HReversed, VReversed;
        public SliceOrientationLabels Labels = SliceOrientationLabels.Unknown;

        public (int x, int y, int z) Coordinate(int slice, int row, int col)
        {
            var h = HReversed ? HDim - 1 - col : col;
            var v = VReversed ? VDim - 1 - row : row;
            int x = SliceAxis == 0 ? slice : HAxis == 0 ? h : v;
            int y = SliceAxis == 1 ? slice : HAxis == 1 ? h : v;
            int z = SliceAxis == 2 ? slice : HAxis == 2 ? h : v;
            return (x, y, z);
        }
    }

    // A read-but-not-yet-finished slice. Exactly one of Gray (intensity floats,
    // to be windowed) / Rgb (interleaved RGB bytes, already display-ready) is set.
    // Internal (not private) so the interactive control can cache one between
    // renders — see PrepareInteractive / FinalizeInteractive.
    internal readonly struct PreparedSlice(float[]? gray, byte[]? rgb, SliceConfig cfg, float maxExt)
    {
        public float[]? Gray { get; } = gray;
        public byte[]? Rgb { get; } = rgb;
        public SliceConfig Cfg { get; } = cfg;
        public float MaxExt { get; } = maxExt;
    }

    // --- Interactive triplanar API (additive; CenterSlices path untouched) ---

    /// Stored-orientation axis roles for a plane: (perpendicular, horizontal,
    /// vertical) indices into the (Width, Height, Depth) voxel axes.
    public (int sliceAxis, int hAxis, int vAxis) AxesFor(SlicePlane plane)
    {
        var p = PlanFor(plane);
        return (p.SliceAxis, p.HAxis, p.VAxis);
    }

    /// Voxel count along a storage axis (0 = Width, 1 = Height, 2 = Depth).
    public int Dim(int axis) => axis switch { 0 => Width, 1 => Height, _ => Depth };

    /// Number of selectable slices for a plane (its perpendicular extent).
    public int SliceCount(SlicePlane plane) => Dim(PlanFor(plane).SliceAxis);

    /// Default (center) slice index for a plane.
    public int CenterIndex(SlicePlane plane) => Math.Max(0, SliceCount(plane) / 2);

    /// Shared intensity window from voxels pooled across the three center
    /// slices — compute once and reuse for every extracted slice so scrolling
    /// does not flicker the brightness. Pass a <paramref name="timepoint"/>
    /// other than 0 to compute the window for a specific 4-D volume.
    public IntensityWindow.Bounds? SharedWindow(MiqRenderingOptions options, int timepoint = 0)
    {
        var pooled = new List<float>();
        foreach (var plane in new[] { SlicePlane.Coronal, SlicePlane.Sagittal, SlicePlane.Axial })
            if (PrepareSlice(plane, timepoint: timepoint).Gray is { } g) pooled.AddRange(g);
        return IntensityWindow.GetBounds(pooled, options.LowerPercentile, options.UpperPercentile);
    }

    /// Extract a single slice at an arbitrary index using a precomputed window
    /// (intensity data) or a precomputed <paramref name="lut"/> (label data). The
    /// LUT is built once per volume so every plane, slice and timepoint share it.
    public CenterSlice ExtractSlice(
        SlicePlane plane, int sliceIndex, IntensityWindow.Bounds? window,
        SegmentationLut? lut = null, int maxDimension = 512, int timepoint = 0)
    {
        var p = PrepareSlice(plane, sliceIndex, timepoint);
        return new CenterSlice(Finalize(p, window, lut, maxDimension), p.Cfg.Labels);
    }

    // ExtractSlice split into its two halves so the interactive control can reuse a
    // decoded slice across a window/level change. The decode (PrepareInteractive) is
    // invariant under the intensity window — only the FINALIZE step (windowing +
    // resample) depends on it. So a right-drag that re-windows the same slice can skip
    // the decode entirely: cache the PreparedSlice keyed by (plane, index, timepoint),
    // then call FinalizeInteractive per window revision. PrepareInteractive + (window)
    // FinalizeInteractive is byte-for-byte ExtractSlice for the same arguments.
    internal PreparedSlice PrepareInteractive(SlicePlane plane, int sliceIndex, int timepoint = 0)
        => PrepareSlice(plane, sliceIndex, timepoint);

    internal CenterSlice FinalizeInteractive(
        PreparedSlice prepared, IntensityWindow.Bounds? window,
        SegmentationLut? lut = null, int maxDimension = 512)
        => new(Finalize(prepared, window, lut, maxDimension), prepared.Cfg.Labels);

    /// Decide whether this volume should be rendered as a coloured segmentation
    /// and, if so, build the shared label→RGB LUT. Returns null (→ percentile
    /// windowing) when colouring is Off, the datatype/scaling is intensity-like,
    /// or the sampled center slices don't look like integer labels.
    ///
    /// Detection is deliberately conservative: only integer or float datatypes
    /// with identity scaling are considered, every sampled value must be integral
    /// (so a float intensity image with continuous values is rejected), the
    /// distinct-label count must stay under <see cref="SegmentationLut.MaxLabels"/>
    /// (a resource guard, not a discriminator — real label counts and intensity
    /// value counts overlap completely), AND the sampled voxels must be
    /// piecewise-constant rather than noisy — see <see cref="IsPiecewiseConstant"/>.
    /// Integrality alone is vacuous for an integer datatype (a uint8 anatomical is
    /// integral by construction), so piecewise-constancy is the gate that actually
    /// separates a label map from a normalised intensity image of the same
    /// datatype. Sampling reuses the three center slices (the same voxels the
    /// intensity window pools), so detection adds no extra read on the off path.
    /// The one exception is a single-label center sample, which triggers a
    /// full-volume-0 confirm before committing to the binary (white) LUT — see
    /// <see cref="ScanVolume0"/>.
    public SegmentationLut? BuildSegmentationLut(MiqRenderingOptions options)
    {
        if (!SegmentationEligible(options)) return null;

        var prepared = new[]
        {
            PrepareSlice(SlicePlane.Coronal),
            PrepareSlice(SlicePlane.Sagittal),
            PrepareSlice(SlicePlane.Axial),
        };
        if (CollectLabels(prepared) is not { } labels) return null;
        if (!(IsPiecewiseConstant(prepared) ?? true)) return null;
        return FinishSegmentationLut(labels, options);
    }

    // Cheap, no-decode eligibility gate: only integer/float identity-scaled data in
    // a non-Off mode is ever a segmentation candidate. Separated from the decode so
    // CenterInteractiveState can check it before deciding whether to run label
    // detection over slices it has already prepared. Equivalent to the original
    // guard (SclInter != 0 || (SclSlope != 0 && SclSlope != 1) → reject), negated.
    private bool SegmentationEligible(MiqRenderingOptions options)
    {
        if (options.Segmentation == MiqSegmentationColoring.Off) return false;
        if (!IsLabelCandidateDatatype(H.Datatype)) return false;
        // Non-identity scaling means the stored values are intensity, not labels.
        return H.SclInter == 0f && (H.SclSlope == 0f || H.SclSlope == 1f);
    }

    // Collect the distinct foreground labels present in already-prepared center
    // slices. Returns null when the data is not label-like — any RGB slice, any
    // fractional value (→ intensity), or more than MaxLabels distinct values (→ a
    // dense intensity image). Background (0) is removed; an all-background sample
    // yields an empty set, which FinishSegmentationLut maps to null.
    private static HashSet<int>? CollectLabels(PreparedSlice[] prepared)
    {
        var labels = new HashSet<int>();
        foreach (var p in prepared)
        {
            if (p.Gray is not { } g) return null; // RGB: not labels
            foreach (var v in g)
            {
                if (!MiqCompat.IsFinite(v)) continue;
                var label = MiqCompat.RoundToInt(v);
                if (Math.Abs(v - label) > 1e-3f) return null;        // fractional → intensity
                if (labels.Add(label) && labels.Count > SegmentationLut.MaxLabels)
                    return null;                                     // too many → intensity
            }
        }
        labels.Remove(0); // background is always black; only foreground labels colour
        return labels;
    }

    // A label map is built from regions of constant value, so adjacent foreground
    // voxels are nearly always equal; an intensity image is noisy at every voxel.
    // This is what actually separates the two for an integer datatype, where the
    // integrality check in CollectLabels is vacuous (any integer image passes it)
    // and the distinct-value count overlaps completely between real label maps and
    // normalised intensity images of the same bit depth.
    //
    // Measures, over the three prepared center slices, the fraction of
    // horizontally adjacent voxel pairs that differ, counting only pairs where
    // BOTH voxels are foreground (non-zero) — background dominates most volumes
    // and would otherwise swamp the ratio. Adjacency runs along a stored row only
    // (Gray is row-major, column-fastest — see GatherGray) and never wraps between
    // rows; a non-finite voxel breaks the chain rather than pairing across the gap.
    //
    // Returns false (reject as intensity) when the pooled ratio exceeds 0.30;
    // true (accept) when it does not; null (abstain — too little foreground to
    // judge) when fewer than 256 qualifying pairs were sampled in total. Callers
    // must treat null as "keep the existing verdict", not as a rejection — sparse
    // masks and thin structures have little foreground and must not be rejected
    // for lack of samples.
    //
    // May exit early ONLY on the reject side (>= 1024 pairs sampled AND running
    // ratio > 0.60 — well clear of the worst real label-map transient, 0.201).
    // There is no accept-side early exit: FinishSegmentationLut's FreeSurfer
    // signature test and the rank-based random palette are both functions of the
    // COMPLETE label set, so accepting must always finish decoding all three
    // planes (e.g. wmparc has collected only 14 of its 136 labels at 2000 pairs).
    private static bool? IsPiecewiseConstant(PreparedSlice[] prepared)
    {
        const long RejectMinPairs = 1024;
        const double RejectRatio = 0.60;
        const long MinSamplePairs = 256;
        const double AcceptMaxRatio = 0.30;

        long total = 0, diff = 0;
        foreach (var p in prepared)
        {
            var g = p.Gray!;
            var w = p.Cfg.SliceWidth;
            var h = p.Cfg.SliceHeight;
            for (var row = 0; row < h; row++)
            {
                var rowStart = row * w;
                // 0 doubles as "no foreground predecessor": row start, background,
                // and the voxel after a non-finite gap all suppress the pair.
                var prev = 0;
                for (var col = 0; col < w; col++)
                {
                    var v = g[rowStart + col];
                    if (!MiqCompat.IsFinite(v)) { prev = 0; continue; }
                    var rounded = MiqCompat.RoundToInt(v);
                    if (rounded != 0 && prev != 0)
                    {
                        total++;
                        if (rounded != prev) diff++;
                        if (total >= RejectMinPairs && (double)diff / total > RejectRatio)
                            return false;
                    }
                    prev = rounded;
                }
            }
        }

        if (total < MinSamplePairs) return null;
        return (double)diff / total <= AcceptMaxRatio;
    }

    // Choose the final LUT from a collected label set.
    // A binary mask (one foreground label) reads best as plain white — a palette
    // colour conveys nothing when there's only one structure. The center slices can
    // MISS a spatially localized second structure, so a single-label center sample
    // is only provisional: confirm it against the whole first volume before
    // committing to the (sticky) monochrome LUT. Multi-label volumes pick the
    // FreeSurfer palette (Auto only) or the random palette.
    private SegmentationLut? FinishSegmentationLut(HashSet<int> labels, MiqRenderingOptions options)
    {
        if (labels.Count == 0) return null;

        if (labels.Count == 1)
        {
            switch (ScanVolume0(GetSingle(labels)))
            {
                case Vol0LabelShape.Intensity: return null;           // periphery is fractional → not labels
                case Vol0LabelShape.Binary:
                    return new SegmentationLut(useFreeSurfer: false, monochromeWhite: true);
                // MultiLabel → fall through and colour as a normal label volume.
            }
        }

        var useFreeSurfer = options.Segmentation == MiqSegmentationColoring.Auto
            && SegmentationLut.LooksLikeFreeSurfer(labels);
        return useFreeSurfer
            ? new SegmentationLut(useFreeSurfer: true)
            : SegmentationLut.Random(labels);
    }

    private enum Vol0LabelShape { Intensity, Binary, MultiLabel }

    private static int GetSingle(HashSet<int> set)
    {
        foreach (var v in set) return v;
        return 0;
    }

    // Confirm the binary-vs-multi decision against the ENTIRE first volume. Only
    // called when the center sample already looks binary (exactly one foreground
    // label), so the common multi-label and intensity files never reach it. Returns
    // the instant a disqualifying voxel appears (a second distinct non-zero label
    // -> MultiLabel, or a fractional value -> Intensity), so only a true binary mask
    // scans to completion. Volume 0 is fully present even on partial vol-0-first
    // loads (the payload is sized to it).
    private Vol0LabelShape ScanVolume0(int label)
    {
        // MIF custom strides: volume 0's elements may be interleaved with other
        // volumes, so fall back to the correct (slower) per-voxel walk. Standard
        // row-major formats (NIfTI/MGH/NRRD) take the fast contiguous path below.
        if (_image.ElementStrides is not null)
            return ScanVolume0PerVoxel(label);

        // Volume 0 is the first N payload elements, contiguous. The binary question
        // depends only on which values are present, not their position, so scan the
        // raw buffer sequentially with the datatype switch hoisted OUT of the loop
        // and integers compared directly (no VoxelElementIndex, no bounds checks per
        // voxel, no float conversion for integer data).
        var s = _image.Storage;
        var bpv = H.Datatype.BytesPerVoxel();
        var elems = Math.Min((long)Width * Height * Depth, (long)_image.PayloadCount / bpv);
        var le = H.LittleEndian;
        long p = _image.PayloadOffset;
        var end = p + elems * bpv;

        static int Rd16(byte[] a, long o, bool le) =>
            le ? a[o] | (a[o + 1] << 8) : (a[o] << 8) | a[o + 1];
        static int Rd32(byte[] a, long o, bool le) =>
            le ? a[o] | (a[o + 1] << 8) | (a[o + 2] << 16) | (a[o + 3] << 24)
               : (a[o] << 24) | (a[o + 1] << 16) | (a[o + 2] << 8) | a[o + 3];
        static long Rd64(byte[] a, long o, bool le)
        {
            long lo = (uint)Rd32(a, o, le), hi = (uint)Rd32(a, o + 4, le);
            return le ? (hi << 32) | lo : (lo << 32) | hi;
        }

        switch (H.Datatype)
        {
            case MiqDatatype.Uint8:
                for (var o = p; o < end; o += 1)
                { int v = s[o]; if (v != 0 && v != label) return Vol0LabelShape.MultiLabel; }
                break;
            case MiqDatatype.Int8:
                for (var o = p; o < end; o += 1)
                { int v = (sbyte)s[o]; if (v != 0 && v != label) return Vol0LabelShape.MultiLabel; }
                break;
            case MiqDatatype.Uint16:
                for (var o = p; o < end; o += 2)
                { int v = Rd16(s, o, le); if (v != 0 && v != label) return Vol0LabelShape.MultiLabel; }
                break;
            case MiqDatatype.Int16:
                for (var o = p; o < end; o += 2)
                { int v = (short)Rd16(s, o, le); if (v != 0 && v != label) return Vol0LabelShape.MultiLabel; }
                break;
            case MiqDatatype.Int32:
            case MiqDatatype.Uint32: // label values are small; signed compare is exact for them
                for (var o = p; o < end; o += 4)
                { int v = Rd32(s, o, le); if (v != 0 && v != label) return Vol0LabelShape.MultiLabel; }
                break;
            case MiqDatatype.Float32:
                for (var o = p; o < end; o += 4)
                {
                    var f = MiqCompat.Int32BitsToSingle(Rd32(s, o, le));
                    if (!MiqCompat.IsFinite(f)) continue;
                    var r = MiqCompat.RoundToInt(f);
                    if (Math.Abs(f - r) > 1e-3f) return Vol0LabelShape.Intensity;
                    if (r != 0 && r != label) return Vol0LabelShape.MultiLabel;
                }
                break;
            case MiqDatatype.Float64:
                for (var o = p; o < end; o += 8)
                {
                    var d = MiqCompat.Int64BitsToDouble(Rd64(s, o, le));
                    if (double.IsNaN(d) || double.IsInfinity(d)) continue;
                    var r = (int)Math.Round(d, MidpointRounding.ToEven);
                    if (Math.Abs(d - r) > 1e-3) return Vol0LabelShape.Intensity;
                    if (r != 0 && r != label) return Vol0LabelShape.MultiLabel;
                }
                break;
            default:
                return ScanVolume0PerVoxel(label); // RGB shouldn't reach here, but be safe
        }
        return Vol0LabelShape.Binary;
    }

    // Correct-for-any-layout fallback (MIF custom strides): walks every voxel of
    // volume 0 through VoxelElementIndex. Slower, but only reached for the rare
    // strided-format binary candidate.
    private Vol0LabelShape ScanVolume0PerVoxel(int label)
    {
        for (var z = 0; z < Depth; z++)
            for (var y = 0; y < Height; y++)
                for (var x = 0; x < Width; x++)
                {
                    var v = Voxel(x, y, z, 0);
                    if (!MiqCompat.IsFinite(v)) continue;
                    var rounded = MiqCompat.RoundToInt(v);
                    if (Math.Abs(v - rounded) > 1e-3f) return Vol0LabelShape.Intensity;
                    if (rounded != 0 && rounded != label) return Vol0LabelShape.MultiLabel;
                }
        return Vol0LabelShape.Binary;
    }

    private static bool IsLabelCandidateDatatype(MiqDatatype dt) => dt switch
    {
        // Integer datatypes are the obvious carriers. Float datatypes are included
        // because label maps are frequently re-saved as float by downstream tools
        // (resampling, arithmetic on the labels) while still holding integral
        // values; the per-value integrality check in BuildSegmentationLut is what
        // actually gates them, so a genuine float intensity image (continuous
        // values, or > MaxLabels distinct) is still rejected. Rgb24/Rgba32 take the
        // RGB path, not labels.
        MiqDatatype.Int8 or MiqDatatype.Uint8 or MiqDatatype.Int16 or MiqDatatype.Uint16
            or MiqDatatype.Int32 or MiqDatatype.Uint32
            or MiqDatatype.Float32 or MiqDatatype.Float64 => true,
        _ => false,
    };

    private PreparedSlice PrepareSlice(
        SlicePlane plane, int? sliceIndex = null, int timepoint = 0)
    {
        var dx = Math.Max(1e-6f, Math.Abs(Pixdim(1)));
        var dy = Math.Max(1e-6f, Math.Abs(Pixdim(2)));
        var dz = Math.Max(1e-6f, Math.Abs(Pixdim(3)));

        var dims = new[] { Width, Height, Depth };
        var pixs = new[] { dx, dy, dz };
        var plan = PlanFor(plane);
        var (sliceAxis, hAxis, vAxis) = (plan.SliceAxis, plan.HAxis, plan.VAxis);

        var cfg = new SliceConfig
        {
            SliceAxis = sliceAxis, HAxis = hAxis, VAxis = vAxis,
            HDim = dims[hAxis], VDim = dims[vAxis],
            SliceWidth = dims[hAxis], SliceHeight = dims[vAxis],
            InnerCount = dims[hAxis], OuterCount = dims[vAxis],
            PixelSpacingX = pixs[hAxis], PixelSpacingY = pixs[vAxis],
            HReversed = plan.HReversed, VReversed = plan.VReversed,
            Labels = plan.Labels,
        };
        var maxExt = Math.Max(Width * dx, Math.Max(Height * dy, Depth * dz));
        var lastSlice = Math.Max(0, dims[sliceAxis] - 1);
        var slice = sliceIndex is { } si
            ? Math.Min(lastSlice, Math.Max(0, si))
            : Math.Max(0, dims[sliceAxis] / 2);

        if (H.Datatype is MiqDatatype.Rgb24 or MiqDatatype.Rgba32)
        {
            var rgb = new byte[cfg.SliceWidth * cfg.SliceHeight * 3];
            var o = 0;
            for (var row = 0; row < cfg.OuterCount; row++)
                for (var col = 0; col < cfg.InnerCount; col++)
                {
                    var (x, y, z) = cfg.Coordinate(slice, row, col);
                    ReadRgb(x, y, z, timepoint, rgb, o);
                    o += 3;
                }
            return new PreparedSlice(gray: null, rgb: rgb, cfg, maxExt);
        }

        return new PreparedSlice(gray: GatherGray(cfg, slice, timepoint), rgb: null, cfg, maxExt);
    }

    // Decode one grayscale slice into a float[] in row-major (col-fastest) order.
    // Equivalent to the former `values[i++] = Voxel(x,y,z,t)` loop, but with the
    // per-slice constants (datatype, endianness, bytes-per-voxel, payload bounds,
    // scaling) hoisted OUT of the inner loop and the value read inlined straight
    // from Storage — skipping Voxel()→RawVoxelValue()'s repeated BytesPerVoxel
    // calls, redundant bounds check, and the MiqBinaryReader.Slice() span build.
    //
    // Bit-identity notes (this is the step most prone to a silent decode bug):
    //   • x,y,z are always in range here (slice/row/col are clamped to their axis
    //     dims and {SliceAxis,HAxis,VAxis} is a permutation of {0,1,2}), so Voxel's
    //     x/y/z guard never fires; only the t guard and the byteOffset guard matter.
    //   • An out-of-range timepoint zeroes the whole slice WITHOUT scaling — exactly
    //     Voxel's `return 0f` on the t guard (a fresh float[] is already zeroed).
    //   • An out-of-range byteOffset yields plain 0f, NOT `intercept` — Voxel returns
    //     0f before reaching the scaling line, so scaling must be skipped on that path.
    //   • In-range reads apply `slope != 0 ? raw*slope+intercept : raw`, as Voxel does.
    // Strided layouts (MIF) and datatypes outside the hot set fall back to the
    // verbatim per-voxel Voxel() loop, staying identical there.
    private float[] GatherGray(SliceConfig cfg, int slice, int timepoint)
    {
        var values = new float[cfg.SliceWidth * cfg.SliceHeight];

        // Out-of-range timepoint → all voxels 0f (Voxel's t guard). Array is zeroed.
        if (timepoint < 0 || timepoint >= Volumes) return values;

        var dt = H.Datatype;
        var hot = dt is MiqDatatype.Uint8 or MiqDatatype.Int16
            or MiqDatatype.Uint16 or MiqDatatype.Float32;
        if (_image.ElementStrides is not null || !hot)
        {
            var j = 0;
            for (var row = 0; row < cfg.OuterCount; row++)
                for (var col = 0; col < cfg.InnerCount; col++)
                {
                    var (x, y, z) = cfg.Coordinate(slice, row, col);
                    values[j++] = Voxel(x, y, z, timepoint);
                }
            return values;
        }

        var s = _image.Storage;
        var le = H.LittleEndian;
        var bpv = dt.BytesPerVoxel();
        var payloadCount = _image.PayloadCount;
        var baseOff = _image.PayloadOffset;
        var slope = H.SclSlope;
        var intercept = H.SclInter;
        var scaled = slope != 0f;

        // For the standard row-major layout the relative byte offset is AFFINE in
        // (row, col): VoxelElementIndex = slice·esS + h·esH + v·esV + t·tStride with
        // h = ±col, v = ±row, so the offset advances by a fixed per-column / per-row
        // stride. Precompute the two strides + the (row 0, col 0) corner once, then
        // the inner loop just adds a stride per step — no per-voxel Coordinate(),
        // VoxelElementIndex(), or bounds check. Element strides: x→1, y→Width, z→W·H.
        long EStride(int axis) => axis == 0 ? 1 : axis == 1 ? Width : (long)Width * Height;
        var esH = EStride(cfg.HAxis);
        var esV = EStride(cfg.VAxis);
        var esS = EStride(cfg.SliceAxis);
        var tStride = (long)Width * Height * Depth;
        var h0 = cfg.HReversed ? cfg.HDim - 1 : 0;
        var v0 = cfg.VReversed ? cfg.VDim - 1 : 0;
        var baseElem = (long)slice * esS + (long)h0 * esH + (long)v0 * esV + (long)timepoint * tStride;
        var colElemStride = cfg.HReversed ? -esH : esH; // h = HReversed ? HDim-1-col : col
        var rowElemStride = cfg.VReversed ? -esV : esV; // v = VReversed ? VDim-1-row : row

        // The map is affine and monotone per axis, so its byte-offset extremes over
        // the slice rectangle are at the four corners. Computed in long (no wrap) to
        // decide IN-RANGE without false positives. When the whole slice is in range
        // every offset is < payloadCount ≤ int.MaxValue, so the fast loop below runs
        // in int with no per-voxel guard and no overflow. equals byte-for-byte what
        // the guarded loop produces (same Storage bytes, same decode, same scaling) —
        // the offsets are the same VoxelElementIndex·bpv values, just accumulated.
        var lastRow = cfg.OuterCount - 1L;
        var lastCol = cfg.InnerCount - 1L;
        long minElem = baseElem, maxElem = baseElem;
        minElem += rowElemStride < 0 ? lastRow * rowElemStride : 0;
        maxElem += rowElemStride > 0 ? lastRow * rowElemStride : 0;
        minElem += colElemStride < 0 ? lastCol * colElemStride : 0;
        maxElem += colElemStride > 0 ? lastCol * colElemStride : 0;

        if (cfg.OuterCount > 0 && cfg.InnerCount > 0
            && minElem * bpv >= 0 && maxElem * bpv + bpv <= payloadCount)
        {
            var colByte = (int)colElemStride * bpv;
            var rowByte = (int)rowElemStride * bpv;
            var rowOff = baseOff + (int)(baseElem * bpv);
            var i2 = 0;
            for (var row = 0; row < cfg.OuterCount; row++)
            {
                var o = rowOff;
                for (var col = 0; col < cfg.InnerCount; col++)
                {
                    float raw;
                    switch (dt)
                    {
                        case MiqDatatype.Uint8:
                            raw = s[o];
                            break;
                        case MiqDatatype.Int16:
                            raw = (short)(le ? s[o] | (s[o + 1] << 8) : (s[o] << 8) | s[o + 1]);
                            break;
                        case MiqDatatype.Uint16:
                            raw = (ushort)(le ? s[o] | (s[o + 1] << 8) : (s[o] << 8) | s[o + 1]);
                            break;
                        default: // Float32 (only remaining hot type)
                            var bits = le
                                ? s[o] | (s[o + 1] << 8) | (s[o + 2] << 16) | (s[o + 3] << 24)
                                : (s[o] << 24) | (s[o + 1] << 16) | (s[o + 2] << 8) | s[o + 3];
                            raw = MiqCompat.Int32BitsToSingle(bits);
                            break;
                    }
                    values[i2++] = scaled ? raw * slope + intercept : raw;
                    o += colByte;
                }
                rowOff += rowByte;
            }
            return values;
        }

        // Out-of-range fallback (e.g. an out-of-range timepoint on a partial vol-0
        // load makes some offsets exceed the payload): the exact per-voxel guarded
        // path, byte-identical to Voxel's 0f-on-out-of-range behaviour.
        var i = 0;
        for (var row = 0; row < cfg.OuterCount; row++)
            for (var col = 0; col < cfg.InnerCount; col++)
            {
                var (x, y, z) = cfg.Coordinate(slice, row, col);
                var byteOffset = _image.VoxelElementIndex(x, y, z, timepoint) * bpv;
                if (byteOffset < 0 || byteOffset + bpv > payloadCount)
                {
                    values[i++] = 0f; // matches Voxel's byteOffset guard (plain 0f, no scaling)
                    continue;
                }
                var o = baseOff + byteOffset;
                float raw;
                switch (dt)
                {
                    case MiqDatatype.Uint8:
                        raw = s[o];
                        break;
                    case MiqDatatype.Int16:
                        raw = (short)(le ? s[o] | (s[o + 1] << 8) : (s[o] << 8) | s[o + 1]);
                        break;
                    case MiqDatatype.Uint16:
                        raw = (ushort)(le ? s[o] | (s[o + 1] << 8) : (s[o] << 8) | s[o + 1]);
                        break;
                    default: // Float32 (only remaining hot type)
                        var bits = le
                            ? s[o] | (s[o + 1] << 8) | (s[o + 2] << 16) | (s[o + 3] << 24)
                            : (s[o] << 24) | (s[o + 1] << 16) | (s[o + 2] << 8) | s[o + 3];
                        raw = MiqCompat.Int32BitsToSingle(bits);
                        break;
                }
                values[i++] = scaled ? raw * slope + intercept : raw;
            }
        return values;
    }

    // Reads the 3 RGB bytes for a voxel into dst[off..off+2]. Alpha (rgba32's
    // 4th byte) is ignored — the preview is opaque. The bounds guard uses the
    // literal 3, not bytes-per-voxel, so rgba32's 4th byte is never required.
    // Out-of-range voxels leave the destination at 0 (black). Port of the RGB
    // read path in MIQVolume.prepareSlice.
    private void ReadRgb(int x, int y, int z, int t, byte[] dst, int off)
    {
        if (x < 0 || x >= Width || y < 0 || y >= Height ||
            z < 0 || z >= Depth || t < 0 || t >= Volumes) return;

        var voxelIndex = _image.VoxelElementIndex(x, y, z, t);
        var byteOffset = voxelIndex * H.Datatype.BytesPerVoxel();
        if (byteOffset < 0 || byteOffset + 3 > _image.PayloadCount) return;

        dst[off] = _image.Byte(byteOffset);
        dst[off + 1] = _image.Byte(byteOffset + 1);
        dst[off + 2] = _image.Byte(byteOffset + 2);
    }

    private float Pixdim(int i) => i < H.Pixdim.Count ? H.Pixdim[i] : 1f;

    private float Voxel(int x, int y, int z, int t)
    {
        if (x < 0 || x >= Width || y < 0 || y >= Height ||
            z < 0 || z >= Depth || t < 0 || t >= Volumes) return 0f;

        var voxelIndex = _image.VoxelElementIndex(x, y, z, t);
        var bpv = H.Datatype.BytesPerVoxel();
        var byteOffset = voxelIndex * bpv;
        if (byteOffset < 0 || byteOffset + bpv > _image.PayloadCount) return 0f;

        var raw = RawVoxelValue(byteOffset);
        var slope = H.SclSlope;
        var intercept = H.SclInter;
        return slope != 0 ? raw * slope + intercept : raw;
    }

    private float RawVoxelValue(int byteOffset)
    {
        var bpv = H.Datatype.BytesPerVoxel();
        if (byteOffset < 0 || byteOffset + bpv > _image.PayloadCount) return 0f;
        var abs = _image.PayloadOffset + byteOffset;
        var s = _image.Storage;
        var le = H.LittleEndian;
        return H.Datatype switch
        {
            MiqDatatype.Uint8 => _image.Byte(byteOffset),
            MiqDatatype.Int8 => (sbyte)_image.Byte(byteOffset),
            MiqDatatype.Int16 => (short)MiqBinaryReader.Uint16(s, abs, le),
            MiqDatatype.Uint16 => MiqBinaryReader.Uint16(s, abs, le),
            MiqDatatype.Int32 => (int)MiqBinaryReader.Uint32(s, abs, le),
            MiqDatatype.Uint32 => MiqBinaryReader.Uint32(s, abs, le),
            MiqDatatype.Float32 => MiqCompat.Int32BitsToSingle((int)MiqBinaryReader.Uint32(s, abs, le)),
            MiqDatatype.Float64 => (float)MiqCompat.Int64BitsToDouble((long)MiqBinaryReader.Uint64(s, abs, le)),
            // RGB datatypes normally take the dedicated ReadRgb path; this
            // luminance fallback only fires if a grayscale read is ever asked
            // of RGB data, so it still renders something rather than nothing.
            MiqDatatype.Rgb24 or MiqDatatype.Rgba32 =>
                0.299f * _image.Byte(byteOffset) + 0.587f * _image.Byte(byteOffset + 1)
                + 0.114f * _image.Byte(byteOffset + 2),
            _ => 0f,
        };
    }
}
