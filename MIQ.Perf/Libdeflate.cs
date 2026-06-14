using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using MIQ.Parsing;

namespace MIQ.Perf;

// Self-contained libdeflate wrapper for MIQ.Perf. Does NOT link against
// QuickLook.Plugin.MIQ's LibdeflateGzip.cs (which calls internal MiqBinaryReader
// methods). Provides the same decompressor hook the plugin uses so parse timings
// reflect the real plugin path, not the slower managed GZipStream.
internal static class Libdeflate
{
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibrary(string path);

    [DllImport("libdeflate", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr libdeflate_alloc_decompressor();

    [DllImport("libdeflate", CallingConvention = CallingConvention.Cdecl)]
    private static extern void libdeflate_free_decompressor(IntPtr d);

    [DllImport("libdeflate", CallingConvention = CallingConvention.Cdecl)]
    private static extern int libdeflate_gzip_decompress(
        IntPtr d, byte[] inBytes, UIntPtr inLen,
        byte[] outBytes, UIntPtr outLen, out UIntPtr actualOut);

    private const int Success = 0;
    // DEFLATE theoretical maximum expansion ~1032:1; 1100 is a safe upper bound.
    private const long MaxExpansionRatio = 1100;

    internal static bool TryLoad()
    {
        try
        {
            var dir = Path.GetDirectoryName(typeof(Libdeflate).Assembly.Location) ?? ".";
            return LoadLibrary(Path.Combine(dir, "libdeflate.dll")) != IntPtr.Zero;
        }
        catch { return false; }
    }

    // Assigned to MiqParser.GzipDecompressorOverride.
    internal static byte[] Decompress(string path)
    {
        var input = File.ReadAllBytes(path);
        return DecompressBuffer(input) ?? ManagedGunzip(input);
    }

    // Assigned to MiqBinaryReader.GzipBufferDecompressorOverride.
    // Returns null (never calls the managed path itself) so the caller can fall back
    // without recursion — same contract as the plugin's LibdeflateGzip.DecompressBuffer.
    internal static byte[]? DecompressBuffer(byte[] input)
    {
        if (input.Length < 18 || input[0] != 0x1F || input[1] != 0x8B) return null;
        var isize = BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(input.Length - 4, 4));
        if (isize == 0 || (long)isize > (long)input.Length * MaxExpansionRatio) return null;

        var output = new byte[(int)isize];
        var d = libdeflate_alloc_decompressor();
        if (d == IntPtr.Zero) return null;
        try
        {
            var r = libdeflate_gzip_decompress(
                d, input, (UIntPtr)(ulong)input.Length,
                output, (UIntPtr)isize, out var actual);
            return r == Success && (ulong)actual == isize ? output : null;
        }
        finally { libdeflate_free_decompressor(d); }
    }

    private static byte[] ManagedGunzip(byte[] input)
    {
        using var ms = new MemoryStream(input, writable: false);
        using var gz = new GZipStream(ms, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gz.CopyTo(output);
        return output.ToArray();
    }
}
