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
        var settings = MiqSettings.Load();
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
        Task.Run(() =>
        {
            try
            {
                // Re-read preferences every preview (a few hundred bytes —
                // negligible next to parsing). Edits to MIQ.settings.ini apply
                // on the next Space, no restart.
                var settings = MiqSettings.Load();
                var options = settings.Options;
                var kind = MiqFileKindExtensions.FromPath(path);

                // Phase 1: fast partial parse — for multi-volume .nii.gz this
                // decompresses only volume 0 via streaming GZipStream, which
                // is ~N× faster than decompressing all N volumes up front.
                // For 3-D files or non-compressed formats it falls through to a
                // full parse transparently (IsPartial remains false).
                var image = MiqParser.ParsePartial(path);
                if (cts.IsCancellationRequested) return;

                var fmt = image.Header.FormatLabel ?? kind?.DisplayName() ?? "Unknown";
                var volume = new MiqVolume(image, options.Orientation);
                var window = volume.SharedWindow(options);
                var initial = new Dictionary<SlicePlane, CenterSlice>
                {
                    [SlicePlane.Coronal] = volume.ExtractSlice(
                        SlicePlane.Coronal, volume.CenterIndex(SlicePlane.Coronal), window),
                    [SlicePlane.Sagittal] = volume.ExtractSlice(
                        SlicePlane.Sagittal, volume.CenterIndex(SlicePlane.Sagittal), window),
                    [SlicePlane.Axial] = volume.ExtractSlice(
                        SlicePlane.Axial, volume.CenterIndex(SlicePlane.Axial), window),
                };
                var orientation = image.Header.OrientationFrame?.Label;
                var metadata = settings.SelectMetadata(
                    new MiqMetadata(image.Header, fmt, orientation).AsDisplayLines());

                control.Dispatcher.BeginInvoke(() =>
                {
                    if (cts.IsCancellationRequested) return;

                    _volume = volume;

                    // Phase 2 is lazy: a multi-volume file that loaded only volume 0
                    // gets an expansion callback, invoked by the control on the first
                    // scrub gesture. Until then no background work runs, so flicking
                    // through previews to glance at volume 0 stays instant. Blocked
                    // (too-large) files never expand.
                    MiqTriPlanarControl view = null!;
                    var onExpand = !volume.IsExpanded && !image.ExpansionBlocked
                        ? () => StartExpansion(path, options, control, view, cts.Token)
                        : (Action?)null;
                    view = new MiqTriPlanarControl(
                        volume, window, initial, metadata, options, settings,
                        isExpanded: volume.IsExpanded,
                        expansionBlocked: image.ExpansionBlocked,
                        onExpandRequested: onExpand);
                    control.ShowContent(view);
                    context.IsBusy = false;
                });
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
                control.Dispatcher.BeginInvoke(() =>
                {
                    if (token.IsCancellationRequested) return;
                    _volume = fullVolume;
                    view.ExpandVolume(fullVolume);
                });
            }
            catch (Exception) { /* cancelled, or viewer cleaned up */ }
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
