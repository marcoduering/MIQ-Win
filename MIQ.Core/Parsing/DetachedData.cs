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
    /// Data-file suffixes, each with the header extensions that can claim it. The
    /// suffixes are generic (camera RAW, disk images), so a file is claimed only
    /// when such a sibling header points at it, never on its name alone.
    private static readonly (string Suffix, string[] Headers)[] DataSuffixes =
    [
        (".img.gz", [".hdr"]),
        (".img", [".hdr"]),
        (".raw.gz", [".nhdr"]),
        (".raw", [".mhd", ".nhdr"]),
        (".zraw", [".mhd"]),
    ];

    public static bool IsDataFileName(string path) => DataSuffix(path) is not null;

    private static (string Suffix, string[] Headers)? DataSuffix(string path)
    {
        foreach (var entry in DataSuffixes)
            if (path.EndsWith(entry.Suffix, StringComparison.OrdinalIgnoreCase))
                return entry;
        return null;
    }

    /// Resolves <paramref name="name"/> (as written in the header) against the
    /// header's folder. Only relative paths that stay inside that folder are
    /// accepted: an absolute or UNC path in a downloaded header would make a
    /// preview read arbitrary files or contact a remote host (leaking NTLM
    /// credentials). Junctions and symlinks are not resolved.
    public static string Resolve(string headerPath, string name)
    {
        var full = ResolvePath(headerPath, name);
        if (!File.Exists(full))
            throw new MiqException($"Data file '{name}' not found next to {Path.GetFileName(headerPath)}.");
        return full;
    }

    /// <see cref="Resolve"/> without the existence check, for <see cref="HeaderFor"/>,
    /// whose data file is the one the user selected (one round trip fewer on a share).
    private static string ResolvePath(string headerPath, string name)
    {
        if (name.Length == 0 || name.IndexOf(':') >= 0 || IsRooted(name))
            throw new MiqException($"Data file '{name}' must be a relative path next to the header.");

        string dir, full;
        try
        {
            dir = Path.GetDirectoryName(Path.GetFullPath(headerPath)) ?? "";
            full = Path.GetFullPath(Path.Combine(dir, name));
        }
        // .NET Framework rejects characters such as '|' or '"' with ArgumentException
        // and long paths with PathTooLongException; .NET 8 accepts both.
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new MiqException($"Data file '{name}' is not a valid file name.");
        }

        var root = dir.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            ? dir
            : dir + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new MiqException($"Data file '{name}' lies outside the header's folder.");
        return full;
    }

    /// Rooted in either separator style: `\x`, `/x` and `\\server` all are.
    private static bool IsRooted(string name) => name[0] is '\\' or '/';

    /// A loader for the parsers: reads the data file a header names, as stored
    /// (no decompression — the header's encoding decides that).
    public static Func<string, byte[]> Loader(string headerPath, CancellationToken ct) =>
        name => ReadFile(Resolve(headerPath, name), ct);

    /// For <see cref="MiqParser.ParsePartial(string, CancellationToken)"/>: resolves a
    /// header's data file to its path and length, without reading it.
    internal static Func<string, (string Path, long Length)> Locator(string headerPath) =>
        name =>
        {
            var path = Resolve(headerPath, name);
            return (path, new FileInfo(path).Length);
        };

    internal static byte[] ReadFile(string path, CancellationToken ct)
    {
        var length = new FileInfo(path).Length;
        if (length > MiqParser.MaxArrayBytes)
            throw new MiqException("Data file exceeds the 2 GB preview limit for split formats.");
        return MiqParser.ReadAllBytes(path, (int)length, ct);
    }

    /// The Analyze/NIfTI-pair data file for <paramref name="hdrPath"/>: the same
    /// base name with <c>.img</c>, else <c>.img.gz</c>. (File.Exists ignores case.)
    public static string AnalyzeImagePath(string hdrPath)
    {
        var stem = Path.Combine(Path.GetDirectoryName(hdrPath) ?? "", Path.GetFileNameWithoutExtension(hdrPath));
        foreach (var ext in new[] { ".img", ".img.gz" })
            if (File.Exists(stem + ext)) return stem + ext;
        throw new MiqException($"Image file '{Path.GetFileName(stem)}.img' not found next to the header.");
    }

    /// Whether <paramref name="path"/> is an Analyze 7.5 / NIfTI-1 pair header the
    /// parser accepts: sizeof_hdr = 348 plus valid dimensions and datatype. That
    /// separates it from the other <c>.hdr</c> formats (Radiance HDR images, ENVI
    /// headers) and from a file whose first four bytes just happen to read 348.
    public static bool LooksLikeAnalyzeHeader(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var hdr = new byte[348];
            var total = 0;
            int n;
            while (total < hdr.Length && (n = fs.Read(hdr, total, hdr.Length - total)) > 0) total += n;
            if (total < hdr.Length) return false;
            NiftiParser.ParsePairHeader(hdr, imgCompressed: false);
            return true;
        }
        catch (Exception) { return false; }
    }

    /// The header that owns <paramref name="dataPath"/>: a sibling with the same
    /// base name whose contents point at this file. Null when there is none, so the
    /// data file is left to other viewers. One existence check per candidate header
    /// (one or two), plus a read of at most a few KB when a candidate exists.
    public static string? HeaderFor(string dataPath)
    {
        if (DataSuffix(dataPath) is not { } entry) return null;
        try
        {
            var fileName = Path.GetFileName(dataPath);
            var stem = fileName.Substring(0, fileName.Length - entry.Suffix.Length);
            var dir = Path.GetDirectoryName(dataPath) ?? "";

            foreach (var ext in entry.Headers)
            {
                var candidate = Path.Combine(dir, stem + ext);
                if (!File.Exists(candidate)) continue;
                try
                {
                    if (ext == ".hdr")
                    {
                        if (LooksLikeAnalyzeHeader(candidate)
                            && SamePath(AnalyzeImagePath(candidate), dataPath))
                            return candidate;
                        continue;
                    }
                    var name = ext == ".mhd"
                        ? MetaImageParser.DataFileName(ReadHead(candidate))
                        : NrrdParser.DataFileName(ReadHead(candidate));
                    if (name is not null && SamePath(ResolvePath(candidate, name), dataPath))
                        return candidate;
                }
                catch (Exception) { /* malformed or unrelated header: not ours */ }
            }
        }
        catch (Exception) { /* a path the BCL rejects: not ours */ }
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

/// Where a detached header's voxels are, for the volume-0-first load: the image
/// header (identical to a full parse's), the data file, and the byte offset of the
/// payload in it (also <see cref="MiqHeader.VoxOffset"/>).
internal sealed record DetachedLayout(MiqHeader Header, string DataPath, int Offset);
