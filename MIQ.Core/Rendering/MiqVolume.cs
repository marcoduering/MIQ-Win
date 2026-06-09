using MIQ.Parsing;

namespace MIQ.Rendering;

/// View-orientation mode. <c>Stored</c> renders the volume's axes exactly as
/// stored (legacy behaviour). <c>Neurological</c>/<c>Radiological</c> reorient
/// and relabel each plane to a canonical anatomical view; they differ only by
/// the in-plane R/L flip on coronal & axial (sagittal is identical in both).
/// Port of MIQCore's ViewOrientation (persisted as "stored"/"ras"/"las").
public enum MiqOrientation { Stored, Neurological, Radiological }

// Percentiles are computed over voxels pooled from all three center slices
// (see CenterSlices) so every plane shares one intensity window. 2/98 clips
// the histogram tails (noise, sparse hyper-intensities) harder than 1/99 for
// better mid-range grayscale contrast.
public readonly record struct MiqRenderingOptions(
    double LowerPercentile = 2.0,
    double UpperPercentile = 98.0,
    MiqOrientation Orientation = MiqOrientation.Stored);

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
/// are rendered as opaque RGB (alpha dropped) and bypass windowing. Port of
/// MIQCore's MIQVolume.
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
    {
        var planes = new[] { SlicePlane.Coronal, SlicePlane.Sagittal, SlicePlane.Axial };

        var prepared = new List<(SlicePlane plane, PreparedSlice p)>();
        var pooled = new List<float>();
        foreach (var plane in planes)
        {
            var p = PrepareSlice(plane);
            prepared.Add((plane, p));
            if (p.Gray is { } g) pooled.AddRange(g); // RGB slices bypass pooling
        }

        var bounds = IntensityWindow.GetBounds(pooled, options.LowerPercentile, options.UpperPercentile);

        var result = new Dictionary<SlicePlane, CenterSlice>();
        foreach (var (plane, p) in prepared)
            result[plane] = new CenterSlice(Finalize(p, bounds, maxDimension), p.Cfg.Labels);
        return result;
    }

    // Turn a prepared slice into a finished image: RGB → opaque RgbImage (no
    // window); grayscale → windowed GrayscaleImage. Both are FOV-resampled.
    private static SliceImage Finalize(PreparedSlice p, IntensityWindow.Bounds? window, int maxDimension)
    {
        var cfg = p.Cfg;
        if (p.Rgb is { } rgb)
            return SliceImage.FromRgb(new RgbImage(cfg.SliceWidth, cfg.SliceHeight, rgb)
                .ResampledForPixelSpacing(cfg.PixelSpacingX, cfg.PixelSpacingY, p.MaxExt, maxDimension));

        var values = p.Gray!;
        var pixels = window is { } b ? IntensityWindow.Apply(values, b) : new byte[values.Length];
        return SliceImage.Gray(new GrayscaleImage(cfg.SliceWidth, cfg.SliceHeight, pixels)
            .ResampledForPixelSpacing(cfg.PixelSpacingX, cfg.PixelSpacingY, p.MaxExt, maxDimension));
    }

    private sealed class SliceConfig
    {
        public int SliceWidth, SliceHeight, OuterCount, InnerCount;
        public float PixelSpacingX, PixelSpacingY;
        public int SliceAxis, HAxis, VAxis, HDim, VDim;
        public bool HReversed, VReversed;
        public SliceOrientationLabels Labels = SliceOrientationLabels.Unknown;

        public (int x, int y, int z) Coordinate(int slice, int row, int col)
        {
            var c = new int[3];
            c[SliceAxis] = slice;
            c[HAxis] = HReversed ? HDim - 1 - col : col;
            c[VAxis] = VReversed ? VDim - 1 - row : row;
            return (c[0], c[1], c[2]);
        }
    }

    // A read-but-not-yet-finished slice. Exactly one of Gray (intensity floats,
    // to be windowed) / Rgb (interleaved RGB bytes, already display-ready) is set.
    private readonly struct PreparedSlice(float[]? gray, byte[]? rgb, SliceConfig cfg, float maxExt)
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

    /// Extract a single slice at an arbitrary index using a precomputed window.
    public CenterSlice ExtractSlice(
        SlicePlane plane, int sliceIndex, IntensityWindow.Bounds? window,
        int maxDimension = 512, int timepoint = 0)
    {
        var p = PrepareSlice(plane, sliceIndex, timepoint);
        return new CenterSlice(Finalize(p, window, maxDimension), p.Cfg.Labels);
    }

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

        var values = new float[cfg.SliceWidth * cfg.SliceHeight];
        var i = 0;
        for (var row = 0; row < cfg.OuterCount; row++)
            for (var col = 0; col < cfg.InnerCount; col++)
            {
                var (x, y, z) = cfg.Coordinate(slice, row, col);
                values[i++] = Voxel(x, y, z, timepoint);
            }
        return new PreparedSlice(gray: values, rgb: null, cfg, maxExt);
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
