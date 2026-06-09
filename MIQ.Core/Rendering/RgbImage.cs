namespace MIQ.Rendering;

/// Row-major interleaved RGB buffer (3 bytes per pixel, R,G,B; no alpha — the
/// preview is opaque). Port of MIQCore's RGBImage + the channels=3 path of
/// nearestNeighborResample.
public sealed class RgbImage(int width, int height, byte[] pixels)
{
    private const int Channels = 3;

    public int Width { get; } = width;
    public int Height { get; } = height;
    /// Interleaved RGB bytes — length == Width * Height * 3.
    public byte[] Pixels { get; } = pixels;

    /// FOV-aware nearest-neighbour resample so physical aspect ratio is correct.
    /// Identical geometry to <see cref="GrayscaleImage.ResampledForPixelSpacing"/>
    /// but copies three channels per pixel.
    public RgbImage ResampledForPixelSpacing(
        float pixelSpacingX, float pixelSpacingY, float maxPhysicalExtent, int maxDimension)
    {
        if (Pixels.Length != Width * Height * Channels) return this;
        var target = ResampleTargetSize(Width, Height, pixelSpacingX, pixelSpacingY, maxPhysicalExtent, maxDimension);
        if (target is not var (tw, th) || (tw == Width && th == Height)) return this;

        var outp = new byte[tw * th * Channels];
        for (var ny = 0; ny < th; ny++)
        {
            var syIdx = Math.Min(Height - 1, (int)((float)ny * Height / th));
            for (var nx = 0; nx < tw; nx++)
            {
                var sxIdx = Math.Min(Width - 1, (int)((float)nx * Width / tw));
                var src = (syIdx * Width + sxIdx) * Channels;
                var dst = (ny * tw + nx) * Channels;
                outp[dst] = Pixels[src];
                outp[dst + 1] = Pixels[src + 1];
                outp[dst + 2] = Pixels[src + 2];
            }
        }
        return new RgbImage(tw, th, outp);
    }

    private static (int width, int height)? ResampleTargetSize(
        int width, int height, float pixelSpacingX, float pixelSpacingY,
        float maxPhysicalExtent, int maxDimension)
    {
        if (width <= 0 || height <= 0) return null;
        var sx = Math.Max(1e-6f, pixelSpacingX);
        var sy = Math.Max(1e-6f, pixelSpacingY);
        var physicalWidth = width * sx;
        var physicalHeight = height * sy;
        var referencePhysical = Math.Max(maxPhysicalExtent, Math.Max(Math.Max(physicalWidth, physicalHeight), 1e-6f));
        var referencePixels = Math.Max(1, maxDimension);
        var tw = Math.Max(1, MiqCompat.RoundToInt(physicalWidth / referencePhysical * referencePixels));
        var th = Math.Max(1, MiqCompat.RoundToInt(physicalHeight / referencePhysical * referencePixels));
        return (tw, th);
    }
}
