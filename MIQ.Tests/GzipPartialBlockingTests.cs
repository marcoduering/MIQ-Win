using System.Buffers.Binary;
using System.IO.Compression;
using MIQ.Parsing;
using Xunit;

namespace MIQ.Tests;

// Lowers the process-wide MiqParser.MaxArrayBytes seam, which MifParser and the
// .nii paths also read, so these tests must not overlap any other class.
[CollectionDefinition(nameof(StaticParserSeams), DisableParallelization = true)]
public class StaticParserSeams { }

// ParsePartial's blocked/expandable decision for .nii.gz. The gzip ISIZE footer
// is the uncompressed size mod 2^32, so a >4 GiB series can report a small
// ISIZE; the decision must come from the header. The wrap is simulated by
// patching the footer and lowering MaxArrayBytes (the managed partial gunzip
// stops long before the footer, so the patched value is never validated).
[Collection(nameof(StaticParserSeams))]
public class GzipPartialBlockingTests
{
    const int Side = 8;
    const int VolumeBytes = Side * Side * Side * 2; // int16
    const int Offset = 352;
    const int Volumes = 12; // == PartialGzipMinVolumes default
    const int FileBytes = Offset + Volumes * VolumeBytes; // 12640

    static byte[] Nifti1(int volumes, int payloadBytes)
    {
        var file = new byte[Offset + payloadBytes];
        var h = file.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(h, 348);
        short[] dims = [4, Side, Side, Side, (short)volumes];
        for (var i = 0; i < dims.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(h[(40 + i * 2)..], dims[i]);
        BinaryPrimitives.WriteInt16LittleEndian(h[70..], (short)MiqDatatype.Int16);
        BinaryPrimitives.WriteInt16LittleEndian(h[72..], 16);
        for (var i = 0; i < 8; i++)
            BinaryPrimitives.WriteSingleLittleEndian(h[(76 + i * 4)..], 1f);
        BinaryPrimitives.WriteSingleLittleEndian(h[108..], Offset);
        return file;
    }

    static byte[] Gzip(byte[] raw, uint? isizeOverride = null)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            gz.Write(raw);
        var bytes = ms.ToArray();
        if (isizeOverride is { } isize)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bytes.Length - 4), isize);
        return bytes;
    }

    static MiqImage ParsePartialWith(byte[] gz, long maxArrayBytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"miq-block-{Guid.NewGuid():N}.nii.gz");
        File.WriteAllBytes(path, gz);
        var saved = MiqParser.MaxArrayBytes;
        MiqParser.MaxArrayBytes = maxArrayBytes;
        try { return MiqParser.ParsePartial(path); }
        finally
        {
            MiqParser.MaxArrayBytes = saved;
            File.Delete(path);
        }
    }

    [Fact]
    public void WrappedIsize_HeaderTooLarge_IsBlocked()
    {
        // Header implies 12640 B > limit 10000, but the footer claims 5000 B
        // (> volume 0, < limit): the shape of a >4 GiB series whose ISIZE wrapped.
        var gz = Gzip(Nifti1(Volumes, Volumes * VolumeBytes), isizeOverride: 5000);
        var image = ParsePartialWith(gz, maxArrayBytes: 10_000);
        Assert.True(image.IsPartial);
        Assert.True(image.ExpansionBlocked);
    }

    [Fact]
    public void HeaderFits_IsExpandable()
    {
        var gz = Gzip(Nifti1(Volumes, Volumes * VolumeBytes));
        var image = ParsePartialWith(gz, maxArrayBytes: FileBytes);
        Assert.True(image.IsPartial);
        Assert.False(image.ExpansionBlocked);
    }

    [Fact]
    public void FewVolumes_HeaderTooLarge_StaysOnBlockedVolume0View()
    {
        // Below PartialGzipMinVolumes a fitting file parses fully; one that can't
        // fit must still take the blocked partial path, whatever ISIZE says.
        const int few = 2;
        var gz = Gzip(Nifti1(few, few * VolumeBytes), isizeOverride: 1000);
        var image = ParsePartialWith(gz, maxArrayBytes: Offset + VolumeBytes);
        Assert.True(image.IsPartial);
        Assert.True(image.ExpansionBlocked);
    }

    [Fact]
    public void DataEndsInsideVolume0_FullParseReportsTruncation()
    {
        // Header claims 12 volumes but only half of volume 0 is present.
        var gz = Gzip(Nifti1(Volumes, VolumeBytes / 2));
        Assert.Throws<MiqException>(() => ParsePartialWith(gz, maxArrayBytes: 0x7FFFFFC7));
    }

    [Fact]
    public void NiftiFileExtent_CountsEveryDimension()
    {
        var header = NiftiParser.ParseHeader(Nifti1(Volumes, 0));
        Assert.Equal((long)FileBytes, MiqParser.NiftiFileExtent(header));
    }
}
