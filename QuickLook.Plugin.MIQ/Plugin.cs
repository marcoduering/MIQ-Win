using System.IO;
using System.Threading;
using MIQ.Parsing;
using MIQ.Rendering;
using QuickLook.Common.Plugin;
using WpfSize = System.Windows.Size;

namespace QuickLook.Plugin.MIQ;

/// <summary>
/// QuickLook plugin for medical volume images. Reuses the parser + slice
/// extraction; composites via <see cref="WpfPreviewRenderer"/> (pure WPF, no
/// System.Drawing — avoids the host's GDI assembly conflict).
///
/// Compound extensions like ".nii.gz" are a non-issue: we match by path suffix
/// in <see cref="CanHandle"/>, independent of Windows file associations.
/// </summary>
public sealed class Plugin : IViewer
{
    private static readonly string[] Suffixes =
        [".nii", ".nii.gz", ".mgh", ".mgz", ".mgh.gz", ".mif", ".mif.gz", ".nrrd"];

    private MiqPreviewControl? _control;
    // Retain the parsed volume for the viewer's lifetime — it owns the (large)
    // decompressed voxel buffer that arbitrary-slice extraction reads from.
    private MiqVolume? _volume;
    // Cancelled in Cleanup() so background tasks don't touch a recycled viewer.
    private CancellationTokenSource? _cts;
    // Loaded once per preview in Prepare() and reused by View(), so the ini is
    // read (and its breadcrumb checked) once per Space, not twice.
    private MiqSettings? _settings;

    // Above QuickLook's generic ArchiveViewer, which otherwise grabs ".gz".
    public int Priority => 100;

    public void Init()
    {
        // Native libdeflate is ~15–50× faster than .NET Framework's built-in
        // gzip; degrade silently to the managed path if it can't be loaded.
        try
        {
            LibdeflateGzip.EnsureLoaded();
            MiqParser.GzipDecompressorOverride = LibdeflateGzip.Decompress;
            // Mid-file gzip segments (e.g. gzip-encoded NRRD payloads) don't go
            // through the path-based override — accelerate them too.
            MiqBinaryReader.GzipBufferDecompressorOverride = LibdeflateGzip.DecompressBuffer;
        }
        catch (Exception)
        {
            // Managed GZipStream fallback remains in effect.
        }
    }

