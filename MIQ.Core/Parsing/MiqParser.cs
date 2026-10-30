using System.IO;
using System.Threading;

namespace MIQ.Parsing;

/// <summary>
/// Entry point: detects format by path, loads and (if needed) gunzips the
/// bytes, then dispatches to a format-specific parser.
/// Mirrors MIQCore's <c>MIQParser</c>.
/// </summary>
public static class MiqParser
{
    private const long MaxFileBytes = 4L * 1024 * 1024 * 1024;

    /// Largest byte[] the CLR will allocate (Array.MaxLength for a 1-byte element).
    /// Data this large can't be held as a single Storage buffer, so an oversized
    /// 4-D file is loaded volume-0-only with <see cref="MiqImage.ExpansionBlocked"/>.
    /// Non-const + internal so it can be lowered to exercise the path on small files.
    internal static long MaxArrayBytes = 0x7FFFFFC7;

    /// Above this size, an uncompressed multi-volume NIfTI is loaded volume-0-first
    /// (then expanded in the background) so the preview appears without waiting for
    /// the whole file — a large win on slow/network storage. Smaller files load
    /// fully up front.
    /// Non-const + internal so it can be lowered to exercise the path on small files.
    internal static long PartialLoadThreshold = 150L * 1024 * 1024;

    /// Minimum volume count for the .nii.gz vol-0-first partial load to be worth it.
    /// The partial path must use managed gzip (libdeflate can't stop mid-member),
    /// which on net462 is 15–50× slower than libdeflate, so decompressing 1/N of
    /// the file only wins for large N. Small series (multi-echo, phase/magnitude)
    /// parse fully instead, with the scrubber live immediately.
    /// NB MIQ.Perf runs on net8's fast gzip, so it misreads this as a regression.
    /// Non-const + internal so tests can vary it.
    internal static int PartialGzipMinVolumes = 12;

    /// <summary>
    /// Optional faster gzip decompressor. Given the file path, returns the
    /// fully-decompressed bytes. The QuickLook plugin sets this to a native
    /// libdeflate implementation (.NET Framework's built-in gzip is far slower).
    /// When null, the built-in streaming path is used. Implementations should
    /// honour the token while reading the file — on a network share the read,
    /// not the decompress, is what outlives a nav-away.
    /// </summary>
    public static Func<string, CancellationToken, byte[]>? GzipDecompressorOverride;

    /// Parses a file fully. Pass a <paramref name="ct"/> for background loads
    /// (e.g. volume expansion) so a slow read/decompress abandons promptly when
    /// the user navigates away, freeing CPU/IO for the next preview.
    /// A data file of a split pair (<c>.img</c>, <c>.raw</c>, …) parses through the
    /// header that claims it (<see cref="DetachedData.HeaderFor"/>).
    public static MiqImage Parse(string filePath, CancellationToken ct = default)
    {
        var k = ResolveKind(ref filePath);
        var data = LoadAndDecompress(filePath, k, ct);
        var formatLabel = k.IsCompressed() ? k.DisplayName() : null;

        return k switch
        {
            MiqFileKind.Nii or MiqFileKind.NiiGz     => NiftiParser.Parse(data, k.IsCompressed()),
            MiqFileKind.Mgh or MiqFileKind.Mgz       => MghParser.Parse(data, formatLabel),
            MiqFileKind.Mif or MiqFileKind.MifGz     => MifParser.Parse(data, formatLabel),
            MiqFileKind.Nrrd                         => NrrdParser.Parse(data, formatLabel),
            MiqFileKind.Mha                          => MetaImageParser.Parse(data),
            MiqFileKind.Mhd                          => MetaImageParser.Parse(data, DetachedData.Loader(filePath, ct)),
            MiqFileKind.Nhdr                         => NrrdParser.Parse(data, null, DetachedData.Loader(filePath, ct)),
            MiqFileKind.AnalyzeHdr                   => ParseAnalyzePair(filePath, data, ct),
            _ => throw MiqException.UnsupportedFileFormat(),
        };
    }

