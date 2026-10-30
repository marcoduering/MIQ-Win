using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using MIQ.Parsing;
using MIQ.Rendering;
using Xunit;

namespace MIQ.Tests;

// Volume-0-first load of split pairs. Lowers the process-wide MiqParser seams, so
// it shares the non-parallel collection with the other seam tests.
[Collection(nameof(StaticParserSeams))]
public sealed class DetachedPartialLoadTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"miq-split4d-{Guid.NewGuid():N}");
    private readonly long _savedThreshold = MiqParser.PartialLoadThreshold;
    private readonly long _savedMaxArray = MiqParser.MaxArrayBytes;

    public DetachedPartialLoadTests()
    {
        Directory.CreateDirectory(_dir);
        MiqParser.PartialLoadThreshold = 0; // every 4-D fixture is "large"
    }

    public void Dispose()
    {
        MiqParser.PartialLoadThreshold = _savedThreshold;
        MiqParser.MaxArrayBytes = _savedMaxArray;
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // ── Fixtures: 4×3×2 voxels × 3 volumes, int16, element i = 3i − 7 ──────

    const int W = 4, H = 3, D = 2, T = 3;
    const int VolumeBytes = W * H * D * 2;

    static byte[] Voxels()
    {
        var raw = new byte[VolumeBytes * T];
        for (var i = 0; i < raw.Length / 2; i++)
            BinaryPrimitives.WriteInt16LittleEndian(raw.AsSpan(i * 2), (short)(3 * i - 7));
        return raw;
    }

    string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    string WriteText(string name, string text) => Write(name, Encoding.ASCII.GetBytes(text));

    static string Mhd(string dataFile, string? extra = null, bool compressed = false) =>
        "ObjectType = Image\nNDims = 4\nBinaryData = True\nBinaryDataByteOrderMSB = False\n"
        + $"CompressedData = {(compressed ? "True" : "False")}\n"
        + "TransformMatrix = 1 0 0 0 0 1 0 0 0 0 1 0 0 0 0 1\nElementSpacing = 1 1 1 1\n"
        + extra + $"DimSize = {W} {H} {D} {T}\nElementType = MET_SHORT\nElementDataFile = {dataFile}\n";

    static string Nhdr(string dataFile, string sizes = "4 3 2 3",
        string directions = "(1,0,0) (0,1,0) (0,0,1) none", string encoding = "raw", string? extra = null) =>
        $"NRRD0004\ntype: int16\ndimension: 4\nspace: right-anterior-superior\nsizes: {sizes}\n"
        + $"space directions: {directions}\nendian: little\nencoding: {encoding}\n"
        + extra + $"data file: {dataFile}\n";

    static byte[] AnalyzeHeader(float voxOffset = 0)
    {
        var h = new byte[348];
        BinaryPrimitives.WriteInt32LittleEndian(h, 348);
        short[] dim = [4, W, H, D, T, 1, 1, 1];
        for (var i = 0; i < 8; i++) BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(40 + 2 * i), dim[i]);
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(70), 4);
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(72), 16);
        for (var i = 0; i < 4; i++) BinaryPrimitives.WriteSingleLittleEndian(h.AsSpan(76 + 4 * i), 1f);
        BinaryPrimitives.WriteSingleLittleEndian(h.AsSpan(108), voxOffset);
        return h;
    }

    /// The partial image must show volume 0 exactly as the full parse does.
    static void AssertMatchesFullVolume0(MiqImage partial, MiqImage full)
    {
        Assert.True(partial.IsPartial);
        Assert.False(full.IsPartial);
        Assert.Equal(full.Header.Dimensions, partial.Header.Dimensions);
        Assert.Equal(full.Header.VoxOffset, partial.Header.VoxOffset);
        Assert.Equal(full.PayloadOffset, partial.PayloadOffset);
        Assert.Equal(full.Header.FormatLabel, partial.Header.FormatLabel);
        Assert.Equal(full.Header.OrientationFrame?.Label, partial.Header.OrientationFrame?.Label);
        Assert.Equal(full.Header.Pixdim, partial.Header.Pixdim);
        Assert.Equal(partial.PayloadOffset + VolumeBytes, partial.Storage.Length);

        var p = new MiqVolume(partial);
        var f = new MiqVolume(full);
        for (var z = 0; z < D; z++)
        for (var y = 0; y < H; y++)
        for (var x = 0; x < W; x++)
        {
            Assert.Equal(3 * (x + W * (y + H * z)) - 7, f.VoxelValue(x, y, z, 0));
            Assert.Equal(f.VoxelValue(x, y, z, 0), p.VoxelValue(x, y, z, 0));
        }
        // The full image really has the later volumes.
        Assert.Equal(3 * (W * H * D * (T - 1)) - 7, f.VoxelValue(0, 0, 0, T - 1));
    }

    // ── Volume-0-first ──────────────────────────────────────────────────────

    [Fact]
    public void Mhd_LoadsVolume0First_FromHeaderAndDataFile()
    {
        var mhd = WriteText("m.mhd", Mhd("m.raw"));
        var raw = Write("m.raw", Voxels());
        var full = MiqParser.Parse(mhd);
        AssertMatchesFullVolume0(MiqParser.ParsePartial(mhd), full);
        AssertMatchesFullVolume0(MiqParser.ParsePartial(raw), full);
    }

    [Theory]
    [InlineData("HeaderSize = 6\n", 6)]
    [InlineData("HeaderSize = -1\n", 11)]
    public void Mhd_HeaderSize_PlacesVolume0(string field, int prefix)
    {
        var mhd = WriteText("h.mhd", Mhd("h.raw", field));
        Write("h.raw", [.. new byte[prefix], .. Voxels()]);
        var partial = MiqParser.ParsePartial(mhd);
        Assert.Equal(prefix, partial.PayloadOffset);
        AssertMatchesFullVolume0(partial, MiqParser.Parse(mhd));
    }

    [Fact]
    public void Nhdr_LoadsVolume0First_WithByteSkip()
    {
        var nhdr = WriteText("n.nhdr", Nhdr("n.raw", extra: "byte skip: 5\n"));
        Write("n.raw", [.. new byte[5], .. Voxels()]);
        AssertMatchesFullVolume0(MiqParser.ParsePartial(nhdr), MiqParser.Parse(nhdr));
    }

    [Fact]
    public void Nhdr_TailOffset_IgnoresLineSkip_ForVolume0First()
    {
        var nhdr = WriteText("tail.nhdr",
            Nhdr("tail.raw", extra: "line skip: 100\nbyte skip: -1\n"));
        Write("tail.raw", [.. new byte[11], .. Voxels()]);
        var partial = MiqParser.ParsePartial(nhdr);
        Assert.Equal(11, partial.PayloadOffset);
        AssertMatchesFullVolume0(partial, MiqParser.Parse(nhdr));
    }

    [Fact]
    public void Analyze_LoadsVolume0First_WithVoxOffset()
    {
        var hdr = Write("a.hdr", AnalyzeHeader(voxOffset: 8));
        var img = Write("a.img", [.. new byte[8], .. Voxels()]);
        var full = MiqParser.Parse(hdr);
        AssertMatchesFullVolume0(MiqParser.ParsePartial(hdr), full);
        AssertMatchesFullVolume0(MiqParser.ParsePartial(img), full);
    }

    [Fact]
    public void BelowThreshold_LoadsFully()
    {
        MiqParser.PartialLoadThreshold = VolumeBytes * T; // file length is not above it
        var mhd = WriteText("s.mhd", Mhd("s.raw"));
        Write("s.raw", Voxels());
        Assert.False(MiqParser.ParsePartial(mhd).IsPartial);
        MiqParser.PartialLoadThreshold = VolumeBytes * T - 1;
        Assert.True(MiqParser.ParsePartial(mhd).IsPartial);
    }

    [Fact]
    public void DataFileBeyondMaxArray_ShowsVolume0Blocked()
    {
        var mhd = WriteText("b.mhd", Mhd("b.raw"));
        Write("b.raw", Voxels());
        MiqParser.MaxArrayBytes = VolumeBytes * T - 1; // whole file can't be held, volume 0 can

        var partial = MiqParser.ParsePartial(mhd);
        Assert.True(partial.IsPartial);
        Assert.True(partial.ExpansionBlocked);
        Assert.Throws<MiqException>(() => MiqParser.Parse(mhd)); // the full load really can't

        MiqParser.MaxArrayBytes = VolumeBytes * T;
        Assert.False(MiqParser.ParsePartial(mhd).ExpansionBlocked);
    }

    // ── Falls back to a full parse ──────────────────────────────────────────

    [Fact]
    public void CompressedData_LoadsFully()
    {
        var mhd = WriteText("z.mhd", Mhd("z.zraw", compressed: true));
        Write("z.zraw", MetaImageTests.Zlib(Voxels()));
        Assert.False(MiqParser.ParsePartial(mhd).IsPartial);

        var nhdr = WriteText("g.nhdr", Nhdr("g.raw.gz", encoding: "gzip"));
        using (var ms = new MemoryStream())
        {
            using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true)) gz.Write(Voxels());
            Write("g.raw.gz", ms.ToArray());
        }
        Assert.False(MiqParser.ParsePartial(nhdr).IsPartial);

        Write("c.hdr", AnalyzeHeader());
        using (var ms = new MemoryStream())
        {
            using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true)) gz.Write(Voxels());
            Write("c.img.gz", ms.ToArray());
        }
        Assert.False(MiqParser.ParsePartial(Path.Combine(_dir, "c.hdr")).IsPartial);
    }

    [Fact]
    public void InterleavedVolumes_LoadFully()
    {
        // NRRD list axis first (as DWI files are): volumes are interleaved per voxel.
        var nhdr = WriteText("i.nhdr", Nhdr("i.raw", sizes: "3 4 3 2", directions: "none (1,0,0) (0,1,0) (0,0,1)"));
        Write("i.raw", Voxels());
        var image = MiqParser.ParsePartial(nhdr);
        Assert.False(image.IsPartial);
        Assert.Equal(T, image.Header.Volumes);
    }

    [Fact]
    public void LineSkip_LoadsFully()
    {
        var nhdr = WriteText("l.nhdr", Nhdr("l.raw", extra: "line skip: 1\n"));
        Write("l.raw", [.. "x\n"u8.ToArray(), .. Voxels()]);
        Assert.False(MiqParser.ParsePartial(nhdr).IsPartial);
        // …and still reads correctly.
        Assert.Equal(-7, new MiqVolume(MiqParser.Parse(nhdr)).VoxelValue(0, 0, 0, 0));
    }

    [Fact]
    public void TruncatedDataFile_FallsBackToFullParse_WhichReportsIt()
    {
        var mhd = WriteText("t.mhd", Mhd("t.raw"));
        Write("t.raw", Voxels()[..(VolumeBytes - 1)]); // not even volume 0
        Assert.Throws<MiqException>(() => MiqParser.ParsePartial(mhd));
    }
}
