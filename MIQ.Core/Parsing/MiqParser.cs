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
    /// fully up front (the partial dance isn't worth a brief scrubber flicker).
    /// Multi-volume .nii.gz already loads volume 0 first regardless of size.
    /// Non-const + internal so it can be lowered to exercise the path on small files.
    internal static long PartialLoadThreshold = 150L * 1024 * 1024;

    /// Minimum volume count for the .nii.gz vol-0-first partial load to be worth it.
    /// The partial path decompresses only volume 0, but via the managed GZipStream
    /// (libdeflate can't stop mid-member), which on the shipping net462 plugin is
    /// 15–50× slower than libdeflate. Decompressing 1/N of the data at that penalty
    /// only beats a single libdeflate pass over the whole file once N exceeds ~15–50,
    /// so for a small series (multi-echo GRE/QSM, phase/magnitude pairs — up to a dozen
    /// volumes) a full parse is faster on the real plugin AND avoids paying twice
    /// (volume 0 is managed-gunzip'd now, then the WHOLE file — volume 0 again — is
    /// libdeflate-decompressed on the first scrub) while making the scrubber live
    /// immediately. Genuinely multi-volume series (DWI/fMRI, dozens–hundreds of volumes)
    /// stay on the partial path, where the fractional decompress/read dominates the
    /// managed-gzip penalty and the slow/network-storage latency win it exists for is real.
    /// NB the net8 MIQ.Perf harness uses .NET 8's fast managed gzip, so it *understates*
    /// the partial-path cost and will read this fall-through as a "regression"; the win is
    /// on the net462 plugin. Non-const + internal so tests can vary it.
    internal static int PartialGzipMinVolumes = 12;

    /// <summary>
    /// Optional faster gzip decompressor. Given the file path, returns the
    /// fully-decompressed bytes. The QuickLook plugin sets this to a native
    /// libdeflate implementation (the .NET Framework built-in gzip is ~5–10×
    /// slower). When null, the built-in streaming path is used.
    /// </summary>
    public static Func<string, byte[]>? GzipDecompressorOverride;

    /// Parses a file fully. Pass a <paramref name="ct"/> for background loads
    /// (e.g. volume expansion) so a slow read/decompress abandons promptly when
    /// the user navigates away, freeing CPU/IO for the next preview.
    public static MiqImage Parse(string filePath, CancellationToken ct = default)
    {
        var kind = MiqFileKindExtensions.FromPath(filePath)
                   ?? throw MiqException.UnsupportedFileFormat();

        var data = LoadAndDecompress(filePath, kind, ct);
        var formatLabel = kind.IsCompressed() ? kind.DisplayName() : null;

        return kind switch
        {
            MiqFileKind.Nii or MiqFileKind.NiiGz     => NiftiParser.Parse(data, formatLabel),
            MiqFileKind.Mgh or MiqFileKind.Mgz       => MghParser.Parse(data, formatLabel),
            MiqFileKind.Mif or MiqFileKind.MifGz     => MifParser.Parse(data, formatLabel),
            MiqFileKind.Nrrd                         => NrrdParser.Parse(data, formatLabel),
            _ => throw MiqException.UnsupportedFileFormat(),
        };
    }

    /// <summary>
    /// Fast path for multi-volume NIfTI: returns just volume 0 as a partial
    /// <see cref="MiqImage"/> (<see cref="MiqImage.IsPartial"/> = true) so the
    /// preview appears without reading the whole file. Applies to any multi-volume
    /// <c>.nii.gz</c> (decompresses only volume 0), and to uncompressed <c>.nii</c>
    /// above <see cref="PartialLoadThreshold"/> (or <see cref="MaxArrayBytes"/>, in
    /// which case it's permanent — <see cref="MiqImage.ExpansionBlocked"/>).
    /// Falls back to a full <see cref="Parse"/> for 3-D / small files, or when
    /// ISIZE is unreliable; those return a complete image (IsPartial = false).
    /// </summary>
    public static MiqImage ParsePartial(string filePath)
    {
        var kind = MiqFileKindExtensions.FromPath(filePath)
                   ?? throw MiqException.UnsupportedFileFormat();

        // Uncompressed NIfTI volume-0-first paths. Above MaxArrayBytes the full
        // data can't be held, so the partial is permanent (blocked, no scrubber);
        // above PartialLoadThreshold it's a latency optimisation that expands in
        // the background. Both fall back to a full parse for 3-D / small files.
        if (kind == MiqFileKind.Nii)
        {
            var len = new FileInfo(filePath).Length;
            if (len > MaxArrayBytes)
                return ParseNiftiFirstVolume(filePath, blocked: true);
            if (len > PartialLoadThreshold)
                return ParseNiftiFirstVolume(filePath, blocked: false);
        }

        // Only multi-volume .nii.gz benefits; everything else parses fully.
        if (kind != MiqFileKind.NiiGz)
            return Parse(filePath);

        var isize = ReadGzipIsize(filePath);
        if (isize == 0)
            return Parse(filePath); // unknown / >4 GB — let full path handle it

        // Probe: decompress just enough to read the NIfTI header (max 1024 B;
        // NIfTI-2 header is 540 B so 1024 covers both variants with margin).
        const int probeSize = 1024;
        byte[] probe;
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            probe = MiqBinaryReader.GunzipPartial(fs, probeSize);
        }
        catch { return Parse(filePath); }

        if (probe.Length < 4)
            return Parse(filePath);

        MiqHeader header;
        try { header = NiftiParser.ParseHeader(probe, kind.DisplayName()); }
        catch { return Parse(filePath); }

        if (header.Volumes <= 1)
            return Parse(filePath); // already 3-D — no benefit

        // Budget = bytes needed for header + exactly one volume.
        var budget = (long)header.VoxOffset
                     + (long)header.Width * header.Height * header.Depth
                     * header.Datatype.BytesPerVoxel();

        if (budget >= (long)isize || budget > int.MaxValue)
            return Parse(filePath); // volume 0 is the whole file

        // Too few volumes for the partial dance to pay off: a full libdeflate parse
        // is cheaper than slow-managed-gunzipping volume 0 (and re-decompressing the
        // whole file on first scrub), and leaves the scrubber live immediately. Only
        // when the full data fits a byte[] — an oversized file (isize > MaxArrayBytes)
        // can't be parsed whole, so it must stay on the permanent blocked vol-0 view.
        if (header.Volumes < PartialGzipMinVolumes && isize <= (ulong)MaxArrayBytes)
            return Parse(filePath);

        byte[] partial;
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            partial = MiqBinaryReader.GunzipPartial(fs, (int)budget);
        }
        catch { return Parse(filePath); }

        if (partial.Length < header.VoxOffset)
            return Parse(filePath); // decompression shorter than expected

        return new MiqImage
        {
            Header = header,
            Storage = partial,
            PayloadOffset = header.VoxOffset,
            IsPartial = true,
            // Full data won't fit a byte[] → never expand; show volume 0 + notice.
            ExpansionBlocked = isize > (ulong)MaxArrayBytes,
        };
    }

    /// Loads only volume 0 of an uncompressed multi-volume NIfTI. Reads the header,
    /// then exactly header + volume-0 bytes from disk. Falls back to a full
    /// <see cref="Parse"/> for a single-volume file (nothing to defer) or when the
    /// data doesn't match the header. <paramref name="blocked"/> sets
    /// <see cref="MiqImage.ExpansionBlocked"/>: true when the full data can't be
    /// held (permanent volume-0 view); false to let the viewer expand in the
    /// background (a latency optimisation for large-but-in-limit files).
    private static MiqImage ParseNiftiFirstVolume(string filePath, bool blocked)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        // Probe enough for either NIfTI-1 (348 B) or NIfTI-2 (540 B) header.
        var probe = new byte[1024];
        var probed = ReadFully(fs, probe, probe.Length);
        MiqHeader header;
        try { header = NiftiParser.ParseHeader(Trim(probe, probed)); }
        catch { return Parse(filePath); }

        // A single volume is assumed to always fit; if a >2 GB file claims to be
        // 3-D, fall back to the full path so it fails with a clear message.
        if (header.Volumes <= 1)
            return Parse(filePath);

        var budget = (long)header.VoxOffset
                     + (long)header.Width * header.Height * header.Depth
                     * header.Datatype.BytesPerVoxel();
        if (budget <= 0 || budget > MaxArrayBytes)
            return Parse(filePath); // volume 0 itself doesn't fit — defer to full path

        var storage = new byte[budget];
        fs.Seek(0, SeekOrigin.Begin);
        if (ReadFully(fs, storage, storage.Length) < storage.Length)
            return Parse(filePath); // file shorter than the header implies

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
    private static int ReadFully(Stream s, byte[] buf, int count)
    {
        var total = 0;
        int n;
        while (total < count && (n = s.Read(buf, total, count - total)) > 0)
            total += n;
        return total;
    }

    private static byte[] Trim(byte[] buf, int length)
    {
        if (length == buf.Length) return buf;
        var t = new byte[length];
        Array.Copy(buf, t, length);
        return t;
    }

    private static uint ReadGzipIsize(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fs.Length < 18) return 0;
            var footer = new byte[4];
            fs.Seek(-4, SeekOrigin.End);
            if (fs.Read(footer, 0, 4) != 4) return 0;
            return (uint)(footer[0] | (footer[1] << 8) | (footer[2] << 16) | (footer[3] << 24));
        }
        catch { return 0; }
    }

    /// Rejects a header whose declared voxel extent (product of every axis ×
    /// bytes-per-voxel) can't be represented in a long. Parsers derive their
    /// required-payload size from that product, and C# multiplies unchecked — so
    /// a wrapped product can slip past a `data.Length &lt; required` check and let a
    /// tiny file claim an enormous volume. (Swift trapped here instead; the C#
    /// failure is silent, which is why the guard matters more on this side.)
    ///
    /// Purely an arithmetic representability guard — it does NOT assert the
    /// payload is present, so the bounded NIfTI vol-0-first cold load (whose
    /// buffer holds only volume 0, and whose header probe is 1 KB) is unaffected.
    /// Validating in each format's earliest shared header-parse step also protects
    /// every downstream product, since each is ≤ this total.
    ///
    /// Non-positive axes are skipped rather than rejected: every caller has
    /// already validated positivity, and skipping keeps a stray 0 from turning the
    /// overflow test's division into a DivideByZeroException.
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
    /// arithmetic that sizes a slice buffer. Any two of the three spatial axes can
    /// become a plane's (width, height) — coronal is W×D, sagittal H×D, axial W×H,
    /// and the reoriented modes only permute which pair — and a plane is allocated
    /// as `new float[w * h]`, with RGB adding `new byte[w * h * 3]`. Both are int
    /// multiplies, so an unvalidated pair wraps to a small or negative length.
    ///
    /// The bound sits far above anything reachable: it permits planes larger than
    /// the CLR can allocate as a single array (a float[] tops out near 536 M
    /// elements), so a header between this bound and reality still fails cleanly
    /// with OutOfMemoryException rather than wrapping. No real volume is within
    /// orders of magnitude of it.
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

        // Native libdeflate is a single uninterruptible call, but fast (GB/s);
        // only the cancellation check before it is meaningful.
        if (GzipDecompressorOverride is { } fast)
        {
            ct.ThrowIfCancellationRequested();
            return fast(filePath);
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
    private static byte[] ReadAllBytes(string filePath, int length, CancellationToken ct)
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