    /// The file kind of <paramref name="filePath"/>; a data file of a split pair is
    /// replaced by the header that claims it.
    private static MiqFileKind ResolveKind(ref string filePath)
    {
        var kind = MiqFileKindExtensions.FromPath(filePath);
        if (kind is null && DetachedData.HeaderFor(filePath) is { } header)
        {
            filePath = header;
            kind = MiqFileKindExtensions.FromPath(header);
        }
        return kind ?? throw MiqException.UnsupportedFileFormat();
    }

    private static bool IsSplit(MiqFileKind kind) =>
        kind is MiqFileKind.AnalyzeHdr or MiqFileKind.Mhd or MiqFileKind.Nhdr;

    /// Volume-0-first load of a split pair whose data file is uncompressed and
    /// volume-major — the counterpart of <see cref="ParseNiftiFirstVolume"/>. Reads
    /// the header, then exactly payload offset + volume 0 from the data file. The
    /// header comes from the same code as a full parse, so the first view is
    /// identical. Anything else (3-D, small, compressed, interleaved, or a data file
    /// shorter than the header implies) falls back to a full <see cref="Parse"/>.
    private static MiqImage ParseDetachedFirstVolume(string headerPath, MiqFileKind kind, CancellationToken ct)
    {
        DetachedLayout? layout;
        try
        {
            var headerBytes = LoadAndDecompress(headerPath, kind, ct);
            layout = kind switch
            {
                MiqFileKind.Mhd => MetaImageParser.RawLayout(headerBytes, DetachedData.Locator(headerPath)),
                MiqFileKind.Nhdr => NrrdParser.RawLayout(headerBytes, DetachedData.Locator(headerPath)),
                _ => AnalyzeLayout(headerPath, headerBytes),
            };
        }
        catch (Exception e) when (e is not OperationCanceledException) { return Parse(headerPath, ct); }

        if (layout is null || layout.Header.Volumes <= 1)
            return Parse(headerPath, ct);

        var fileLength = new FileInfo(layout.DataPath).Length;
        if (fileLength <= PartialLoadThreshold && fileLength <= MaxArrayBytes)
            return Parse(headerPath, ct);

        var header = layout.Header;
        var budget = (long)layout.Offset
                     + (long)header.Width * header.Height * header.Depth * header.Datatype.BytesPerVoxel();
        if (budget > MaxArrayBytes)
            return Parse(headerPath, ct); // volume 0 itself doesn't fit — full path reports it

        var storage = new byte[budget];
        using (var fs = new FileStream(layout.DataPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            if (ReadFully(fs, storage, storage.Length, ct) < storage.Length)
                return Parse(headerPath, ct); // data file shorter than the header implies

        return new MiqImage
        {
            Header = header,
            Storage = storage,
            PayloadOffset = layout.Offset,
            IsPartial = true,
            // The full parse reads the data file into one byte[].
            ExpansionBlocked = fileLength > MaxArrayBytes,
        };
    }

    /// Analyze/NIfTI-pair layout: vox_offset into an uncompressed .img.
    private static DetachedLayout? AnalyzeLayout(string hdrPath, byte[] hdr)
    {
        var imgPath = DetachedData.AnalyzeImagePath(hdrPath);
        if (imgPath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)) return null;
        var header = NiftiParser.ParsePairHeader(hdr, imgCompressed: false);
        return new DetachedLayout(header, imgPath, header.VoxOffset);
    }

    private static MiqImage ParseAnalyzePair(string hdrPath, byte[] hdr, CancellationToken ct)
    {
        var imgPath = DetachedData.AnalyzeImagePath(hdrPath);
        var compressed = imgPath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase);
        var img = compressed
            ? LoadAndDecompress(imgPath, MiqFileKind.NiiGz, ct)
            : DetachedData.ReadFile(imgPath, ct);
        return NiftiParser.ParsePair(hdr, img, compressed);
    }

    /// Uncancellable <see cref="ParsePartial(string, CancellationToken)"/>. An
    /// overload rather than an optional parameter so the method group still
    /// converts to <c>Func&lt;string, MiqImage&gt;</c>.
    public static MiqImage ParsePartial(string filePath) => ParsePartial(filePath, CancellationToken.None);

