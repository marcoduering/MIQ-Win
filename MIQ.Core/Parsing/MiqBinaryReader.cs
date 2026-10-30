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

    // Largest single Read in the cancellable loops: one Read can otherwise fill a
    // multi-GB buffer before the token is checked again.
    private const int CancelChunk = 4 * 1024 * 1024;

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
                       (n = gzip.Read(buf, total, Math.Min(CancelChunk, buf.Length - total))) > 0)
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
    /// Returns fewer bytes when the stream ends before the limit. Checks
    /// <paramref name="ct"/> before each read (abandon promptly on nav-away).
    public static byte[] GunzipPartial(Stream input, int maxBytes, CancellationToken ct = default)
    {
        try
        {
            // leaveOpen: caller owns the FileStream lifetime.
            using var gzip = new GZipStream(input, CompressionMode.Decompress, leaveOpen: true);
            var buf = new byte[maxBytes];
            var total = 0;
            while (total < maxBytes)
            {
                ct.ThrowIfCancellationRequested();
                var n = gzip.Read(buf, total, Math.Min(CancelChunk, maxBytes - total));
                if (n <= 0) break;
                total += n;
            }
            return total == maxBytes ? buf : Trim(buf, total);
        }
        catch (InvalidDataException)
        {
            throw MiqException.DecompressionFailed();
        }
    }

    /// <summary>
    /// Optional faster zlib decompressor. Given a zlib (RFC 1950) stream and the
    /// exact output size, returns the decompressed bytes, or null to defer to the
    /// managed path, which then gives the verdict (and verifies the check value
    /// itself). The QuickLook plugin wires this to native libdeflate.
    /// </summary>
    public static Func<byte[], int, byte[]?>? ZlibBufferDecompressorOverride;

    /// <summary>
    /// Inflates a zlib-wrapped (RFC 1950) stream, the encoding MetaImage writes for
    /// <c>CompressedData = True</c>, producing exactly <paramref name="outputBytes"/>
    /// (the payload size the caller's header declares) and ignoring anything past
    /// it. A gzip stream is accepted too, since some writers emit one. Unlike gzip,
    /// zlib has no size trailer, so a declared size beyond DEFLATE's maximum
    /// expansion of the input is rejected before anything is allocated.
    /// Throws <see cref="MiqException.TruncatedData"/> when the stream ends early,
    /// and <see cref="MiqException.DecompressionFailed"/> when its check value fails.
    /// </summary>
    public static byte[] InflateZlib(byte[] data, int outputBytes)
    {
        if (data.Length < 2 || outputBytes < 1) throw MiqException.DecompressionFailed();
        if (outputBytes > data.Length * MaxDeflateExpansion) throw MiqException.TruncatedData();

        var isGzip = IsLikelyGzip(data);
        if (!isGzip)
        {
            // CMF/FLG: deflate (CM = 8), a check value that is a multiple of 31, and
            // no preset dictionary (FDICT), which MetaIO never writes.
            var cmf = data[0];
            var flg = data[1];
            if ((cmf & 0x0F) != 8 || (cmf << 8 | flg) % 31 != 0 || (flg & 0x20) != 0)
                throw MiqException.DecompressionFailed();
            if (ZlibBufferDecompressorOverride is { } fast && fast(data, outputBytes) is { } result)
                return result;
        }

        try
        {
            var output = new byte[outputBytes];
            if (isGzip)
            {
                // GZipStream checks the CRC itself once it reaches the footer.
                using var gzip = new GZipStream(new MemoryStream(data, writable: false), CompressionMode.Decompress);
                Inflate(gzip, output);
                return output;
            }

            // The zlib header is two bytes. Raw DEFLATE never reads the Adler-32
            // trailer, which follows wherever the DEFLATE data ends: find that point
            // (see DeflateEndLocator), then check the trailer there. A stream that
            // runs past the declared size keeps its prefix unverified.
            var input = new DeflateEndLocator(data, 2, data.Length - DeflateEndLocator.TailWindow);
            if (!Inflate(new DeflateStream(input, CompressionMode.Decompress), output)) return output;
            if (!input.EndIsExact)
            {
                // The stream ended before the tail window (trailing bytes): decode
                // again, single-stepping from where the last chunk began.
                input = new DeflateEndLocator(data, 2, input.LastChunkStart);
                Inflate(new DeflateStream(input, CompressionMode.Decompress), output);
                if (!input.EndIsExact) throw MiqException.DecompressionFailed();
            }
            var end = (int)input.Position; // at most data.Length
            if (data.Length - end < 4) throw MiqException.TruncatedData();
            if (Adler32(output) != BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(end)))
                throw MiqException.DecompressionFailed();
            return output;
        }
        catch (InvalidDataException)
        {
            throw MiqException.DecompressionFailed();
        }
    }

    /// Fills <paramref name="output"/> from <paramref name="inflater"/> (and disposes
    /// it). True when the stream ends exactly there, false when it runs on.
    private static bool Inflate(Stream inflater, byte[] output)
    {
        using (inflater)
        {
            var total = 0;
            int n;
            while (total < output.Length &&
                   (n = inflater.Read(output, total, Math.Min(CancelChunk, output.Length - total))) > 0)
                total += n;
            if (total < output.Length) throw MiqException.TruncatedData();
            return inflater.Read(new byte[1], 0, 1) == 0;
        }
    }

    /// <summary>
    /// The input of a raw DEFLATE decode, handing out bytes so that where the
    /// DEFLATE data ended is known afterwards. A decompressor asks for more input
    /// only once it has used up what it holds, so every chunk but the last is fully
    /// consumed. From <c>byteModeFrom</c> on it gets one byte per read; ending on
    /// such a read, it has consumed exactly <see cref="Position"/> bytes. This
    /// controls what the decompressor can see instead of asking how far it read
    /// (its own buffering can read ahead).
    /// </summary>
    private sealed class DeflateEndLocator(byte[] data, int start, int byteModeFrom) : Stream
    {
        /// Bytes single-stepped at the end of the input. MetaIO's own files end
        /// with the trailer, so the first pass is exact; longer trailing data takes
        /// a second pass.
        internal const int TailWindow = 4096;

        private int _position = start;

        /// Index in the data after the last byte handed out.
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        /// Where the last read began; everything before it was consumed.
        public int LastChunkStart { get; private set; } = start;
        /// Whether the last read was a single byte, so <see cref="Position"/> is
        /// exactly where the DEFLATE data ended.
        public bool EndIsExact { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= data.Length || count <= 0) return 0;
            EndIsExact = _position >= byteModeFrom;
            var n = EndIsExact ? 1 : Math.Min(count, byteModeFrom - _position);
            Array.Copy(data, _position, buffer, offset, n);
            LastChunkStart = _position;
            _position += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// RFC 1950 Adler-32 of <paramref name="data"/>.
    internal static uint Adler32(byte[] data)
    {
        const uint Modulus = 65521;
        const int MaxRun = 5552; // the longest run before the sums can overflow 32 bits
        uint a = 1, b = 0;
        for (var start = 0; start < data.Length; start += MaxRun)
        {
            var end = start + Math.Min(MaxRun, data.Length - start);
            for (var i = start; i < end; i++)
            {
                a += data[i];
                b += a;
            }
            a %= Modulus;
            b %= Modulus;
        }
        return b << 16 | a;
    }

    private static byte[] Trim(byte[] buf, int length)
    {
        var t = new byte[length];
        Array.Copy(buf, t, length);
        return t;
    }
}
