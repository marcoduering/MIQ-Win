namespace MIQ.Rendering;

/// Target size for the FOV-aware nearest-neighbour resample shared by
/// <see cref="GrayscaleImage"/> and <see cref="RgbImage"/>. Port of MIQCore's
/// <c>ResampleTargetSize</c>.
internal static class ResampleTarget
{
    /// Spacings arrive sanitized (finite, positive — see
    /// <c>MiqVolume.SanitizedSpacing</c>), so there is no absolute floor here: a
    /// 1e-6 floor on the reference extent shrank every slice of a volume whose
    /// largest extent is under 1e-6 in its own units (256 voxels at 1e-9 rendered
    /// at ~66 px instead of 256). Non-finite or non-positive arithmetic — a caller
    /// passing an unsanitized spacing, or a huge finite spacing whose extent
    /// overflows — falls back to the unscaled size rather than guess an aspect
    /// ratio from arithmetic that has already lost it.
    public static (int width, int height)? Size(
        int width, int height, float pixelSpacingX, float pixelSpacingY,
        float maxPhysicalExtent, int maxDimension)
    {
        if (width <= 0 || height <= 0) return null;
        var physicalWidth = width * pixelSpacingX;
        var physicalHeight = height * pixelSpacingY;
        var referencePhysical = Math.Max(maxPhysicalExtent, Math.Max(physicalWidth, physicalHeight));
        var referencePixels = Math.Max(1, maxDimension);
        if (!(physicalWidth > 0) || !(physicalHeight > 0)
            || !MiqCompat.IsFinite(physicalWidth) || !MiqCompat.IsFinite(physicalHeight)
            || !MiqCompat.IsFinite(referencePhysical))
            return (Math.Min(width, referencePixels), Math.Min(height, referencePixels));
        var tw = Math.Max(1, MiqCompat.RoundToInt(physicalWidth / referencePhysical * referencePixels));
        var th = Math.Max(1, MiqCompat.RoundToInt(physicalHeight / referencePhysical * referencePixels));
        return (tw, th);
    }
}