    /// <summary>
    /// Fast path for multi-volume NIfTI: returns just volume 0 as a partial
    /// <see cref="MiqImage"/> (<see cref="MiqImage.IsPartial"/> = true) so the
    /// preview appears without reading the whole file. Applies to any multi-volume
    /// <c>.nii.gz</c> (decompresses only volume 0), and to uncompressed <c>.nii</c>
    /// above <see cref="PartialLoadThreshold"/> (or <see cref="MaxArrayBytes"/>, in
    /// which case it's permanent — <see cref="MiqImage.ExpansionBlocked"/>).
    /// Falls back to a full <see cref="Parse"/> for 3-D / small files, or when the
    /// data is shorter than the header implies; those return a complete image
    /// (IsPartial = false).
    /// <paramref name="ct"/> abandons the read/decompress when the user navigates
    /// away (throws <see cref="OperationCanceledException"/>).
    /// </summary>
    public static MiqImage ParsePartial(string filePath, CancellationToken ct)
    {
        var kind = ResolveKind(ref filePath);
        if (IsSplit(kind))
            return ParseDetachedFirstVolume(filePath, kind, ct);

        if (kind == MiqFileKind.Nii)
        {
            var len = new FileInfo(filePath).Length;
            if (len > MaxArrayBytes)
                return ParseNiftiFirstVolume(filePath, blocked: true, ct);
            if (len > PartialLoadThreshold)
                return ParseNiftiFirstVolume(filePath, blocked: false, ct);
        }

        // Only multi-volume .nii.gz benefits; everything else parses fully.
        if (kind != MiqFileKind.NiiGz)
            return Parse(filePath, ct);

        // Probe: decompress just enough to read the NIfTI header (max 1024 B;
        // NIfTI-2 header is 540 B so 1024 covers both variants with margin).
        const int probeSize = 1024;
        byte[] probe;
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            probe = MiqBinaryReader.GunzipPartial(fs, probeSize, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException) { return Parse(filePath, ct); }

        if (probe.Length < 4)
            return Parse(filePath, ct);

        MiqHeader header;
        try { header = NiftiParser.ParseHeader(probe, compressed: true); }
        catch { return Parse(filePath, ct); }

        if (header.Volumes <= 1)
            return Parse(filePath, ct); // already 3-D — no benefit

        // Budget = bytes needed for header + exactly one volume.
        var budget = (long)header.VoxOffset
                     + (long)header.Width * header.Height * header.Depth
                     * header.Datatype.BytesPerVoxel();

        if (budget > int.MaxValue)
            return Parse(filePath, ct); // volume 0 alone can't be held — full path reports it

        // Full size from the header, NOT the gzip ISIZE footer: ISIZE is the size
        // mod 2^32, so a >4 GiB series can report a small ISIZE and be offered an
        // expansion that can never fit a byte[].
        var blocked = NiftiFileExtent(header) > MaxArrayBytes;

        // Too few volumes to pay off (see PartialGzipMinVolumes) — unless the full
        // data can't fit a byte[], which must stay on the blocked vol-0 view.
        if (header.Volumes < PartialGzipMinVolumes && !blocked)
            return Parse(filePath, ct);

        byte[] partial;
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            partial = MiqBinaryReader.GunzipPartial(fs, (int)budget, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException) { return Parse(filePath, ct); }

        // Stream ended inside volume 0: the file is shorter than its header says,
        // so it is small enough for the full path, which reports the truncation.
        if (partial.Length < budget)
            return Parse(filePath, ct);

        return new MiqImage
        {
            Header = header,
            Storage = partial,
            PayloadOffset = header.VoxOffset,
            IsPartial = true,
            // Full data won't fit a byte[] → never expand; show volume 0 + notice.
            ExpansionBlocked = blocked,
        };
    }

    /// Uncompressed NIfTI file size implied by the header: vox_offset plus every
    /// dim[1..7] (dim[5+] also hold data) × bytes-per-voxel. ParseHeader's
    /// ValidateDimensionExtent keeps the product within a long; the sum saturates.
    internal static long NiftiFileExtent(MiqHeader header)
    {
        long payload = header.Datatype.BytesPerVoxel();
        foreach (var d in header.Dimensions) payload *= Math.Max(1, d);
        return payload > long.MaxValue - header.VoxOffset
            ? long.MaxValue
            : payload + header.VoxOffset;
    }

