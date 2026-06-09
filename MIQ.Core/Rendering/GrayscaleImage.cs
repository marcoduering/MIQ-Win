namespace MIQ.Rendering;

/// Row-major 8-bit grayscale buffer. Port of MIQCore's GrayscaleImage +
/// ResampleTargetSize + nearestNeighborResample (channels = 1).
public sealed class GrayscaleImage(int width, int height, byte[] pixels)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte[] Pixels { get; } = pixels;

    /// FOV-aware nearest-neighbour resample so physical aspect ratio is correct.
    public GrayscaleImage ResampledForPixelSpacing(
        float pixelSpacingX, float pixelSpacingY, float maxPhysicalExtent, int maxDimension)
    {
        if (Pixels.Length != Width * Height) return this;
        var target = ResampleTargetSize(Width, Height, pixelSpacingX, pixelSpacingY, maxPhysicalExtent, maxDimension);
        if (target is not var (tw, th) || (tw == Width && th == Height)) return this;

        var outp = new byte[tw * th];
        for (var ny = 0; ny < th; ny++)
        {
            var syIdx = Math.Min(Height - 1, (int)((float)ny * Height / th));
            for (var nx = 0; nx < tw; nx++)
            {
                var sxIdx = Math.Min(Width - 1, (int)((float)nx * Width / tw));
                outp[ny * tw + nx] = Pixels[syIdx * Width + sxIdx];
            }
        }
        return new GrayscaleImage(tw, th, outp);
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
