using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MIQ;
using MIQ.Parsing;
using MIQ.Rendering;

namespace QuickLook.Plugin.MIQ;

/// <summary>
/// Interactive triplanar viewer: the 2×2 composite (coronal / sagittal / axial
/// + metadata) with linked navigation. A single focus voxel is shared by all
/// three planes:
///   • mouse wheel over a slice scrolls *that* plane (its perpendicular axis);
///   • Alt + mouse wheel anywhere changes the active volume (4-D scrubbing);
///   • click / drag in a slice sets the in-plane position, which moves the
///     other two planes to the corresponding slices.
/// A volume scrubber is shown in the metadata quadrant when Volumes > 1.
/// Crosshairs mark the focus in every plane. Rendering reuses
/// <see cref="WpfPreviewRenderer"/> so styling stays single-sourced.
/// </summary>
internal sealed class MiqTriPlanarControl : FrameworkElement
{
    // Quadrant assignment + center-hugging alignment (matches the static
    // compositor): coronal TL, sagittal TR, axial BL, metadata BR.
    private static readonly (SlicePlane plane, double ax, double ay)[] Layout =
    [
        (SlicePlane.Coronal, 1, 1),
        (SlicePlane.Sagittal, 0, 1),
        (SlicePlane.Axial, 1, 0),
    ];

    // Non-readonly so ExpandVolume() can swap in the full volume.
    private MiqVolume _vol;
    private readonly IReadOnlyList<MetadataEntry> _metadata;
    private readonly MiqRenderingOptions _options;
    private readonly MiqSettings _settings;

    // Focus voxel in storage-axis coordinates (0 = Width, 1 = Height, 2 = Depth).
    private readonly int[] _focus;

    // Current volume (timepoint) index for 4-D datasets.
    private int _volumeIndex;
    // True once the full decompressed volume is available (enables scrubbing).
    private bool _isExpanded;
    // True when only volume 0 is available and expansion is impossible (file too
    // large): the scrubber is replaced by a "first volume only" notice.
    private readonly bool _expansionBlocked;
    // Set when the full volume can be loaded on demand (partial, not blocked).
    // The first scrub gesture invokes it; the load runs lazily so that flicking
    // through previews never triggers background work. Null once expanded/blocked.
    private readonly Action? _onExpandRequested;
    private bool _expansionRequested; // guards a single trigger; flips row to "loading…"

    // Per-volume window cache (populated lazily on scrub when PerVolumeWindow = true).
    private readonly bool _perVolumeWindow;
    private readonly Dictionary<int, IntensityWindow.Bounds?> _windowCache;
    private CancellationTokenSource? _windowCts;

    // Live intensity window. Starts at the pooled 2/98 window; right-drag or
    // Alt+left-drag adjusts it. _winRev bumps on every change so the per-plane
    // cache knows to re-extract.
    private readonly bool _hasWindow;
    private double _winLow, _winHigh;
    private readonly double _winRefSpan; // drag sensitivity reference
    private int _winRev;

    // Per-plane cache: (slice index, volume index, window revision) → slice.
    private readonly Dictionary<SlicePlane, (int index, int vol, int rev, CenterSlice slice)> _cache = new();

    private bool _navDrag;   // left-drag: focus navigation
    private bool _wlDrag;    // right-drag: window/level
    private bool _scrubDrag; // left-drag on scrubber track
    private Point _wlStart;
    private double _wlStartLow, _wlStartHigh;

    // Scrubber geometry — computed each render, used for mouse hit-testing.
    private Rect _scrubberRect;
    private double _scrubTrackX0, _scrubTrackX1;

    // Crosshairs stay hidden until the user first navigates.
    private bool _interacted;

    internal MiqTriPlanarControl(
        MiqVolume volume,
        IntensityWindow.Bounds? window,
        IReadOnlyDictionary<SlicePlane, CenterSlice> initial,
        IReadOnlyList<MetadataEntry> metadata,
        MiqRenderingOptions options,
        MiqSettings settings,
        bool isExpanded = true,
        bool expansionBlocked = false,
        Action? onExpandRequested = null)
    {
        _vol = volume;
        _metadata = metadata;
        _options = options;
        _settings = settings;
        _isExpanded = isExpanded;
        _expansionBlocked = expansionBlocked;
        _onExpandRequested = onExpandRequested;

        _hasWindow = window.HasValue;
        if (window is { } w)
        {
            _winLow = w.Low;
            _winHigh = w.High;
        }
        _winRefSpan = Math.Max(1e-6, _winHigh - _winLow);

        _perVolumeWindow = settings.PerVolumeWindow;
        // Seed the cache with volume 0's window so the first render is instant.
        _windowCache = new Dictionary<int, IntensityWindow.Bounds?> { [0] = window };

        _focus = [_vol.Dim(0) / 2, _vol.Dim(1) / 2, _vol.Dim(2) / 2];
        foreach (var (plane, _, _) in Layout)
            _cache[plane] = (_vol.CenterIndex(plane), 0, 0, initial[plane]);

        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        Unloaded += (_, _) => { _windowCts?.Cancel(); _windowCts = null; };
    }

