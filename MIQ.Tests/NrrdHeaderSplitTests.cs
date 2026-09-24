using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using MIQ.Parsing;
using MIQ.Rendering;
using Xunit;

namespace MIQ.Tests;

// The NRRD header ends at its *earliest* blank line. Payload bytes that happen to
// spell a blank line must never be mistaken for it. Port of MIQ 1.5.1's
// NRRD header-split fix (MIQ@d6dabd5).
//
// The realistic trigger: Slicer/ITK write LF headers, and the old split searched
// the whole file for \r\n\r\n *before* \n\n — so two adjacent int16 LE voxels of
// value 2573 (0x0A0D → bytes 0D 0A 0D 0A) hijacked the split.
public class NrrdHeaderSplitTests
{
    const short Hijack = 2573; // 0x0A0D: little-endian bytes 0D 0A

    /// 4×4×1 int16 voxels; voxels 5 and 6 hold 2573 so the payload contains
    /// 0D 0A 0D 0A.
    static short[] Voxels()
    {
        var v = new short[16];
        for (var i = 0; i < v.Length; i++) v[i] = (short)(i * 10);
        v[5] = Hijack;
        v[6] = Hijack;
        return v;
    }

    static byte[] VoxelBytes(short[] voxels)
    {
        var b = new byte[voxels.Length * 2];
        for (var i = 0; i < voxels.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(i * 2), voxels[i]);
        return b;
    }

    static byte[] Nrrd(string encoding, byte[] payload, string eol = "\n")
    {
        var header = string.Join(eol,
            "NRRD0004", "type: int16", "dimension: 3", "sizes: 4 4 1",
            $"encoding: {encoding}", "endian: little", "", "");
        return [.. Encoding.ASCII.GetBytes(header), .. payload];
    }

    /// gzip of <paramref name="raw"/> with an FCOMMENT field carrying
    /// <paramref name="comment"/>, which lands verbatim in the compressed bytes.
    static byte[] GzipWithComment(byte[] raw, byte[] comment)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            gz.Write(raw);
        var plain = ms.ToArray();
        var withComment = new List<byte>(plain.Length + comment.Length + 1);
        withComment.AddRange(plain.AsSpan(0, 10).ToArray());
        withComment[3] |= 0x10; // FLG.FCOMMENT
        withComment.AddRange(comment);
        withComment.Add(0);     // zero-terminated
        withComment.AddRange(plain.AsSpan(10).ToArray());
        return [.. withComment];
    }

    static void AssertVoxels(MiqImage image, short[] expected)
    {
        var vol = new MiqVolume(image);
        for (var i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], vol.VoxelValue(i % 4, i / 4, 0, 0));
    }

    [Fact]
    public void LfHeader_PayloadWithCrlfBlankLine_SplitsAtHeader()
    {
        var voxels = Voxels();
        var payload = VoxelBytes(voxels);
        var data = Nrrd("raw", payload);

        var image = NrrdParser.Parse(data);

        Assert.Equal(data.Length - payload.Length, image.PayloadOffset);
        AssertVoxels(image, voxels);
    }

    [Fact]
    public void LfHeader_PayloadWithLfBlankLine_SplitsAtHeader()
    {
        var voxels = Voxels();
        voxels[5] = 0x0A0A; // bytes 0A 0A
        var payload = VoxelBytes(voxels);
        var data = Nrrd("raw", payload);

        Assert.Equal(data.Length - payload.Length, NrrdParser.Parse(data).PayloadOffset);
    }

    [Fact]
    public void CrlfHeader_StillParses()
    {
        var voxels = Voxels();
        var payload = VoxelBytes(voxels);
        var data = Nrrd("raw", payload, eol: "\r\n");

        var image = NrrdParser.Parse(data);

        Assert.Equal(data.Length - payload.Length, image.PayloadOffset);
        AssertVoxels(image, voxels);
    }

    [Fact]
    public void GzipPayloadContainingCrlfBlankLine_SplitsAtHeader()
    {
        // Splitting inside the compressed stream used to fail as
        // "gzip magic bytes are missing".
        var voxels = Voxels();
        var gz = GzipWithComment(VoxelBytes(voxels), [0x0D, 0x0A, 0x0D, 0x0A]);

        AssertVoxels(NrrdParser.Parse(Nrrd("gzip", gz)), voxels);
    }

    [Fact]
    public void MissingSeparator_Throws() =>
        Assert.Throws<MiqException>(() => NrrdParser.Parse(
            Encoding.ASCII.GetBytes("NRRD0004\ntype: uint8\nsizes: 4 4 1\n")));
}