    public bool CanHandle(string path)
    {
        if (Directory.Exists(path)) return false;
        foreach (var s in Suffixes)
            if (path.EndsWith(s, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    public void Prepare(string path, ContextObject context)
    {
        // Re-read every preview so ini edits apply on the next Space.
        var settings = MiqSettings.Load();
        _settings = settings;
        context.PreferredSize = new WpfSize(settings.PreviewWidth, settings.PreviewHeight);
        context.Theme = Themes.Dark;
    }

    public void View(string path, ContextObject context)
    {
        context.Title = Path.GetFileName(path);

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var cts = _cts;

        _control = new MiqPreviewControl();
        context.ViewerContent = _control;

        var control = _control;
        // Frozen WPF resources only, so safe to hand to the background task.
        // Load here only if the host skipped Prepare().
        var loaded = _settings;
        Task.Run(() =>
        {
            try
            {
                var settings = loaded ?? MiqSettings.Load();
                var options = settings.Options;
                var kind = MiqFileKindExtensions.FromPath(path);

                // Phase 1: volume-0-only load where it pays off (see ParsePartial);
                // otherwise a full parse (IsPartial = false). The token abandons the
                // read/decompress if the user navigates away mid-load.
                var image = MiqParser.ParsePartial(path, cts.Token);
                if (cts.IsCancellationRequested) return;

                var fmt = image.Header.FormatLabel ?? kind?.DisplayName() ?? "Unknown";
                var volume = new MiqVolume(image, options.Orientation);
                // One decode yields the segmentation LUT, the shared window (null
                // when a LUT is present), and the initial slices.
                var (lut, window, initial) = volume.CenterInteractiveState(options);

                // No LUT and no window on a scalar volume: volume 0 holds no finite
                // voxel (the window falls back to a whole-volume scan). Say so rather than render black squares (not an error).
                if (window is null && lut is null && !volume.IsRgb)
                {
                    control.Dispatcher.BeginInvoke(() =>
                    {
                        if (cts.IsCancellationRequested) return;
                        control.ShowMessage(
                            $"{Path.GetFileName(path)}\n\n"
                            + "No finite voxel values to display (the volume is entirely NaN or infinite).");
                        context.IsBusy = false;
                    });
                    return;
                }

                var orientation = image.Header.OrientationFrame?.Label;
                var metadata = settings.SelectMetadata(
                    new MiqMetadata(image.Header, fmt, orientation).AsDisplayLines());

                control.Dispatcher.BeginInvoke(() =>
                {
                    if (cts.IsCancellationRequested) return;

                    _volume = volume;

                    // Phase 2 is lazy: the full load runs only on the first scrub
                    // gesture, so flicking through previews does no background work.
                    // Blocked (too-large) files never expand.
                    MiqTriPlanarControl view = null!;
                    var onExpand = !volume.IsExpanded && !image.ExpansionBlocked
                        ? () => StartExpansion(path, options, control, view, cts.Token)
                        : (Action?)null;
                    view = new MiqTriPlanarControl(
                        volume, window, lut, initial, metadata, options, settings,
                        isExpanded: volume.IsExpanded,
                        expansionBlocked: image.ExpansionBlocked,
                        onExpandRequested: onExpand);
                    control.ShowContent(view);
                    context.IsBusy = false;
                });
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // Navigated away mid-load: nothing to show.
            }
            catch (Exception ex)
            {
                control.Dispatcher.BeginInvoke(() =>
                {
                    if (cts.IsCancellationRequested) return;
                    control.ShowMessage(
                        $"{Path.GetFileName(path)}\n\n{ex.Message}", error: true);
                    context.IsBusy = false;
                });
            }
        });
    }

    /// Loads the full multi-volume file in the background (triggered lazily by the
    /// first scrub gesture) and swaps it into the view. Below-normal priority plus
    /// the cancellation token keep file-switching responsive if the user navigates
    /// away mid-load.
    private void StartExpansion(string path, MiqRenderingOptions options,
        MiqPreviewControl control, MiqTriPlanarControl view, CancellationToken token)
    {
        Task.Run(() =>
        {
            var thread = Thread.CurrentThread;
            var prevPriority = thread.Priority;
            thread.Priority = ThreadPriority.BelowNormal;
            try
            {
                if (token.IsCancellationRequested) return;
                var fullImage = MiqParser.Parse(path, token);
                var fullVolume = new MiqVolume(fullImage, options.Orientation);
                // Rebuild the LUT for the new volume object. The random palette
                // depends on the label set; this matches the initial LUT only
                // because detection samples volume 0's center slices in both.
                var fullLut = fullVolume.BuildSegmentationLut(options);
                control.Dispatcher.BeginInvoke(() =>
                {
                    if (token.IsCancellationRequested) return;
                    _volume = fullVolume;
                    view.ExpandVolume(fullVolume, fullLut);
                });
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Navigated away mid-load: nothing to report.
            }
            catch (Exception ex)
            {
                // A real failure (out of memory, I/O error, file changed): tell the
                // view so it leaves "loading…" for the volume-0-only notice.
                var message = ex.Message;
                control.Dispatcher.BeginInvoke(() =>
                {
                    if (token.IsCancellationRequested) return;
                    view.ExpansionFailed(message);
                });
            }
            finally { thread.Priority = prevPriority; }
        });
    }

    public void Cleanup()
    {
        _cts?.Cancel();
        _control = null;
        _volume = null; // release the decompressed voxel buffer
    }
}
