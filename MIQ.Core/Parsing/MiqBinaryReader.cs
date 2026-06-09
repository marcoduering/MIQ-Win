using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Threading;

namespace MIQ.Parsing;

/// <summary>
/// Endianness-aware scalar/array readers over a byte buffer, plus gzip
/// detection and decompression. Mirrors MIQCore's <c>MIQBinaryReader</c>.
/// Out-of-range reads throw <see cref="MiqException"/> (truncated data).
/// </summary>
public static class MiqBinaryReader
{
    private static ReadOnlySpan<byte> Slice(byte[] data, int offset, int size)
    {
        if (offset < 0 || offset + size > data.Length)
            throw MiqException.TruncatedData();
        return data.AsSpan(offset, size);
    }

    public static short Int16(byte[] data, int offset, bool littleEndian)
    {
        var s = Slice(data, offset, 2);
        return littleEndian
            ? BinaryPrimitives.ReadInt16LittleEndian(s)
            : BinaryPrimitives.ReadInt16BigEndian(s);
    }

    public static int Int32(byte[] data, int offset, bool littleEndian)
    {
        var s = Slice(data, offset, 4);
        return littleEndian
            ? BinaryPrimitives.ReadInt32LittleEndian(s)
            : BinaryPrimitives.ReadInt32BigEndian(s);
    }

    public static long Int64(byte[] data, int offset, bool littleEndian)
    {
        var s = Slice(data, offset, 8);
        return littleEndian
            ? BinaryPrimitives.ReadInt64LittleEndian(s)
            : BinaryPrimitives.ReadInt64BigEndian(s);
    }

    // Read the raw bits then reinterpret: BinaryPrimitives.ReadSingle/Double*
    // are unavailable in the System.Memory backport used on .NET Framework.
    public static float Float32(byte[] data, int offset, bool littleEndian)
    {
        var s = Slice(data, offset, 4);
        var bits = littleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(s)
            : BinaryPrimitives.ReadUInt32BigEndian(s);
        return MiqCompat.Int32BitsToSingle((int)bits);
    }

    public static double Float64(byte[] data, int offset, bool littleEndian)
    {
        var s = Slice(data, offset, 8);
        var bits = littleEndian
            ? BinaryPrimitives.ReadUInt64LittleEndian(s)
            : BinaryPrimitives.ReadUInt64BigEndian(s);
        return MiqCompat.Int64BitsToDouble((long)bits);
    }

    public static ushort Uint16(byte[] data, int offset, bool littleEndian)
    {
        var s = Slice(data, offset, 2);
        return littleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(s)
            : BinaryPrimitives.ReadUInt16BigEndian(s);
    }

    public static uint Uint32(byte[] data, int offset, bool littleEndian)
    {
        var s = Slice(data, offset, 4);
        return littleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(s)
            : BinaryPrimitives.ReadUInt32BigEndian(s);
    }

    public static ulong Uint64(byte[] data, int offset, bool littleEndian)
    {
        var s = Slice(data, offset, 8);
        return littleEndian
            ? BinaryPrimitives.ReadUInt64LittleEndian(s)
            : BinaryPrimitives.ReadUInt64BigEndian(s);
    }

    public static short[] Int16Array(byte[] data, int offset, int count, bool littleEndian)
    {
        var result = new short[count];
        for (var i = 0; i < count; i++)
            result[i] = Int16(data, offset + i * sizeof(short), littleEndian);
        return result;
    }

    public static long[] Int64Array(byte[] data, int offset, int count, bool littleEndian)
    {
        var result = new long[count];
        for (var i = 0; i < count; i++)
            result[i] = Int64(data, offset + i * sizeof(long), littleEndian);
        return result;
    }

    public static float[] Float32Array(byte[] data, int offset, int count, bool littleEndian)
    {
        var result = new float[count];
        for (var i = 0; i < count; i++)
            result[i] = Float32(data, offset + i * sizeof(float), littleEndian);
        return result;
    }

    public static double[] Float64Array(byte[] data, int offset, int count, bool littleEndian)
    {
        var result = new double[count];
        for (var i = 0; i < count; i++)
            result[i] = Float64(data, offset + i * sizeof(double), littleEndian);
        return result;
    }

    public static bool IsLikelyGzip(byte[] data) =>
        data.Length >= 2 && data[0] == 0x1F && data[1] == 0x8B;

