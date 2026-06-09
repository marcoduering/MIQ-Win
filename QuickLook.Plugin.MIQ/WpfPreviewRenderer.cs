using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MIQ.Parsing;
using MIQ.Rendering;

namespace QuickLook.Plugin.MIQ;

/// <summary>
/// Pure-WPF compositing primitives for the 2×2 layout (no System.Drawing — it
/// would conflict with the System.Drawing.Primitives the QuickLook host ships).
/// Styling comes from <see cref="MiqSettings"/> so the static reference path
/// and the interactive control stay visually identical.
/// </summary>
internal static class WpfPreviewRenderer
{
    private static readonly Typeface Regular = new("Segoe UI");
    private static readonly Typeface Bold =
        new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private static readonly Typeface Italic =
        new(new FontFamily("Segoe UI"), FontStyles.Italic, FontWeights.Normal, FontStretches.Normal);

    // Footer disclaimer, mirroring macOS MIQ. Hidden via MiqSettings.ShowDisclaimer.
    private const string DisclaimerText =
        "Not for clinical or diagnostic use.\nNo warranty expressed or implied.";

    private const double Dpi = 96.0;

    // Track is neutral gray — not user-configurable.
    private static readonly Brush ScrubTrackBrush =
        new SolidColorBrush(Color.FromArgb(55, 160, 160, 160));

    static WpfPreviewRenderer()
    {
        ((SolidColorBrush)ScrubTrackBrush).Freeze();
    }

    /// Letterbox+aligned destination of a w×h image inside <paramref name="quad"/>.
    /// Shared by the compositor and the interactive control so the crosshair /
    /// hit-testing geometry exactly matches what is drawn.
    internal static Rect SliceDst(Rect quad, int pixelWidth, int pixelHeight, double ax, double ay)
    {
        var scale = Math.Min(quad.Width / pixelWidth, quad.Height / pixelHeight);
        var dw = Math.Max(1, pixelWidth * scale);
        var dh = Math.Max(1, pixelHeight * scale);
        return new Rect(
            quad.X + (quad.Width - dw) * ax,
            quad.Y + (quad.Height - dh) * ay,
            dw, dh);
    }

    internal static Rect DrawSlice(
        DrawingContext dc, CenterSlice slice, Rect quad, double ax, double ay, MiqSettings s)
    {
        if (quad.Width <= 0 || quad.Height <= 0) return Rect.Empty;

        var src = ToBitmap(slice.Image);
        var dst = SliceDst(quad, src.PixelWidth, src.PixelHeight, ax, ay);
        dc.DrawImage(src, dst);
        DrawAxisLabels(dc, slice.Labels, dst, s);
        return dst;
    }

    private static void DrawAxisLabels(
        DrawingContext dc, SliceOrientationLabels labels, Rect r, MiqSettings s)
    {
        var brush = labels.IsUnknown ? s.AxisUnknownBrush : s.AxisBrush;
        var fs = s.AxisLabelFontSize;
        const double pad = 3;

        var top = Text(labels.Top, brush, fs);
        dc.DrawText(top, new Point(r.X + (r.Width - top.Width) / 2, r.Y + pad));

        var bottom = Text(labels.Bottom, brush, fs);
        dc.DrawText(bottom, new Point(r.X + (r.Width - bottom.Width) / 2,
            r.Bottom - bottom.Height - pad));

        var leading = Text(labels.Leading, brush, fs);
        dc.DrawText(leading, new Point(r.X + pad, r.Y + (r.Height - leading.Height) / 2));

        var trailing = Text(labels.Trailing, brush, fs);
        dc.DrawText(trailing, new Point(r.Right - trailing.Width - pad,
            r.Y + (r.Height - trailing.Height) / 2));
    }

    // Inline scrubber constants (track sits to the right of the Volumes value text).
    private const double ScrubInlineTrackH = 4;
    private const double ScrubInlineGapX = 14; // gap between the value text and the track start

    /// State of the Volumes row's scrubber. <see cref="Loadable"/> draws an
    /// interactive track but the full volume isn't loaded yet — the first scrub
    /// gesture triggers the background load (then the row shows <see cref="Loading"/>
    /// until it expands). <see cref="Blocked"/> is the permanent volume-0-only view.
    internal enum ScrubMode { Expanded, Loadable, Loading, Blocked }