    // The full volume can still be loaded, hasn't been requested yet, and a
    // callback is wired — i.e. the scrubber is in its Loadable state.
    private bool CanExpandOnDemand =>
        !_isExpanded && !_expansionBlocked && !_expansionRequested && _onExpandRequested != null;

    /// First scrub gesture on a not-yet-loaded multi-volume file: flip the row to
    /// "loading…" and kick off the background load (which calls <see cref="ExpandVolume"/>
    /// when done). Loads lazily so flicking through previews never triggers work.
    private void RequestExpansion()
    {
        if (!CanExpandOnDemand) return;
        _expansionRequested = true;
        InvalidateVisual();
        _onExpandRequested!();
    }

    /// <summary>
    /// Swaps in the fully-decompressed volume (called from the UI thread once
    /// background expansion finishes). Enables the volume scrubber.
    /// </summary>
    internal void ExpandVolume(MiqVolume vol)
    {
        _windowCts?.Cancel();
        _windowCts = null;
        _vol = vol;
        _isExpanded = true;
        _cache.Clear(); // old slices came from partial storage — discard
        // Volume 0's window is valid for both partial and full storage (same
        // voxels); keep it. Any other entries would only exist if scrubbing
        // happened during load, which is blocked, so the cache is just [0].
        InvalidateVisual();
    }

    // Fill whatever the host gives us (placed stretched in a Grid cell).
    protected override Size MeasureOverride(Size availableSize)
    {
        double w = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        double h = double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height;
        return new Size(w, h);
    }

    private IntensityWindow.Bounds? CurrentWindow() =>
        _hasWindow ? new IntensityWindow.Bounds((float)_winLow, (float)_winHigh) : null;

    private CenterSlice Slice(SlicePlane plane)
    {
        var axis = _vol.AxesFor(plane).sliceAxis;
        var want = _focus[axis];
        if (_cache.TryGetValue(plane, out var c) &&
            c.index == want && c.vol == _volumeIndex && c.rev == _winRev)
            return c.slice;
        var slice = _vol.ExtractSlice(plane, want, CurrentWindow(), timepoint: _volumeIndex);
        _cache[plane] = (want, _volumeIndex, _winRev, slice);
        return slice;
    }

    private readonly struct Frame(Rect coronal, Rect sagittal, Rect axial, Rect meta)
    {
        public Rect Quad(SlicePlane p) =>
            p == SlicePlane.Coronal ? coronal : p == SlicePlane.Sagittal ? sagittal : axial;
        public Rect Meta { get; } = meta;
    }

    // Tight layout (identical math to WpfPreviewRenderer.Render), centered in
    // the control so any leftover is symmetric letterbox.
    private Frame ComputeFrame(Size sz)
    {
        var cor = Slice(SlicePlane.Coronal).Image;
        var sag = Slice(SlicePlane.Sagittal).Image;
        var axi = Slice(SlicePlane.Axial).Image;

        double leftSrc = Math.Max(cor.Width, axi.Width);
        double rightSrc = sag.Width;
        double topSrc = Math.Max(cor.Height, sag.Height);
        double botSrc = axi.Height;

        var scale = Math.Min(sz.Width / (leftSrc + rightSrc),
                             sz.Height / (topSrc + botSrc));
        scale = Math.Max(scale, 1e-6);

        double lW = leftSrc * scale, rW = rightSrc * scale;
        double tH = topSrc * scale, bH = botSrc * scale;
        double ox = (sz.Width - (lW + rW)) / 2;
        double oy = (sz.Height - (tH + bH)) / 2;

        return new Frame(
            new Rect(ox, oy, lW, tH),
            new Rect(ox + lW, oy, rW, tH),
            new Rect(ox, oy + tH, lW, bH),
            new Rect(ox + lW, oy + tH, rW, bH));
    }