    /// <summary>
    /// Optional faster gzip decompressor over an in-memory buffer. Given the
    /// full gzip member bytes, returns the decompressed bytes, or null to defer
    /// to the managed path. The QuickLook plugin wires this to native libdeflate
    /// for mid-file gzip segments (e.g. a gzip-encoded NRRD payload), where the
    /// path-based whole-file <see cref="MiqParser.GzipDecompressorOverride"/>
    /// doesn't apply. Implementations must not call back into
    /// <see cref="Gunzip(byte[])"/> (return null instead) to avoid recursion.
    /// </summary>
    public static Func<byte[], byte[]?>? GzipBufferDecompressorOverride;

    public static byte[] Gunzip(byte[] data)
    {
        if (GzipBufferDecompressorOverride is { } fast && fast(data) is { } result)
            return result;
        return GunzipManaged(data);
    }

    /// Managed (built-in <see cref="GZipStream"/>) buffer decompression, bypassing
    /// any native override — the override's fallback path calls this directly.
    internal static byte[] GunzipManaged(byte[] data)
    {
        using var input = new MemoryStream(data, writable: false);
        return Gunzip(input, TrustedIsize(GzipIsize(data), data.Length));
    }

    /// gzip ISIZE footer: uncompressed size mod 2^32 (little-endian last 4 bytes).
    internal static uint GzipIsize(byte[] data) =>
        data.Length >= 4
            ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(data.Length - 4, 4))
            : 0u;

    /// The gzip ISIZE footer is attacker-controlled: a tiny file can claim a huge
    /// uncompressed size to force a multi-GB pre-allocation (a cheap denial of
    /// service). Trust it only when it is within DEFLATE's maximum expansion of the
    /// compressed input (~1032:1); a legitimate stream can never exceed that ratio,
    /// so a real file is never rejected. Returns 0 (treat as unknown, so the caller
    /// grows the buffer to the actual output) when the claim is implausible.
    internal static uint TrustedIsize(uint isize, long compressedLength) =>
        isize <= compressedLength * MaxDeflateExpansion ? isize : 0u;

    // DEFLATE's theoretical maximum compression ratio is ~1032:1; round up for a
    // safety margin (gzip framing only adds bytes, lowering the real ratio).
    private const long MaxDeflateExpansion = 1100;

    /// Decompress <paramref name="input"/> into a single buffer pre-sized from
    /// the gzip ISIZE — avoids the repeated MemoryStream reallocations + final
    /// ToArray copy (2–3× the decompressed size) the naive approach incurs.
    /// Falls back to a growable buffer if ISIZE is unreliable (streams &gt; 4 GB).
    public static byte[] Gunzip(Stream input, uint sizeHint, CancellationToken ct = default)
    {
        try
        {
            using var gzip = new GZipStream(input, CompressionMode.Decompress);

            if (sizeHint > 0)
            {
                var buf = new byte[sizeHint];
                var total = 0;
                int n;
                while (total < buf.Length &&
                       (n = gzip.Read(buf, total, buf.Length - total)) > 0)
                {
                    total += n;
                    ct.ThrowIfCancellationRequested(); // abandon promptly on nav-away
                }

                // ISIZE exact (the common case): done in one allocation.
                if (gzip.ReadByte() == -1)
                    return total == buf.Length ? buf : Trim(buf, total);

                // ISIZE was truncated/wrong (> 4 GB) — continue growably.
                using var rest = new MemoryStream();
                rest.Write(buf, 0, total);
                gzip.CopyTo(rest);
                return rest.ToArray();
            }

            using var output = new MemoryStream();
            gzip.CopyTo(output);
            return output.ToArray();
        }
        catch (InvalidDataException)
        {
            throw MiqException.DecompressionFailed();
        }
    }

    /// Decompress at most <paramref name="maxBytes"/> bytes from a gzip stream,
    /// stopping as soon as the buffer is full (no further input is consumed).
    /// Returns fewer bytes when the stream ends before the limit.
    public static byte[] GunzipPartial(Stream input, int maxBytes)
    {
        try
        {
            // leaveOpen: caller owns the FileStream lifetime.
            using var gzip = new GZipStream(input, CompressionMode.Decompress, leaveOpen: true);
            var buf = new byte[maxBytes];
            var total = 0;
            int n;
            while (total < maxBytes && (n = gzip.Read(buf, total, maxBytes - total)) > 0)
                total += n;
            return total == maxBytes ? buf : Trim(buf, total);
        }
        catch (InvalidDataException)
        {
            throw MiqException.DecompressionFailed();
        }
    }

    private static byte[] Trim(byte[] buf, int length)
    {
        var t = new byte[length];
        Array.Copy(buf, t, length);
        return t;
    }
}