    /// Loads only volume 0 of an uncompressed multi-volume NIfTI. Reads the header,
    /// then exactly header + volume-0 bytes from disk. Falls back to a full
    /// <see cref="Parse"/> for a single-volume file (nothing to defer) or when the
    /// data doesn't match the header. <paramref name="blocked"/> sets
    /// <see cref="MiqImage.ExpansionBlocked"/>: true when the full data can't be
    /// held (permanent volume-0 view); false to let the viewer expand in the
    /// background (a latency optimisation for large-but-in-limit files).
    /// Cleans a header's free-text description into <see cref="MiqHeader.Comment"/>:
    /// each part is cut at its first NUL (NIfTI pads `descrip` with NULs, sometimes
    /// followed by junk), control characters become spaces, whitespace is trimmed
    /// and empty parts are dropped. The parts are joined with "\n"; null when
    /// nothing is left.
    internal static string? HeaderComment(IEnumerable<string> parts)
    {
        var lines = new List<string>();
        foreach (var part in parts)
        {
            var nul = part.IndexOf('\0');
            var cut = nul >= 0 ? part.Substring(0, nul) : part;
            var cleaned = new string(cut.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
            if (cleaned.Length > 0) lines.Add(cleaned);
        }
        return lines.Count > 0 ? string.Join("\n", lines) : null;
    }

    /// <see cref="HeaderComment(IEnumerable{string})"/> for a fixed-size byte field
    /// (NIfTI `descrip`), decoded as UTF-8 with invalid bytes replaced.
    internal static string? HeaderComment(byte[] data, int offset, int length) =>
        data.Length - offset >= length
            ? HeaderComment([System.Text.Encoding.UTF8.GetString(data, offset, length)])
            : null;

    private static MiqImage ParseNiftiFirstVolume(string filePath, bool blocked, CancellationToken ct)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        // Probe enough for either NIfTI-1 (348 B) or NIfTI-2 (540 B) header.
        var probe = new byte[1024];
        var probed = ReadFully(fs, probe, probe.Length, ct);
        MiqHeader header;
        try { header = NiftiParser.ParseHeader(Trim(probe, probed), compressed: false); }
        catch { return Parse(filePath, ct); }

        // A single volume is assumed to always fit; if a >2 GB file claims to be
        // 3-D, fall back to the full path so it fails with a clear message.
        if (header.Volumes <= 1)
            return Parse(filePath, ct);

        var budget = (long)header.VoxOffset
                     + (long)header.Width * header.Height * header.Depth
                     * header.Datatype.BytesPerVoxel();
        if (budget <= 0 || budget > MaxArrayBytes)
            return Parse(filePath, ct); // volume 0 itself doesn't fit — defer to full path

        var storage = new byte[budget];
        fs.Seek(0, SeekOrigin.Begin);
        if (ReadFully(fs, storage, storage.Length, ct) < storage.Length)
            return Parse(filePath, ct); // file shorter than the header implies

        return new MiqImage
        {
            Header = header,
            Storage = storage,
            PayloadOffset = header.VoxOffset,
            IsPartial = true,
            ExpansionBlocked = blocked,
        };
    }

    /// Reads up to <paramref name="count"/> bytes into <paramref name="buf"/>,
    /// looping over short reads. Returns the number actually read (&lt; count at EOF).
    /// Reads in chunks, checking <paramref name="ct"/> between them, so a
    /// multi-GB volume-0 read abandons promptly on nav-away.
    private static int ReadFully(Stream s, byte[] buf, int count, CancellationToken ct)
    {
        const int chunk = 4 * 1024 * 1024;
        var total = 0;
        while (total < count)
        {
            ct.ThrowIfCancellationRequested();
            var n = s.Read(buf, total, Math.Min(chunk, count - total));
            if (n <= 0) break;
            total += n;
        }
        return total;
    }

    private static byte[] Trim(byte[] buf, int length)
    {
        if (length == buf.Length) return buf;
        var t = new byte[length];
        Array.Copy(buf, t, length);
        return t;
    }

