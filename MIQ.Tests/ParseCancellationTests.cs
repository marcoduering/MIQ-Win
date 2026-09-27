using System.Buffers.Binary;
using System.IO.Compression;
using MIQ.Parsing;
using Xunit;

namespace MIQ.Tests;

// Changes the process-wide MiqParser.PartialLoadThreshold seam in one test, so
// this class must not overlap others that parse .nii files.
[CollectionDefinition(nameof(PartialLoadSeam), DisableParallelization = true)]
public class PartialLoadSeam { }

// The first preview load (ParsePartial) must honour its token on every path, so
// navigating away abandons the read/decompress instead of finishing it.
[Collection(nameof(PartialLoadSeam))]
public class ParseCancellationTests
{
    const int Side = 8;
    const int VolumeBytes = Side * Side * Side * 2; // int16
    const int Offset = 352;

    static byte[] Nifti1(int volumes)
    {
        var file = new byte[Offset + volumes * VolumeBytes];
        var h = file.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(h, 348);
        short[] dims = [(short)(volumes > 1 ? 4 : 3), Side, Side, Side, (short)volumes];
        for (var i = 0; i < dims.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(h[(40 + i * 2)..], dims[i]);
        BinaryPrimitives.WriteInt16LittleEndian(h[70..], (short)MiqDatatype.Int16);
        BinaryPrimitives.WriteInt16LittleEndian(h[72..], 16);
        for (var i = 0; i < 8; i++)
            BinaryPrimitives.WriteSingleLittleEndian(h[(76 + i * 4)..], 1f);
        BinaryPrimitives.WriteSingleLittleEndian(h[108..], Offset);
        return file;
    }

    static byte[] Gzip(byte[] raw)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            gz.Write(raw);
        return ms.ToArray();
    }

    static CancellationToken Cancelled()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        return cts.Token;
    }

    static void AssertCancels(byte[] bytes, string suffix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"miq-cancel-{Guid.NewGuid():N}{suffix}");
        File.WriteAllBytes(path, bytes);
        try
        {
            Assert.ThrowsAny<OperationCanceledException>(
                () => MiqParser.ParsePartial(path, Cancelled()));
            // The same file still loads with no token (default: never cancelled).
            Assert.NotNull(MiqParser.ParsePartial(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void MultiVolumeGzip_PartialPath_Cancels() =>
        AssertCancels(Gzip(Nifti1(MiqParser.PartialGzipMinVolumes)), ".nii.gz");

    [Fact]
    public void SingleVolumeGzip_FullParsePath_Cancels() =>
        AssertCancels(Gzip(Nifti1(1)), ".nii.gz");

    [Fact]
    public void SmallUncompressed_FullParsePath_Cancels() =>
        AssertCancels(Nifti1(2), ".nii");

    [Fact]
    public void LargeUncompressed_Volume0Path_Cancels()
    {
        var saved = MiqParser.PartialLoadThreshold;
        MiqParser.PartialLoadThreshold = 0; // force the vol-0-first read on a tiny file
        try { AssertCancels(Nifti1(2), ".nii"); }
        finally { MiqParser.PartialLoadThreshold = saved; }
    }

    [Fact]
    public void GunzipPartial_Cancels()
    {
        using var input = new MemoryStream(Gzip(Nifti1(1)));
        Assert.ThrowsAny<OperationCanceledException>(
            () => MiqBinaryReader.GunzipPartial(input, 1024, Cancelled()));
    }
}
