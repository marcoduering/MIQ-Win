using System.IO;
using System.Threading;

namespace MIQ.Parsing;

/// <summary>
/// Split ("detached") formats: a header file whose voxels live in a second file
/// next to it — Analyze 7.5 / NIfTI-1 pair (<c>.hdr</c> + <c>.img</c>), MetaImage
/// (<c>.mhd</c> + <c>.raw</c>/<c>.zraw</c>) and NRRD (<c>.nhdr</c> + <c>.raw</c>/<c>.raw.gz</c>).
/// Windows-only: macOS Quick Look grants access to the selected file only.
///
/// Resolves the data file a header names, and the reverse — the header that owns
/// a data file the user pressed Space on.
/// </summary>
public static class DetachedData
{
    /// Data-file suffixes that are previewed only when a sibling header claims them.
    /// They are generic (camera RAW, disk images), so never claimed on name alone.
    private static readonly string[] DataSuffixes = [".img.gz", ".img", ".raw.gz", ".raw", ".zraw"];

    /// Header extensions tried, in order, for a data file with the same base name.
    private static readonly string[] HeaderExtensions = [".hdr", ".mhd", ".nhdr"];

    public static bool IsDataFileName(string path) =>
        DataSuffixes.Any(s => path.EndsWith(s, StringComparison.OrdinalIgnoreCase));

    /// Resolves <paramref name="name"/> (as written in the header) against the
    /// header's folder. Only plain relative paths that stay inside that folder are
    /// accepted: an absolute or UNC path in a downloaded header would make a
    /// preview read arbitrary files or contact a remote host.
    public static string Resolve(string headerPath, string name)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(headerPath)) ?? "";
        if (name.Length == 0 || Path.IsPathRooted(name) || name.IndexOf(':') >= 0)
            throw new MiqException($"Data file '{name}' must be a relative path next to the header.");

        var full = Path.GetFullPath(Path.Combine(dir, name));
        var root = dir.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            ? dir
            : dir + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new MiqException($"Data file '{name}' lies outside the header's folder.");
        if (!File.Exists(full))
            throw new MiqException($"Data file '{name}' not found next to {Path.GetFileName(headerPath)}.");
        return full;
    }

    /// A loader for the parsers: reads the data file a header names, as stored
    /// (no decompression — the header's encoding decides that).
    public static Func<string, byte[]> Loader(string headerPath, CancellationToken ct) =>
        name => ReadFile(Resolve(headerPath, name), ct);

    internal static byte[] ReadFile(string path, CancellationToken ct)
    {
        var length = new FileInfo(path).Length;
        if (length > MiqParser.MaxArrayBytes)
            throw new MiqException("Data file exceeds the 2 GB preview limit for split formats.");
        return MiqParser.ReadAllBytes(path, (int)length, ct);
    }

    /// The Analyze/NIfTI-pair data file for <paramref name="hdrPath"/>: the same
    /// base name with <c>.img</c>, else <c>.img.gz</c>.
    public static string AnalyzeImagePath(string hdrPath)
    {
        var stem = Path.Combine(Path.GetDirectoryName(hdrPath) ?? "", Path.GetFileNameWithoutExtension(hdrPath));
        foreach (var ext in new[] { ".img", ".IMG", ".img.gz" })
            if (File.Exists(stem + ext)) return stem + ext;
        throw new MiqException($"Image file '{Path.GetFileName(stem)}.img' not found next to the header.");
    }

    /// Whether <paramref name="path"/> starts like an Analyze 7.5 / NIfTI-1 header
    /// (sizeof_hdr = 348 in either byte order). Separates it from the other
    /// <c>.hdr</c> formats (Radiance HDR images, ENVI headers).
    public static bool LooksLikeAnalyzeHeader(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var b = new byte[4];
            if (fs.Read(b, 0, 4) != 4) return false;
            return MiqBinaryReader.Int32(b, 0, littleEndian: true) == 348
                   || MiqBinaryReader.Int32(b, 0, littleEndian: false) == 348;
        }
        catch (Exception) { return false; }
    }

    /// The header that owns <paramref name="dataPath"/>: a sibling with the same
    /// base name whose contents point at this file. Null when there is none, so the
    /// data file is left to other viewers. Reads at most a few KB per candidate.
    public static string? HeaderFor(string dataPath)
    {
        if (!IsDataFileName(dataPath)) return null;
        var fileName = Path.GetFileName(dataPath);
        var stem = fileName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(fileName))
            : Path.GetFileNameWithoutExtension(fileName);
        var dir = Path.GetDirectoryName(dataPath) ?? "";
        var isImg = fileName.IndexOf(".img", StringComparison.OrdinalIgnoreCase) >= 0;

        foreach (var ext in HeaderExtensions)
        {
            var candidate = Path.Combine(dir, stem + ext);
            if (!File.Exists(candidate)) continue;
            try
            {
                if (ext == ".hdr")
                {
                    if (isImg && LooksLikeAnalyzeHeader(candidate)) return candidate;
                    continue;
                }
                var name = ext == ".mhd"
                    ? MetaImageParser.DataFileName(ReadHead(candidate))
                    : NrrdParser.DataFileName(ReadHead(candidate));
                if (name is not null && SamePath(Resolve(candidate, name), dataPath))
                    return candidate;
            }
            catch (Exception) { /* malformed or unrelated header: not ours */ }
        }
        return null;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// The first <see cref="MetaImageParser.HeaderProbeBytes"/> of a text header.
    private static byte[] ReadHead(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buf = new byte[Math.Min(fs.Length, MetaImageParser.HeaderProbeBytes)];
        var total = 0;
        int n;
        while (total < buf.Length && (n = fs.Read(buf, total, buf.Length - total)) > 0) total += n;
        return total == buf.Length ? buf : buf.Take(total).ToArray();
    }

    /// Multi-file data (MetaImage `LIST` or a `%d` pattern, NRRD `LIST` or a
    /// format string) is out of scope.
    internal static bool IsMultiFile(string name) =>
        name.StartsWith("LIST", StringComparison.OrdinalIgnoreCase) || name.IndexOf('%') >= 0;
}