    /// Rejects a header whose declared voxel extent (product of every axis ×
    /// bytes-per-voxel) can't be represented in a long. C# multiplies unchecked, so
    /// a wrapped product could slip past a `data.Length &lt; required` check.
    ///
    /// Arithmetic only — does NOT assert the payload is present, so partial loads
    /// and the 1 KB header probe are unaffected. Non-positive axes are skipped
    /// (callers already validated them; this avoids dividing by zero).
    internal static void ValidateDimensionExtent(IReadOnlyList<int> dims, int bytesPerVoxel)
    {
        long total = Math.Max(1, bytesPerVoxel);
        foreach (var d in dims)
        {
            if (d <= 0) continue;
            if (total > long.MaxValue / d) throw MiqException.InvalidDimensions();
            total *= d;
        }
    }

    /// Rejects spatial dimensions whose slice planes would overflow the int
    /// arithmetic that sizes a slice buffer (`new float[w * h]`, RGB `w * h * 3`).
    /// Any pair of spatial axes can form a plane. Headers between this bound and
    /// the CLR array limit fail cleanly with OutOfMemoryException instead.
    internal static void ValidateSlicePlaneExtent(int width, int height, int depth)
    {
        ValidatePlanePair(width, height);
        ValidatePlanePair(width, depth);
        ValidatePlanePair(height, depth);
    }

    /// int.MaxValue / 3 leaves room for the RGB path's ×3 without wrapping.
    private const long MaxSlicePlaneElements = int.MaxValue / 3;

    private static void ValidatePlanePair(long a, long b)
    {
        // Both operands are non-negative ints widened to long, so a*b (≤ 2^62)
        // cannot overflow the comparison.
        if (a > 0 && b > 0 && a * b > MaxSlicePlaneElements)
            throw MiqException.InvalidDimensions();
    }

    private static byte[] LoadAndDecompress(string filePath, MiqFileKind kind, CancellationToken ct)
    {
        if (!kind.IsCompressed())
        {
            var fileLen = new FileInfo(filePath).Length;
            if (fileLen > MaxFileBytes)
                throw new MiqException("File exceeds the 4 GB preview limit.");
            if (fileLen > MaxArrayBytes)
                return File.ReadAllBytes(filePath); // >2 GB: surfaces the BCL limit message
            return ReadAllBytes(filePath, (int)fileLen, ct);
        }

        // Native libdeflate is a single uninterruptible call, but fast (GB/s); the
        // override reads the compressed file in cancellable chunks before it.
        if (GzipDecompressorOverride is { } fast)
        {
            ct.ThrowIfCancellationRequested();
            return fast(filePath, ct);
        }

        // Stream straight from disk: don't hold the whole compressed file in
        // memory just to decompress it.
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
            FileShare.Read);

        // The extension claims compression — require a valid gzip header.
        if (fs.Length < 18 || fs.ReadByte() != 0x1F || fs.ReadByte() != 0x8B)
            throw MiqException.DecompressionFailed();

        // gzip ISIZE footer (uncompressed size mod 2^32, little-endian) lets
        // the decompressor allocate the exact buffer once.
        var footer = new byte[4];
        fs.Seek(-4, SeekOrigin.End);
        if (fs.Read(footer, 0, 4) != 4)
            throw MiqException.DecompressionFailed();
        var isize = (uint)(footer[0] | (footer[1] << 8)
                           | (footer[2] << 16) | (footer[3] << 24));
        // Don't pre-allocate from an implausibly large ISIZE (a crafted footer
        // could force a multi-GB allocation); 0 makes Gunzip grow to the real size.
        isize = MiqBinaryReader.TrustedIsize(isize, fs.Length);

        fs.Seek(0, SeekOrigin.Begin);
        return MiqBinaryReader.Gunzip(fs, isize, ct);
    }

    /// Reads a whole file into a byte[], checking <paramref name="ct"/> between
    /// chunks so a background load abandons promptly on nav-away instead of
    /// blocking the next preview. Caller guarantees length fits a byte[].
    internal static byte[] ReadAllBytes(string filePath, int length, CancellationToken ct)
    {
        var buf = new byte[length];
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        const int chunk = 4 * 1024 * 1024;
        var total = 0;
        while (total < length)
        {
            ct.ThrowIfCancellationRequested();
            var n = fs.Read(buf, total, Math.Min(chunk, length - total));
            if (n <= 0) break;
            total += n;
        }
        return total == length ? buf : Trim(buf, total);
    }
}