    /// <summary>
    /// Draws metadata entries. When <paramref name="scrubVol"/> ≥ 0 and the entry
    /// labelled "Volumes" is present, that row shows the current/total volume text
    /// plus an inline scrubber track. Returns the track hit-rect and x bounds for
    /// pointer interaction; all three are zero/empty when no scrubber is drawn.
    /// </summary>
    internal static (Rect hitRect, double trackX0, double trackX1) DrawMetadata(
        DrawingContext dc, IReadOnlyList<MetadataEntry> entries, Rect r, MiqSettings s,
        int scrubVol = -1, int scrubTotal = 1, ScrubMode scrubMode = ScrubMode.Expanded)
    {
        // Both states present an interactive track; they differ only in whether the
        // full volume is already loaded (Expanded) or loads on first scrub (Loadable).
        var showTrack = scrubMode is ScrubMode.Expanded or ScrubMode.Loadable;
        dc.DrawRectangle(s.BackgroundBrush, null, r);

        const double padX = 18;
        const double padTop = 18;
        const double gap = 12; // between the label and value columns
        var fs = s.MetadataFontSize;
        var textH = Text("Mg", s.MetadataLabelBrush, fs).Height;
        var lineH = textH + 6;

        // Footer disclaimer pinned to the bottom; rows must not overlap it.
        const double disPad = 18;
        FormattedText? disclaimer = null;
        var rowsBottom = r.Bottom;
        if (s.ShowDisclaimer)
        {
            var d = Text(DisclaimerText, s.MetadataLabelBrush, Math.Max(8, fs * 0.85), Italic);
            d.MaxTextWidth = Math.Max(20, r.Width - 2 * disPad);
            disclaimer = d;
            rowsBottom = r.Bottom - d.Height - disPad;
        }

        // Build once, sizing the label column to the widest actual label.
        var labels = new FormattedText[entries.Count];
        var values = new FormattedText[entries.Count];
        double labelW = 0;
        for (var i = 0; i < entries.Count; i++)
        {
            labels[i] = Text(entries[i].Label, s.MetadataLabelBrush, fs, Regular);
            bool isVolRow = scrubVol >= 0 && entries[i].Label == "Volumes";
            if (isVolRow)
            {
                if (showTrack)
                {
                    // Expanded or Loadable: bright "N / M" beside the live track.
                    values[i] = Text($"{scrubVol + 1} / {scrubTotal}",
                        s.MetadataValueBrush, fs, Regular);
                }
                else
                {
                    // Loading the rest, or permanently blocked (too large) — vol 0 only.
                    var valStr = scrubMode == ScrubMode.Blocked
                        ? $"1 / {scrubTotal}  ·  first volume only (too large for 4-D)"
                        : $"{scrubVol + 1} / {scrubTotal}  loading…";
                    values[i] = Text(valStr, s.MetadataLabelBrush, fs * 0.9, Regular);
                }
            }
            else
            {
                values[i] = Text(entries[i].Value, s.MetadataValueBrush, fs, Regular);
            }
            labelW = Math.Max(labelW, labels[i].Width);
        }
        // Defensive: keep the value column on-panel if labels are huge.
        var valueX = Math.Min(r.X + padX + labelW + gap, r.Right - padX - 20);

        // Reserve a stable slot for the Volumes value ("N / M") so the inline
        // scrubber track keeps a fixed x. It must not shift when the index gains
        // a digit, nor when the "loading…" suffix drops off after expansion.
        var volValueSlotW = scrubVol >= 0
            ? Text($"{scrubTotal} / {scrubTotal}", s.MetadataValueBrush, fs, Regular).Width
            : 0;

        var hitRect = Rect.Empty;
        double trackX0 = 0, trackX1 = 0;

        var y = r.Y + padTop;
        for (var i = 0; i < entries.Count; i++)
        {
            if (y + lineH > rowsBottom) break;
            dc.DrawText(labels[i], new Point(r.X + padX, y));
            dc.DrawText(values[i], new Point(valueX, y));

            bool isVolRow = scrubVol >= 0 && entries[i].Label == "Volumes";
            if (isVolRow)
            {
                // Track runs inline, right of the value's reserved slot, centred on
                // the line. Drawn for Expanded/Loadable — not while loading — and a
                // fixed tx0 means it never shifts when the loading suffix drops off.
                const double knobR = 5;
                var trackY = y + (textH - ScrubInlineTrackH) / 2.0;
                var tx0 = valueX + volValueSlotW + ScrubInlineGapX;
                var tx1 = r.Right - padX - knobR;
                if (showTrack && tx1 > tx0 + 20)
                {
                    var fraction = scrubTotal > 1 ? (double)scrubVol / (scrubTotal - 1) : 0.0;
                    var knobX = tx0 + fraction * (tx1 - tx0);

                    dc.DrawRectangle(ScrubTrackBrush, null,
                        new Rect(tx0, trackY, tx1 - tx0, ScrubInlineTrackH));
                    if (knobX > tx0)
                        dc.DrawRectangle(s.ScrubberFillBrush, null,
                            new Rect(tx0, trackY, knobX - tx0, ScrubInlineTrackH));
                    dc.DrawEllipse(s.ScrubberKnobBrush, null,
                        new Point(knobX, trackY + ScrubInlineTrackH / 2.0), knobR, knobR);

                    // Hit area hugs the track (plus knob radius), not the whole row.
                    hitRect = new Rect(tx0 - knobR, y, tx1 - tx0 + 2 * knobR, lineH);
                    trackX0 = tx0;
                    trackX1 = tx1;
                }
            }
            y += lineH;
        }

        if (disclaimer is not null)
            dc.DrawText(disclaimer, new Point(r.X + disPad, r.Bottom - disclaimer.Height - disPad));

        return (hitRect, trackX0, trackX1);
    }

    private static FormattedText Text(string s, Brush brush, double size, Typeface? face = null) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            face ?? Bold, size, brush, 1.0);

    private static BitmapSource ToBitmap(SliceImage img)
    {
        BitmapSource bmp = img.Rgb is { } rgb
            ? BitmapSource.Create(
                rgb.Width, rgb.Height, Dpi, Dpi,
                PixelFormats.Rgb24, null, rgb.Pixels, rgb.Width * 3)
            : BitmapSource.Create(
                img.Grayscale!.Width, img.Grayscale.Height, Dpi, Dpi,
                PixelFormats.Gray8, null, img.Grayscale.Pixels, img.Grayscale.Width);
        bmp.Freeze();
        return bmp;
    }
}