    protected override void OnRender(DrawingContext dc)
    {
        var sz = RenderSize;
        dc.DrawRectangle(_settings.BackgroundBrush, null, new Rect(0, 0, sz.Width, sz.Height));
        if (sz.Width < 2 || sz.Height < 2) return;

        var frame = ComputeFrame(sz);
        foreach (var (plane, ax, ay) in Layout)
        {
            var slice = Slice(plane);
            var dst = WpfPreviewRenderer.DrawSlice(dc, slice, frame.Quad(plane), ax, ay, _settings);
            if (dst.IsEmpty) continue;
            if (_interacted) DrawCrosshair(dc, plane, dst);
        }

        var metaRect = frame.Meta;

        var scrubVol = _vol.Volumes > 1 ? _volumeIndex : -1;
        var mode =
            _isExpanded ? WpfPreviewRenderer.ScrubMode.Expanded :
            _expansionBlocked ? WpfPreviewRenderer.ScrubMode.Blocked :
            _expansionRequested ? WpfPreviewRenderer.ScrubMode.Loading :
            _onExpandRequested != null ? WpfPreviewRenderer.ScrubMode.Loadable :
            WpfPreviewRenderer.ScrubMode.Loading;
        var (scrubHit, tx0, tx1) = WpfPreviewRenderer.DrawMetadata(
            dc, _metadata, metaRect, _settings,
            scrubVol: scrubVol, scrubTotal: _vol.Volumes, scrubMode: mode);
        _scrubberRect = scrubHit;
        _scrubTrackX0 = tx0;
        _scrubTrackX1 = tx1;
    }

    private void DrawCrosshair(DrawingContext dc, SlicePlane plane, Rect dst)
    {
        var plan = _vol.PlanFor(plane);
        double sw = Math.Max(1, _vol.Dim(plan.HAxis));
        double sh = Math.Max(1, _vol.Dim(plan.VAxis));
        double col = plan.HReversed ? sw - 1 - _focus[plan.HAxis] : _focus[plan.HAxis];
        double row = plan.VReversed ? sh - 1 - _focus[plan.VAxis] : _focus[plan.VAxis];

        var x = dst.X + (col + 0.5) / sw * dst.Width;
        var y = dst.Y + (row + 0.5) / sh * dst.Height;
        x = Math.Max(dst.Left, Math.Min(dst.Right, x));
        y = Math.Max(dst.Top, Math.Min(dst.Bottom, y));

        var pen = _settings.CrosshairPen;
        dc.DrawLine(pen, new Point(x, dst.Top), new Point(x, dst.Bottom));
        dc.DrawLine(pen, new Point(dst.Left, y), new Point(dst.Right, y));
    }

    private (SlicePlane plane, Rect dst)? PlaneAt(Point pt)
    {
        var frame = ComputeFrame(RenderSize);
        foreach (var (plane, ax, ay) in Layout)
        {
            var quad = frame.Quad(plane);
            if (!quad.Contains(pt)) continue;
            var img = Slice(plane).Image;
            return (plane, WpfPreviewRenderer.SliceDst(quad, img.Width, img.Height, ax, ay));
        }
        return null;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        // Alt + wheel → change volume (mirrors macOS option-scroll). On a not-yet-
        // loaded multi-volume file, the gesture instead triggers the lazy load.
        if (_vol.Volumes > 1 && (Keyboard.Modifiers & ModifierKeys.Alt) != 0)
        {
            if (_isExpanded) StepVolume(e.Delta > 0 ? 1 : -1);
            else if (CanExpandOnDemand) RequestExpansion();
            else { base.OnMouseWheel(e); return; }
            e.Handled = true;
            return;
        }

        var hit = PlaneAt(e.GetPosition(this));
        if (hit is not { } h) { base.OnMouseWheel(e); return; }

        var axis = _vol.AxesFor(h.plane).sliceAxis;
        var n = _vol.Dim(axis);
        var step = e.Delta / 120;
        if (step == 0) step = e.Delta > 0 ? 1 : -1;
        _focus[axis] = Math.Max(0, Math.Min(n - 1, _focus[axis] + step));
        _interacted = true;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        var pt = e.GetPosition(this);

        // Scrubber hit-test. The track is drawn for both Expanded and Loadable, so
        // clicking it either scrubs (expanded) or triggers the lazy load (loadable).
        if (_vol.Volumes > 1 && !_scrubberRect.IsEmpty && _scrubberRect.Contains(pt))
        {
            if (_isExpanded)
            {
                _scrubDrag = CaptureMouse();
                SeekVolume(pt.X);
            }
            else if (CanExpandOnDemand)
            {
                RequestExpansion();
            }
            e.Handled = true;
            return;
        }

        _navDrag = CaptureMouse();
        SetFocusFromPoint(pt);
        e.Handled = true;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        if (_hasWindow) BeginWindowLevel(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var pt = e.GetPosition(this);
        if (_scrubDrag && e.LeftButton == MouseButtonState.Pressed)
            SeekVolume(pt.X);
        else if (_wlDrag && e.RightButton == MouseButtonState.Pressed)
            WindowLevelTo(pt);
        else if (_navDrag && e.LeftButton == MouseButtonState.Pressed)
            SetFocusFromPoint(pt);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) => EndDrag();

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        EndDrag();
        e.Handled = true;
    }

