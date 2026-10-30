using System.IO;

namespace MIQ.Parsing;

/// <summary>
/// Supported file formats, detected by path suffix (case-insensitive).
/// Compound suffixes like ".nii.gz" are checked before single extensions.
/// Mirrors MIQCore's <c>MIQFileKind</c>.
/// </summary>
public enum MiqFileKind
{
    Nii,
    NiiGz,
    Mgh,
    Mgz,
    Mif,
    MifGz,
    Nrrd,
    Mha,
    // Split formats: a header whose voxels live in a sibling file (DetachedData).
    /// Analyze 7.5 or NIfTI-1 pair header (<c>.hdr</c> + <c>.img</c>).
    AnalyzeHdr,
    /// Detached MetaImage header (<c>.mhd</c> + <c>.raw</c>/<c>.zraw</c>).
    Mhd,
    /// Detached NRRD header (<c>.nhdr</c> + <c>.raw</c>/<c>.raw.gz</c>).
    Nhdr,
}

public static class MiqFileKindExtensions
{
    public static MiqFileKind? FromPath(string path)
    {
        var p = path.ToLowerInvariant();

        if (p.EndsWith(".nii.gz", StringComparison.Ordinal)) return MiqFileKind.NiiGz;
        if (p.EndsWith(".mgh.gz", StringComparison.Ordinal)) return MiqFileKind.Mgz;
        if (p.EndsWith(".mif.gz", StringComparison.Ordinal)) return MiqFileKind.MifGz;

        var ext = Path.GetExtension(p).TrimStart('.');
        return ext switch
        {
            "nii" => MiqFileKind.Nii,
            "mgh" => MiqFileKind.Mgh,
            "mgz" => MiqFileKind.Mgz,
            "mif" => MiqFileKind.Mif,
            "nrrd" => MiqFileKind.Nrrd,
            "mha" => MiqFileKind.Mha,
            "hdr" => MiqFileKind.AnalyzeHdr,
            "mhd" => MiqFileKind.Mhd,
            "nhdr" => MiqFileKind.Nhdr,
            _ => null,
        };
    }

    public static bool IsCompressed(this MiqFileKind kind) => kind switch
    {
        MiqFileKind.NiiGz or MiqFileKind.Mgz or MiqFileKind.MifGz => true,
        _ => false,
    };

    public static string DisplayName(this MiqFileKind kind) => kind switch
    {
        MiqFileKind.Nii => "NIfTI-1",
        MiqFileKind.NiiGz => "Compressed NIfTI-1",
        MiqFileKind.Mgh => "MGH",
        MiqFileKind.Mgz => "Compressed MGH",
        MiqFileKind.Mif => "MRtrix MIF",
        MiqFileKind.MifGz => "Compressed MRtrix MIF",
        MiqFileKind.Nrrd => "NRRD",
        MiqFileKind.Mha or MiqFileKind.Mhd => "MetaImage",
        MiqFileKind.AnalyzeHdr => "Analyze 7.5",
        MiqFileKind.Nhdr => "NRRD",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
