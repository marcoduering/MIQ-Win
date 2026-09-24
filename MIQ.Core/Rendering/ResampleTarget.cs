namespace MIQ.Rendering;

/// Target size for the FOV-aware nearest-neighbour resample shared by
/// <see cref="GrayscaleImage"/> and <see cref="RgbImage"/>. Port of MIQCore's
/// <c>ResampleTargetSize</c>.
internal static class ResampleTarget
{
    /// Spacings arrive sanitized (<c>MiqVolume.SanitizedSpacing</c>), so no floor
    /// here. Deliberately unlike MIQCore: this extent and
    /// <paramref name="maxPhysicalExtent"/> (from <c>PrepareSlice</c>) must treat
    /// spacing identically, or tiny spacings blow small planes up or shrink every
    /// plane (see SpacingSanitizeTests). Non-finite or non-positive arithmetic
    /// falls back to the unscaled size.
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
