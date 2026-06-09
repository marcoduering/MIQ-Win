using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using MIQ.Parsing;

namespace QuickLook.Plugin.MIQ;

/// <summary>
/// Native gzip decompression via bundled libdeflate.dll — the .NET Framework
/// built-in <c>GZipStream</c> is ~5–10× slower. Single-shot: we know the exact
/// output size from the gzip ISIZE footer, which is libdeflate's ideal case.
/// Falls back to the managed path for the rare unreliable-ISIZE case.
/// </summary>
internal static class LibdeflateGzip
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

    // libdeflate_result
    private const int Success = 0;

    /// Loads libdeflate.dll from the plugin folder (DllImport otherwise probes
    /// the host app dir, not ours). Throws if unavailable so the caller can
    /// keep the managed fallback.
    internal static void EnsureLoaded()
    {
        var dir = Path.GetDirectoryName(typeof(LibdeflateGzip).Assembly.Location) ?? ".";
        var dll = Path.Combine(dir, "libdeflate.dll");
        if (LoadLibrary(dll) == IntPtr.Zero)
            throw new DllNotFoundException($"libdeflate.dll not loadable from {dll}");
    }

    /// Whole-file decompressor for path-based <see cref="MiqParser.GzipDecompressorOverride"/>.
    internal static byte[] Decompress(string path)
    {
        var input = File.ReadAllBytes(path);
        // Managed fallback handles the rare multi-member / odd-ISIZE / >4 GB cases.
        return DecompressBuffer(input) ?? MiqBinaryReader.GunzipManaged(input);
    }

    /// Buffer decompressor for <see cref="MiqBinaryReader.GzipBufferDecompressorOverride"/>
    /// (e.g. a gzip-encoded NRRD payload). Returns null — never the managed path —
    /// for any case libdeflate can't single-shot, letting the caller fall back
    /// without recursing back through the override.
    internal static byte[]? DecompressBuffer(byte[] input)
    {
        if (input.Length < 18 || input[0] != 0x1F || input[1] != 0x8B)
            return null;

        // gzip ISIZE: uncompressed size mod 2^32 (little-endian footer). Trust it
        // only within DEFLATE's plausible expansion of the input — a crafted footer
        // could otherwise force a multi-GB allocation here. Implausible / unknown /
        // >4 GB → null, and the managed fallback sizes to the actual output.
        var isize = MiqBinaryReader.TrustedIsize(MiqBinaryReader.GzipIsize(input), input.Length);
        if (isize == 0) return null;

        var output = new byte[isize];
        var d = libdeflate_alloc_decompressor();
        if (d == IntPtr.Zero) return null;
        try
        {
            var r = libdeflate_gzip_decompress(
                d, input, (UIntPtr)input.Length,
                output, (UIntPtr)output.Length, out var actual);

            // Exact single-member case (the norm). Anything else → null so the
            // caller uses the managed path (multi-member / odd ISIZE).
            return r == Success && (ulong)actual == isize ? output : null;
        }
        finally
        {
            libdeflate_free_decompressor(d);
        }
    }
}
