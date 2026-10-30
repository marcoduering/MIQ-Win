using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using MIQ.Parsing;
using MIQ.Rendering;
using Xunit;

namespace MIQ.Tests;

// Split formats: .mhd + .raw/.zraw, .nhdr + .raw/.raw.gz, .hdr + .img/.img.gz.
// Each test writes its pair into a fresh temp folder. Guards are tested with a
// neighbouring input they must still accept (see ParserGuardTests).
public sealed class DetachedDataTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"miq-split-{Guid.NewGuid():N}");

    public DetachedDataTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // ── Fixtures ────────────────────────────────────────────────────────────

    static readonly int[] Sizes = [4, 3, 2];

    /// int16 little-endian, element i = 3i − 7, x fastest.
    static byte[] Voxels(bool bigEndian = false)
    {
        var n = Sizes.Aggregate(1, (a, b) => a * b);
        var raw = new byte[n * 2];
        for (var i = 0; i < n; i++)
        {
            var span = raw.AsSpan(i * 2, 2);
            if (bigEndian) BinaryPrimitives.WriteInt16BigEndian(span, (short)(3 * i - 7));
            else BinaryPrimitives.WriteInt16LittleEndian(span, (short)(3 * i - 7));
        }
        return raw;
    }

    static void AssertVoxels(MiqImage image)
    {
        var volume = new MiqVolume(image);
        var (w, h, d) = (Sizes[0], Sizes[1], Sizes[2]);
        for (var z = 0; z < d; z++)
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
            Assert.Equal(3 * (x + w * (y + h * z)) - 7, volume.VoxelValue(x, y, z, 0));
    }

    static byte[] Gzip(byte[] raw)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true)) gz.Write(raw);
        return ms.ToArray();
    }

    string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    string WriteText(string name, string text) => Write(name, Encoding.ASCII.GetBytes(text));

    static string Mhd(string dataFile, bool compressed = false, string? extra = null) =>
        "ObjectType = Image\nNDims = 3\nBinaryData = True\nBinaryDataByteOrderMSB = False\n"
        + $"CompressedData = {(compressed ? "True" : "False")}\n"
        + "TransformMatrix = 1 0 0 0 1 0 0 0 1\nElementSpacing = 1 1 1\n"
        + extra
        + "DimSize = 4 3 2\nElementType = MET_SHORT\n"
        + $"ElementDataFile = {dataFile}\n";

    static string Nhdr(string dataFile, string encoding = "raw", string? extra = null) =>
        "NRRD0004\ntype: int16\ndimension: 3\nspace: left-posterior-superior\nsizes: 4 3 2\n"
        + "space directions: (1,0,0) (0,1,0) (0,0,1)\nendian: little\n"
        + $"encoding: {encoding}\n" + extra + $"data file: {dataFile}\n";

    /// 348-byte header. <paramref name="nifti"/> writes the `ni1` magic and an
    /// sform; otherwise plain Analyze 7.5 with junk where the sform would be.
    static byte[] AnalyzeHeader(bool nifti, float voxOffset = 0)
    {
        var h = new byte[348];
        BinaryPrimitives.WriteInt32LittleEndian(h, 348);
        short[] dim = [3, 4, 3, 2, 1, 1, 1, 1];
        for (var i = 0; i < 8; i++) BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(40 + 2 * i), dim[i]);
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(70), 4);   // DT_SIGNED_SHORT
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(72), 16);
        for (var i = 0; i < 4; i++) BinaryPrimitives.WriteSingleLittleEndian(h.AsSpan(76 + 4 * i), 1f);
        BinaryPrimitives.WriteSingleLittleEndian(h.AsSpan(108), voxOffset);
        if (nifti)
        {
            BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(254), 1); // sform_code
            // LAS: x points left.
            BinaryPrimitives.WriteSingleLittleEndian(h.AsSpan(280), -1f);
            BinaryPrimitives.WriteSingleLittleEndian(h.AsSpan(296 + 4), 1f);
            BinaryPrimitives.WriteSingleLittleEndian(h.AsSpan(312 + 8), 1f);
            "ni1\0"u8.CopyTo(h.AsSpan(344));
        }
        else
        {
            // Analyze's orient/originator bytes overlap qform_code/sform_code.
            for (var i = 252; i < 344; i++) h[i] = 0x11;
        }
        return h;
    }

    // ── MetaImage ───────────────────────────────────────────────────────────

    [Fact]
    public void Mhd_ReadsRawSibling_FromHeaderAndFromDataFile()
    {
        var mhd = WriteText("brain.mhd", Mhd("brain.raw"));
        var raw = Write("brain.raw", Voxels());

        var image = MiqParser.Parse(mhd);
        AssertVoxels(image);
        Assert.Equal("MetaImage (detached)", image.Header.FormatLabel);
        // Identity in ITK's LPS world.
        Assert.Equal("LPS", image.Header.OrientationFrame!.Label);

        Assert.Equal(mhd, DetachedData.HeaderFor(raw));
        AssertVoxels(MiqParser.ParsePartial(raw));
    }

    [Fact]
    public void Mhd_CompressedZraw()
    {
        var mhd = WriteText("c.mhd", Mhd("c.zraw", compressed: true));
        Write("c.zraw", MetaImageTests.Zlib(Voxels()));
        var image = MiqParser.Parse(mhd);
        AssertVoxels(image);
        Assert.Equal("Compressed MetaImage (detached)", image.Header.FormatLabel);
    }

    [Theory]
    [InlineData("HeaderSize = 5\n", 5)]
    [InlineData("HeaderSize = -1\n", 9)]
    public void Mhd_HeaderSizeSkipsPrefix(string field, int prefix)
    {
        var mhd = WriteText("s.mhd", Mhd("s.raw", extra: field));
        Write("s.raw", [.. new byte[prefix], .. Voxels()]);
        AssertVoxels(MiqParser.Parse(mhd));
    }

    [Fact]
    public void Mhd_InSubfolder_IsAccepted_ButEscapingFolderIsRejected()
    {
        Write(Path.Combine("data", "v.raw"), Voxels());
        AssertVoxels(MiqParser.Parse(WriteText("ok.mhd", Mhd("data/v.raw"))));

        Write("outside.raw", Voxels());
        var nested = WriteText(Path.Combine("sub", "bad.mhd"), Mhd("../outside.raw"));
        var e = Assert.Throws<MiqException>(() => MiqParser.Parse(nested));
        Assert.Contains("outside", e.Message);
    }

    [Theory]
    [InlineData(@"C:\Windows\win.ini")]
    [InlineData(@"\\server\share\v.raw")]
    public void Mhd_RootedOrUncDataFile_IsRejected(string dataFile)
    {
        var mhd = WriteText("abs.mhd", Mhd(dataFile));
        var e = Assert.Throws<MiqException>(() => MiqParser.Parse(mhd));
        Assert.Contains("relative path", e.Message);
    }

    [Fact]
    public void Mhd_MissingDataFile_SaysSo()
    {
        var mhd = WriteText("gone.mhd", Mhd("gone.raw"));
        var e = Assert.Throws<MiqException>(() => MiqParser.Parse(mhd));
        Assert.Contains("not found", e.Message);
    }

    [Fact]
    public void Mhd_ListDataFile_IsRejected()
    {
        var mhd = WriteText("list.mhd", Mhd("LIST"));
        var e = Assert.Throws<MiqException>(() => MiqParser.Parse(mhd));
        Assert.Contains("several data files", e.Message);
    }

    [Fact]
    public void Mha_WithSeparateDataFile_IsStillRejected()
    {
        Write("x.raw", Voxels());
        var mha = WriteText("x.mha", Mhd("x.raw"));
        var e = Assert.Throws<MiqException>(() => MiqParser.Parse(mha));
        Assert.Contains("separate data file", e.Message);
    }

    // ── NRRD ────────────────────────────────────────────────────────────────

    [Fact]
    public void Nhdr_RawWithoutBlankLine_AndByteSkip()
    {
        var nhdr = WriteText("n.nhdr", Nhdr("n.raw", extra: "byte skip: 3\n"));
        var raw = Write("n.raw", [1, 2, 3, .. Voxels()]);

        var image = MiqParser.Parse(nhdr);
        AssertVoxels(image);
        Assert.Equal("NRRD (detached)", image.Header.FormatLabel);
        Assert.Equal(nhdr, DetachedData.HeaderFor(raw));
    }

    [Fact]
    public void Nhdr_ByteSkipMinusOne_TakesTail_AndLineSkipSkipsLines()
    {
        WriteText("t.nhdr", Nhdr("t.raw", extra: "byte skip: -1\n"));
        Write("t.raw", [.. new byte[17], .. Voxels()]);
        AssertVoxels(MiqParser.Parse(Path.Combine(_dir, "t.nhdr")));

        WriteText("l.nhdr", Nhdr("l.raw", extra: "line skip: 2\n"));
        Write("l.raw", [.. "a\nbb\n"u8.ToArray(), .. Voxels()]);
        AssertVoxels(MiqParser.Parse(Path.Combine(_dir, "l.nhdr")));
    }

    [Fact]
    public void Nhdr_GzipDataFile()
    {
        var nhdr = WriteText("g.nhdr", Nhdr("g.raw.gz", encoding: "gzip"));
        var gz = Write("g.raw.gz", Gzip(Voxels()));
        var image = MiqParser.Parse(nhdr);
        AssertVoxels(image);
        Assert.Equal("Compressed NRRD (detached)", image.Header.FormatLabel);
        Assert.Equal(nhdr, DetachedData.HeaderFor(gz));
    }

    [Fact]
    public void Nhdr_FormatStringDataFile_IsRejected()
    {
        var nhdr = WriteText("f.nhdr", Nhdr("f%03d.raw 1 2 1"));
        var e = Assert.Throws<MiqException>(() => MiqParser.Parse(nhdr));
        Assert.Contains("several data files", e.Message);
    }

    [Fact]
    public void Nrrd_WithDataFile_IsStillRejected()
    {
        Write("y.raw", Voxels());
        var nrrd = WriteText("y.nrrd", Nhdr("y.raw") + "\n");
        var e = Assert.Throws<MiqException>(() => MiqParser.Parse(nrrd));
        Assert.Contains("detached header", e.Message);
    }

    // ── Analyze / NIfTI-1 pair ──────────────────────────────────────────────

    [Fact]
    public void Analyze_HasNoOrientation_AndReadsImg()
    {
        var hdr = Write("a.hdr", AnalyzeHeader(nifti: false));
        var img = Write("a.img", Voxels());

        var image = MiqParser.Parse(hdr);
        AssertVoxels(image);
        Assert.Equal("Analyze 7.5", image.Header.FormatLabel);
        Assert.Null(image.Header.OrientationFrame);

        Assert.Equal(hdr, DetachedData.HeaderFor(img));
        AssertVoxels(MiqParser.Parse(img));
    }

    [Fact]
    public void NiftiPair_UsesSform_AndVoxOffsetIntoImg()
    {
        var hdr = Write("p.hdr", AnalyzeHeader(nifti: true, voxOffset: 8));
        Write("p.img", [.. new byte[8], .. Voxels()]);

        var image = MiqParser.Parse(hdr);
        AssertVoxels(image);
        Assert.Equal("NIfTI-1 pair", image.Header.FormatLabel);
        Assert.Equal("LAS", image.Header.OrientationFrame!.Label);
    }

    [Fact]
    public void Analyze_GzippedImg()
    {
        var hdr = Write("z.hdr", AnalyzeHeader(nifti: false));
        Write("z.img.gz", Gzip(Voxels()));
        var image = MiqParser.Parse(hdr);
        AssertVoxels(image);
        Assert.Equal("Analyze 7.5, compressed .img", image.Header.FormatLabel);
    }

    [Fact]
    public void Analyze_TruncatedImg_IsRejected()
    {
        var hdr = Write("tr.hdr", AnalyzeHeader(nifti: false));
        Write("tr.img", Voxels()[..^1]);
        Assert.Throws<MiqException>(() => MiqParser.Parse(hdr));
    }

    // ── Claiming data files ─────────────────────────────────────────────────

    [Fact]
    public void RadianceHdr_IsNotAnalyze()
    {
        var radiance = WriteText("sky.hdr", "#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y 2 +X 2\n");
        Assert.False(DetachedData.LooksLikeAnalyzeHeader(radiance));
        Assert.True(DetachedData.LooksLikeAnalyzeHeader(Write("a.hdr", AnalyzeHeader(nifti: false))));
    }

    [Theory]
    [InlineData(40, 0)]      // dim[0] = 0
    [InlineData(70, 1)]      // DT_BINARY (1-bit): not a datatype the parser reads
    [InlineData(72, 8)]      // bitpix disagrees with int16
    public void HdrStartingWith348_ButInvalid_IsNotAnalyze(int offset, short value)
    {
        var good = AnalyzeHeader(nifti: false);
        Assert.True(DetachedData.LooksLikeAnalyzeHeader(Write("good.hdr", good)));

        var bad = (byte[])good.Clone();
        BinaryPrimitives.WriteInt16LittleEndian(bad.AsSpan(offset), value);
        var hdr = Write("bad.hdr", bad);
        Assert.False(DetachedData.LooksLikeAnalyzeHeader(hdr));
        Assert.Null(DetachedData.HeaderFor(Write("bad.img", Voxels())));
    }

    [Fact]
    public void ShortHdr_IsNotAnalyze()
    {
        Assert.False(DetachedData.LooksLikeAnalyzeHeader(Write("short.hdr", AnalyzeHeader(nifti: false)[..347])));
    }

    [Fact]
    public void ImgGz_IsClaimed_OnlyWhenNoPlainImg()
    {
        var hdr = Write("g.hdr", AnalyzeHeader(nifti: false));
        var gz = Write("g.img.gz", Gzip(Voxels()));
        Assert.Equal(hdr, DetachedData.HeaderFor(gz));

        // With both present the header reads g.img, so g.img.gz isn't its data.
        var img = Write("g.img", Voxels());
        Assert.Equal(hdr, DetachedData.HeaderFor(img));
        Assert.Null(DetachedData.HeaderFor(gz));
    }

    [Theory]
    [InlineData("/v.raw")]
    [InlineData("C:v.raw")]
    [InlineData("a|b.raw")]
    [InlineData("a\"b.raw")]
    [InlineData("a\0b.raw")] // the one name net8's path APIs also reject (ArgumentException)
    public void OddDataFileNames_FailWithAMessage(string dataFile)
    {
        // net462 throws ArgumentException for several of these; it must surface as
        // a MiqException either way, never a raw BCL exception.
        var mhd = WriteText("odd.mhd", Mhd(dataFile));
        Assert.Throws<MiqException>(() => MiqParser.Parse(mhd));
    }

    [Fact]
    public void DataFile_WithoutClaimingHeader_IsNotOurs()
    {
        // No sibling header at all.
        Assert.Null(DetachedData.HeaderFor(Write("lonely.raw", Voxels())));
        Assert.Null(DetachedData.HeaderFor(Write("disk.img", Voxels())));
        // A sibling .mhd that names another file.
        WriteText("other.mhd", Mhd("different.raw"));
        var raw = Write("other.raw", Voxels());
        Assert.Null(DetachedData.HeaderFor(raw));
        Assert.Throws<MiqException>(() => MiqParser.Parse(raw));
        // .img next to a non-Analyze .hdr (e.g. Radiance).
        WriteText("photo.hdr", "#?RADIANCE\n\n");
        Assert.Null(DetachedData.HeaderFor(Write("photo.img", Voxels())));
    }
}