    private void StepVolume(int step)
    {
        var next = Math.Max(0, Math.Min(_vol.Volumes - 1, _volumeIndex + step));
        SwitchVolume(next);
    }

    private void SeekVolume(double x)
    {
        var span = _scrubTrackX1 - _scrubTrackX0;
        if (span < 1) return;
        var clamped = Math.Max(_scrubTrackX0, Math.Min(_scrubTrackX1, x));
        var fraction = (clamped - _scrubTrackX0) / span;
        var target = (int)Math.Round(fraction * (_vol.Volumes - 1));
        target = Math.Max(0, Math.Min(_vol.Volumes - 1, target));
        SwitchVolume(target);
    }

    private void SwitchVolume(int next)
    {
        if (next == _volumeIndex) return;
        _volumeIndex = next;
        _interacted = true;
        if (_perVolumeWindow) RequestWindowForVolume(next);
        InvalidateVisual();
    }

    // Applies a window from the cache (cache-hit path) or starts a background
    // compute (cache-miss path). Cancels any in-flight compute for a prior volume.
    private void RequestWindowForVolume(int vol)
    {
        if (_windowCache.TryGetValue(vol, out var cached))
        {
            ApplyWindow(cached);
            return;
        }

        _windowCts?.Cancel();
        _windowCts = new CancellationTokenSource();
        var cts = _windowCts;
        var volume = _vol;
        var options = _options;

        Task.Run(() =>
        {
            if (cts.IsCancellationRequested) return;
            var w = volume.SharedWindow(options, vol);
            if (cts.IsCancellationRequested) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (cts.IsCancellationRequested) return;
                _windowCache[vol] = w; // cache for instant back-navigation
                if (_volumeIndex != vol) return; // user moved on; store only
                ApplyWindow(w);
                InvalidateVisual();
            });
        });
    }

    // Updates the live window bounds and bumps the revision so the slice
    // cache knows to re-extract with the new mapping.
    private void ApplyWindow(IntensityWindow.Bounds? b)
    {
        if (!_hasWindow || b is not { } bounds) return;
        _winLow = bounds.Low;
        _winHigh = bounds.High;
        _winRev++;
    }

    private void BeginWindowLevel(Point pt)
    {
        _wlDrag = CaptureMouse();
        _wlStart = pt;
        _wlStartLow = _winLow;
        _wlStartHigh = _winHigh;
    }

    private void EndDrag()
    {
        if (_navDrag || _wlDrag || _scrubDrag) ReleaseMouseCapture();
        _navDrag = false;
        _wlDrag = false;
        _scrubDrag = false;
    }

    // Right-drag: horizontal = window width (contrast), vertical = window
    // level (brightness; drag up = brighter). A full-extent drag changes the
    // value by roughly one reference span (the initial 2/98 width), so
    // sensitivity scales with the data.
    private void WindowLevelTo(Point pt)
    {
        var w = Math.Max(1.0, ActualWidth);
        var h = Math.Max(1.0, ActualHeight);

        var startLevel = (_wlStartLow + _wlStartHigh) / 2;
        var startWidth = _wlStartHigh - _wlStartLow;

        var width = startWidth + (pt.X - _wlStart.X) / w * _winRefSpan;
        width = Math.Max(_winRefSpan * 1e-3, width);
        var level = startLevel + (pt.Y - _wlStart.Y) / h * _winRefSpan;

        _winLow = level - width / 2;
        _winHigh = level + width / 2;
        _winRev++;
        InvalidateVisual();
    }

    // Click/drag sets the two in-plane axes of the hit plane; the other two
    // planes follow via their now-changed slice index (triplanar linking).
    private void SetFocusFromPoint(Point pt)
    {
        if (PlaneAt(pt) is not { } h || h.dst.Width < 1 || h.dst.Height < 1) return;
        _interacted = true;

        var plan = _vol.PlanFor(h.plane);
        var u = (pt.X - h.dst.X) / h.dst.Width;
        var v = (pt.Y - h.dst.Y) / h.dst.Height;

        var sw = _vol.Dim(plan.HAxis);
        var sh = _vol.Dim(plan.VAxis);
        var col = Math.Max(0, Math.Min(sw - 1, (int)(u * sw)));
        var row = Math.Max(0, Math.Min(sh - 1, (int)(v * sh)));

        // Invert the extraction reversal so the clicked pixel maps back to its
        // storage voxel (kept exactly in sync with SliceConfig.Coordinate).
        _focus[plan.HAxis] = plan.HReversed ? sw - 1 - col : col;
        _focus[plan.VAxis] = plan.VReversed ? sh - 1 - row : row;
        InvalidateVisual();
    }
}
