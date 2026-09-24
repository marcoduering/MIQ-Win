using System.Buffers.Binary;
using System.IO.Compression;
using MIQ.Parsing;
using Xunit;

namespace MIQ.Tests;

// NIfTI-2 shares its file kinds with NIfTI-1, whose display name says "NIfTI-1".
// Port of MIQ 1.5.1 (MIQ@d6dabd5) — with one Windows-specific twist: the
// vol-0-first partial loads keep their 1 KB probe header as the image's header,
// so the label must be right on those paths too, not just in the full parse.
public class NiftiFormatLabelTests
{
    const int VolumeBytes = 4 * 4 * 4 * 2; // 4×4×4 int16

    static byte[] Nifti(int version, int volumes)
    {
        var hdrSize = version == 1 ? 348 : 540;
        var offset = version == 1 ? 352 : 544;
        var file = new byte[offset + volumes * VolumeBytes];
        var h = file.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(h, hdrSize);
        var ndim = volumes > 1 ? 4 : 3;
        if (version == 1)
        {
            short[] dims = [(short)ndim, 4, 4, 4, (short)volumes];
            for (var i = 0; i < dims.Length; i++)
                BinaryPrimitives.WriteInt16LittleEndian(h[(40 + i * 2)..], dims[i]);
            BinaryPrimitives.WriteInt16LittleEndian(h[70..], (short)MiqDatatype.Int16);
            BinaryPrimitives.WriteInt16LittleEndian(h[72..], 16);
            for (var i = 0; i < 8; i++)
                BinaryPrimitives.WriteSingleLittleEndian(h[(76 + i * 4)..], 1f);
            BinaryPrimitives.WriteSingleLittleEndian(h[108..], offset);
        }
        else
        {
            BinaryPrimitives.WriteInt16LittleEndian(h[12..], (short)MiqDatatype.Int16);
            BinaryPrimitives.WriteInt16LittleEndian(h[14..], 16);
            long[] dims = [ndim, 4, 4, 4, volumes, 1, 1, 1];
            for (var i = 0; i < dims.Length; i++)
                BinaryPrimitives.WriteInt64LittleEndian(h[(16 + i * 8)..], dims[i]);
            for (var i = 0; i < 4; i++)
                BinaryPrimitives.WriteDoubleLittleEndian(h[(104 + i * 8)..], 1d);
            BinaryPrimitives.WriteInt64LittleEndian(h[168..], offset);
        }
        return file;
    }

    static byte[] Gzip(byte[] raw)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            gz.Write(raw);
        return ms.ToArray();
    }

    /// Writes <paramref name="bytes"/> to a temp file with the given suffix, runs
    /// <paramref name="parse"/> on it, and returns the image's format label.
    static string? LabelOf(byte[] bytes, string suffix, Func<string, MiqImage> parse,
                           bool expectPartial = false)
    {
        var path = Path.Combine(Path.GetTempPath(), $"miq-label-{Guid.NewGuid():N}{suffix}");
        File.WriteAllBytes(path, bytes);
        try
        {
            var image = parse(path);
            Assert.Equal(expectPartial, image.IsPartial); // proves which path ran
            return image.Header.FormatLabel;
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(2, ".nii", "NIfTI-2")]
    [InlineData(2, ".nii.gz", "Compressed NIfTI-2")]
    [InlineData(1, ".nii", null)] // null → MiqFileKind display name "NIfTI-1"
    [InlineData(1, ".nii.gz", "Compressed NIfTI-1")]
    public void FullParse(int version, string suffix, string? expected)
    {
        var raw = Nifti(version, volumes: 1);
        var bytes = suffix == ".nii.gz" ? Gzip(raw) : raw;
        Assert.Equal(expected, LabelOf(bytes, suffix, path => MiqParser.Parse(path)));
    }

    [Theory]
    [InlineData(2, "Compressed NIfTI-2")]
    [InlineData(1, "Compressed NIfTI-1")]
    public void PartialGzipLoad(int version, string expected)
    {
        var bytes = Gzip(Nifti(version, volumes: MiqParser.PartialGzipMinVolumes));
        Assert.Equal(expected, LabelOf(bytes, ".nii.gz", MiqParser.ParsePartial, expectPartial: true));
    }

    [Theory]
    [InlineData(2, "NIfTI-2")]
    [InlineData(1, null)]
    public void PartialUncompressedLoad(int version, string? expected)
    {
        var saved = MiqParser.PartialLoadThreshold;
        MiqParser.PartialLoadThreshold = 0; // force the vol-0-first path on a tiny file
        try
        {
            Assert.Equal(expected,
                LabelOf(Nifti(version, volumes: 2), ".nii", MiqParser.ParsePartial, expectPartial: true));
        }
        finally { MiqParser.PartialLoadThreshold = saved; }
    }
}
