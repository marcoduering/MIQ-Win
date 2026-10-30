using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using MIQ.Parsing;
using MIQ.Rendering;
using Xunit;

namespace MIQ.Tests;

// ITK MetaImage (.mha). Port of MIQ's MetaImageTests (metaimage-mha branch,
// MIQ@8c7f384), minus the macOS-only thumbnail gate. Each guard is tested
// together with a neighbouring input it must still accept (see ParserGuardTests).
public class MetaImageTests
{
    // ── Fixtures ────────────────────────────────────────────────────────────

    /// Sample for element <paramref name="i"/>: distinct, exactly representable
    /// in every datatype, negative where the type allows.
    static double Sample(int i, MiqDatatype dt) => dt switch
    {
        MiqDatatype.Uint8 or MiqDatatype.Rgb24 or MiqDatatype.Rgba32 => i % 251,
        MiqDatatype.Int8 => i % 120 - 60,
        MiqDatatype.Float32 or MiqDatatype.Float64 => i * 0.5 - 3,
        MiqDatatype.Uint16 or MiqDatatype.Uint32 or MiqDatatype.Uint64 => i * 3,
        _ => i * 3 - 7,
    };

    static void Encode(double v, Span<byte> dst, MiqDatatype dt, bool bigEndian)
    {
        switch (dt)
        {
            case MiqDatatype.Uint8: dst[0] = (byte)v; return;
            case MiqDatatype.Int8: dst[0] = (byte)(sbyte)v; return;
        }
        Span<byte> le = stackalloc byte[8];
        var size = dt.BytesPerVoxel();
        switch (dt)
        {
            case MiqDatatype.Int16: BinaryPrimitives.WriteInt16LittleEndian(le, (short)v); break;
            case MiqDatatype.Uint16: BinaryPrimitives.WriteUInt16LittleEndian(le, (ushort)v); break;
            case MiqDatatype.Int32: BinaryPrimitives.WriteInt32LittleEndian(le, (int)v); break;
            case MiqDatatype.Uint32: BinaryPrimitives.WriteUInt32LittleEndian(le, (uint)v); break;
            case MiqDatatype.Int64: BinaryPrimitives.WriteInt64LittleEndian(le, (long)v); break;
            case MiqDatatype.Uint64: BinaryPrimitives.WriteUInt64LittleEndian(le, (ulong)v); break;
            case MiqDatatype.Float32: BinaryPrimitives.WriteSingleLittleEndian(le, (float)v); break;
            case MiqDatatype.Float64: BinaryPrimitives.WriteDoubleLittleEndian(le, v); break;
        }
        for (var b = 0; b < size; b++) dst[b] = le[bigEndian ? size - 1 - b : b];
    }

    static string TypeLabel(MiqDatatype dt) => dt switch
    {
        MiqDatatype.Int8 => "MET_CHAR",
        MiqDatatype.Uint8 => "MET_UCHAR",
        MiqDatatype.Int16 => "MET_SHORT",
        MiqDatatype.Uint16 => "MET_USHORT",
        MiqDatatype.Int32 => "MET_INT",
        MiqDatatype.Uint32 => "MET_UINT",
        MiqDatatype.Int64 => "MET_LONG_LONG",
        MiqDatatype.Uint64 => "MET_ULONG_LONG",
        MiqDatatype.Float32 => "MET_FLOAT",
        _ => "MET_DOUBLE",
    };

    internal static byte[] Zlib(byte[] raw)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(raw);
        return ms.ToArray();
    }

    /// MetaImage with the fields in the order ITK's MetaImageIO writes them.
    /// Elements are <see cref="Sample"/> in storage order, channels interleaved per
    /// voxel. <paramref name="transform"/> is the TransformMatrix value; null writes
    /// the identity, <paramref name="omitTransform"/> leaves the field out.
    internal static byte[] MetaImage(
        int[] sizes, MiqDatatype dt, int channels = 1, bool compressed = false,
        bool bigEndian = false, string? transform = null, bool omitTransform = false,
        string? spacing = null, string dataFile = "LOCAL", string lineEnding = "\n",
        string[]? extraFields = null)
    {
        var n = sizes.Length;
        var elements = sizes.Aggregate(1, (a, b) => a * b) * channels;
        var size = dt.BytesPerVoxel();
        var raw = new byte[elements * size];
        for (var i = 0; i < elements; i++)
            Encode(Sample(i, dt), raw.AsSpan(i * size, size), dt, bigEndian);
        var payload = compressed ? Zlib(raw) : raw;

        string Repeat(string v) => string.Join(" ", Enumerable.Repeat(v, n));
        var identity = string.Join(" ", Enumerable.Range(0, n * n).Select(k => k / n == k % n ? "1" : "0"));
        var lines = new[]
        {
            "ObjectType = Image",
            $"NDims = {n}",
            "BinaryData = True",
            $"BinaryDataByteOrderMSB = {(bigEndian ? "True" : "False")}",
            $"CompressedData = {(compressed ? "True" : "False")}",
            compressed ? $"CompressedDataSize = {payload.Length}" : null,
            omitTransform ? null : $"TransformMatrix = {transform ?? identity}",
            $"Offset = {Repeat("0")}",
            "AnatomicalOrientation = RAI",
            $"ElementSpacing = {spacing ?? Repeat("1")}",
            channels > 1 ? $"ElementNumberOfChannels = {channels}" : null,
            $"DimSize = {string.Join(" ", sizes)}",
            $"ElementType = {TypeLabel(dt)}",
        }.Where(l => l != null).Concat(extraFields ?? []).Append($"ElementDataFile = {dataFile}");
        var header = string.Join(lineEnding, lines) + lineEnding;
        return [.. Encoding.ASCII.GetBytes(header), .. payload];
    }

    /// Every voxel of a single-channel image against the sample at its x-fastest index.
    static void AssertVoxels(MiqImage image, int[] sizes, MiqDatatype dt)
    {
        var volume = new MiqVolume(image);
        var (w, h) = (sizes[0], sizes[1]);
        var d = sizes.Length > 2 ? sizes[2] : 1;
        var v = sizes.Length > 3 ? sizes[3] : 1;
        for (var t = 0; t < v; t++)
        for (var z = 0; z < d; z++)
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
            Assert.Equal((float)Sample(x + w * (y + h * (z + d * t)), dt), volume.VoxelValue(x, y, z, t));
    }

    static string Axes(MiqImage image) => image.Header.OrientationFrame!.Label;

    // ── Payload ─────────────────────────────────────────────────────────────

    [Fact]
    public void RawFile_ParsesThroughDispatch()
    {
        var path = Path.Combine(Path.GetTempPath(), $"miq-mha-{Guid.NewGuid():N}.MHA");
        File.WriteAllBytes(path, MetaImage([5, 4, 3], MiqDatatype.Int16, spacing: "0.5 2 3"));
        try
        {
            Assert.Equal(MiqFileKind.Mha, MiqFileKindExtensions.FromPath(path));
            var image = MiqParser.Parse(path);
            Assert.Equal([5, 4, 3, 1], image.Header.Dimensions);
            Assert.Equal(MiqDatatype.Int16, image.Header.Datatype);
            Assert.Equal([1f, 0.5f, 2f, 3f], image.Header.Pixdim);
            Assert.Null(image.Header.FormatLabel); // → kind display name "MetaImage"
            Assert.Null(image.ElementStrides);
            AssertVoxels(image, [5, 4, 3], MiqDatatype.Int16);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(MiqDatatype.Uint8)]
    [InlineData(MiqDatatype.Int8)]
    [InlineData(MiqDatatype.Int16)]
    [InlineData(MiqDatatype.Uint16)]
    [InlineData(MiqDatatype.Int32)]
    [InlineData(MiqDatatype.Uint32)]
    [InlineData(MiqDatatype.Float32)]
    [InlineData(MiqDatatype.Float64)]
    [InlineData(MiqDatatype.Int64)]
    [InlineData(MiqDatatype.Uint64)]
    public void CompressedAndBigEndian_DecodeLikeRaw(MiqDatatype dt)
    {
        int[] sizes = [4, 3, 2];
        foreach (var (compressed, bigEndian) in new[] { (false, false), (false, true), (true, false), (true, true) })
        {
            var image = MetaImageParser.Parse(MetaImage(sizes, dt, compressed: compressed, bigEndian: bigEndian));
            Assert.Equal(!bigEndian, image.Header.LittleEndian);
            Assert.Equal(compressed ? "Compressed MetaImage" : null, image.Header.FormatLabel);
            AssertVoxels(image, sizes, dt);
        }
    }

    // MET_LONG is 4 bytes in MetaIO, not the platform long.
    [Fact]
    public void MetLong_IsFourBytes()
    {
        // Later fields win, so this overrides the fixture's MET_INT.
        var image = MetaImageParser.Parse(
            MetaImage([4, 3, 2], MiqDatatype.Int32, extraFields: ["ElementType = MET_LONG"]));
        Assert.Equal(MiqDatatype.Int32, image.Header.Datatype);
        AssertVoxels(image, [4, 3, 2], MiqDatatype.Int32);
    }

    [Fact]
    public void CrlfHeader_Parses() =>
        AssertVoxels(MetaImageParser.Parse(MetaImage([4, 3, 2], MiqDatatype.Uint8, lineEnding: "\r\n")),
            [4, 3, 2], MiqDatatype.Uint8);

    [Fact]
    public void FourDimensionalImage_HasVolumes()
    {
        int[] sizes = [4, 3, 2, 3];
        var image = MetaImageParser.Parse(MetaImage(sizes, MiqDatatype.Float32, compressed: true));
        Assert.Equal([4, 3, 2, 3], image.Header.Dimensions);
        Assert.Null(image.ElementStrides);
        AssertVoxels(image, sizes, MiqDatatype.Float32);
    }

    [Fact]
    public void TwoDimensionalImage_IsOneSlice()
    {
        var image = MetaImageParser.Parse(MetaImage([5, 4], MiqDatatype.Uint16));
        Assert.Equal([5, 4, 1, 1], image.Header.Dimensions);
        Assert.Null(image.Header.OrientationFrame);
        AssertVoxels(image, [5, 4], MiqDatatype.Uint16);
    }

    [Theory]
    [InlineData(3, MiqDatatype.Rgb24)]
    [InlineData(4, MiqDatatype.Rgba32)]
    public void ThreeOrFourByteChannels_AreColor(int channels, MiqDatatype expected)
    {
        var image = MetaImageParser.Parse(MetaImage([4, 3, 2], MiqDatatype.Uint8, channels: channels));
        Assert.Equal(expected, image.Header.Datatype);
        Assert.Equal(1, image.Header.Volumes);
        Assert.Null(image.ElementStrides);
    }

    [Fact]
    public void OtherChannels_AreVolumes()
    {
        var (w, h, d, c) = (4, 3, 2, 3);
        var image = MetaImageParser.Parse(MetaImage([w, h, d], MiqDatatype.Float32, channels: c));
        Assert.Equal([w, h, d, c], image.Header.Dimensions);
        Assert.Equal(new[] { c, c * w, c * w * h, 1 }, image.ElementStrides);
        var volume = new MiqVolume(image);
        for (var t = 0; t < c; t++)
        for (var z = 0; z < d; z++)
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
            Assert.Equal((float)Sample(t + c * (x + w * (y + h * z)), MiqDatatype.Float32),
                volume.VoxelValue(x, y, z, t));
    }

    // ── Orientation ─────────────────────────────────────────────────────────

    [Fact]
    public void IdentityTransform_IsLps()
    {
        var image = MetaImageParser.Parse(MetaImage([4, 3, 2], MiqDatatype.Uint8));
        Assert.Equal("LPS", Axes(image));
        Assert.Equal(1, image.Header.SformCode);
    }

    /// Each run of three values is one storage axis's direction (ITK's convention),
    /// so axis 0 → +S, axis 1 → +L, axis 2 → +P.
    [Fact]
    public void TransformRuns_AreAxisDirections() =>
        Assert.Equal("SLP", Axes(MetaImageParser.Parse(
            MetaImage([4, 3, 2], MiqDatatype.Uint8, transform: "0 0 1 1 0 0 0 1 0"))));

    [Fact]
    public void FourDimensionalTransform_UsesSpatialBlock() =>
        Assert.Equal("RPS", Axes(MetaImageParser.Parse(
            MetaImage([4, 3, 2, 2], MiqDatatype.Uint8, transform: "-1 0 0 0  0 1 0 0  0 0 1 0  0 0 0 1"))));

    [Fact]
    public void MissingTransform_BuildsNoFrameButKeepsSpacing()
    {
        var image = MetaImageParser.Parse(
            MetaImage([4, 3, 2], MiqDatatype.Uint8, omitTransform: true, spacing: "2 2 3"));
        Assert.Null(image.Header.OrientationFrame);
        Assert.Equal(0, image.Header.SformCode);
        Assert.Equal([1f, 2f, 2f, 3f], image.Header.Pixdim);
    }

    // A wrongly sized matrix is ignored, not fatal: the orientation is optional.
    [Fact]
    public void WrongSizeTransform_BuildsNoFrame() =>
        Assert.Null(MetaImageParser.Parse(
            MetaImage([4, 3, 2], MiqDatatype.Uint8, transform: "1 0 0 0 1 0")).Header.OrientationFrame);

    // ── Rejections ──────────────────────────────────────────────────────────

    [Fact]
    public void DetachedDataFile_IsRejected()
    {
        var e = Assert.Throws<MiqException>(() =>
            MetaImageParser.Parse(MetaImage([4, 3, 2], MiqDatatype.Uint8, dataFile: "image.raw")));
        Assert.Contains("separate data file", e.Message);
    }

    [Fact]
    public void AsciiData_IsRejected()
    {
        var e = Assert.Throws<MiqException>(() => MetaImageParser.Parse(
            MetaImage([4, 3, 2], MiqDatatype.Uint8, extraFields: ["BinaryData = False"])));
        Assert.Contains("ASCII", e.Message);
    }

    [Fact]
    public void FileWithoutElementDataFile_IsRejected()
    {
        byte[] data = [.. Encoding.ASCII.GetBytes(
            "ObjectType = Image\nNDims = 3\nDimSize = 2 2 2\nElementType = MET_UCHAR\n"), .. new byte[8]];
        var e = Assert.Throws<MiqException>(() => MetaImageParser.Parse(data));
        Assert.Contains("ElementDataFile", e.Message);
    }

    [Fact]
    public void NDimsMismatch_IsRejected()
    {
        var e = Assert.Throws<MiqException>(() => MetaImageParser.Parse(
            MetaImage([4, 3, 2], MiqDatatype.Uint8, extraFields: ["NDims = 4"])));
        Assert.Contains("NDims", e.Message);
    }

    [Fact]
    public void FourDimensionalMultiChannel_IsRejected_ButThreeDimensionalIsNot()
    {
        Assert.Throws<MiqException>(() =>
            MetaImageParser.Parse(MetaImage([2, 2, 2, 2], MiqDatatype.Float32, channels: 2)));
        MetaImageParser.Parse(MetaImage([2, 2, 2], MiqDatatype.Float32, channels: 2));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TruncatedPayload_Throws_ButCompletePayloadParses(bool compressed)
    {
        var data = MetaImage([8, 8, 8], MiqDatatype.Int16, compressed: compressed);
        MetaImageParser.Parse(data);
        Assert.Throws<MiqException>(() => MetaImageParser.Parse(data[..^7]));
    }

    [Fact]
    public void HugeDimensions_ThrowWithoutAllocating()
    {
        var data = Encoding.ASCII.GetBytes(
            $"ObjectType = Image\nNDims = 3\nDimSize = {int.MaxValue} {int.MaxValue} {int.MaxValue}\n"
            + "ElementType = MET_DOUBLE\nElementDataFile = LOCAL\n");
        Assert.Throws<MiqException>(() => MetaImageParser.Parse(data));
    }

    // zlib has no size trailer: a header claiming more than DEFLATE could ever
    // produce from the payload is rejected before the output is allocated.
    [Fact]
    public void ImplausibleCompressedSize_IsRejectedBeforeAllocating()
    {
        var bloated = MetaImage([8, 8, 8], MiqDatatype.Uint8, compressed: true,
            extraFields: ["DimSize = 1000 1000 1000"]);
        var e = Assert.Throws<MiqException>(() => MetaImageParser.Parse(bloated));
        Assert.Equal(MiqException.TruncatedData().Message, e.Message);
    }

    [Fact]
    public void GzipWrappedPayload_IsAccepted()
    {
        int[] sizes = [4, 3, 2];
        var zlib = MetaImage(sizes, MiqDatatype.Int16, compressed: true);
        var raw = MetaImage(sizes, MiqDatatype.Int16);
        var payload = raw[^(4 * 3 * 2 * 2)..];
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true)) gz.Write(payload);
        var headerEnd = Encoding.ASCII.GetString(zlib).IndexOf("ElementDataFile = LOCAL\n", StringComparison.Ordinal)
                        + "ElementDataFile = LOCAL\n".Length;
        var header = Encoding.ASCII.GetString(zlib, 0, headerEnd)
            .Replace($"CompressedDataSize = {zlib.Length - headerEnd}", $"CompressedDataSize = {ms.Length}");
        AssertVoxels(MetaImageParser.Parse([.. Encoding.ASCII.GetBytes(header), .. ms.ToArray()]),
            sizes, MiqDatatype.Int16);
    }

    // ── HeaderSize (MetaIO's M_ReadElements, which LOCAL data goes through too) ──

    const string LocalLine = "ElementDataFile = LOCAL\n";

    static int HeaderEnd(byte[] file) =>
        Encoding.ASCII.GetString(file).IndexOf(LocalLine, StringComparison.Ordinal) + LocalLine.Length;

    /// <paramref name="file"/> with <paramref name="padding"/> bytes of 0xEE between
    /// its header and payload.
    static byte[] Padded(byte[] file, int padding)
    {
        var end = HeaderEnd(file);
        return [.. file[..end], .. Enumerable.Repeat((byte)0xEE, padding), .. file[end..]];
    }

    /// A header whose `HeaderSize` points just past <paramref name="padding"/>
    /// bytes. Fixed-width digits keep the header length independent of the value.
    static byte[] PaddedWithHeaderSize(int[] sizes, MiqDatatype dt, int padding, bool compressed = false)
    {
        var placeholder = MetaImage(sizes, dt, compressed: compressed, extraFields: ["HeaderSize = 000000"]);
        var offset = HeaderEnd(placeholder) + padding;
        return Padded(MetaImage(sizes, dt, compressed: compressed, extraFields: [$"HeaderSize = {offset:D6}"]),
            padding);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PositiveHeaderSize_SkipsPaddingBeforePayload(bool compressed)
    {
        int[] sizes = [4, 3, 2];
        AssertVoxels(MetaImageParser.Parse(PaddedWithHeaderSize(sizes, MiqDatatype.Int16, 16, compressed)),
            sizes, MiqDatatype.Int16);
    }

    [Fact]
    public void HeaderSizeMinusOne_TakesRawPayloadFromTheTail()
    {
        int[] sizes = [4, 3, 2];
        var file = Padded(MetaImage(sizes, MiqDatatype.Int16, extraFields: ["HeaderSize = -1"]), 16);
        AssertVoxels(MetaImageParser.Parse(file), sizes, MiqDatatype.Int16);
    }

    // MetaIO only acts on > 0 and -1; anything else keeps the natural position.
    [Theory]
    [InlineData("HeaderSize = 0")]
    [InlineData("HeaderSize = -2")]
    public void OtherHeaderSizes_KeepPayloadAfterHeader(string field)
    {
        int[] sizes = [4, 3, 2];
        AssertVoxels(MetaImageParser.Parse(MetaImage(sizes, MiqDatatype.Int16, extraFields: [field])),
            sizes, MiqDatatype.Int16);
    }

    // -1 means "seek back by the payload size", which MetaIO does with the
    // decompressed size even for a zlib stream; that is meaningless, so it's ignored.
    [Fact]
    public void HeaderSizeMinusOne_IsIgnoredForCompressedData()
    {
        int[] sizes = [4, 3, 2];
        AssertVoxels(MetaImageParser.Parse(
                MetaImage(sizes, MiqDatatype.Int16, compressed: true, extraFields: ["HeaderSize = -1"])),
            sizes, MiqDatatype.Int16);
    }

    [Fact]
    public void HeaderSizePastEndOfFile_Throws_ButLastValidOffsetParses()
    {
        int[] sizes = [4, 3, 2];
        var file = PaddedWithHeaderSize(sizes, MiqDatatype.Int16, 16);
        MetaImageParser.Parse(file);
        var payloadStart = HeaderEnd(file) + 16;
        var beyond = Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(file)
            .Replace($"HeaderSize = {payloadStart:D6}", $"HeaderSize = {payloadStart + 1:D6}"));
        Assert.Throws<MiqException>(() => MetaImageParser.Parse(beyond));
    }

    // ── zlib integrity ───────────────────────────────────────────────────────

    /// <paramref name="file"/> (a compressed MetaImage) with its zlib stream
    /// replaced by <paramref name="stream"/>.
    static byte[] WithStream(byte[] file, byte[] stream)
    {
        var end = HeaderEnd(file);
        var header = System.Text.RegularExpressions.Regex.Replace(Encoding.ASCII.GetString(file, 0, end),
            @"CompressedDataSize = \d+", $"CompressedDataSize = {stream.Length}");
        return [.. Encoding.ASCII.GetBytes(header), .. stream];
    }

    /// A zlib stream holding <paramref name="raw"/> in one stored (uncompressed)
    /// block, so a changed data byte still decodes and only Adler-32 catches it.
    static byte[] StoredZlib(byte[] raw)
    {
        var block = new byte[5];
        block[0] = 1; // BFINAL, BTYPE = stored
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(1), (ushort)raw.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(3), (ushort)~raw.Length);
        var adler = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(adler, MiqBinaryReader.Adler32(raw));
        return [0x78, 0x01, .. block, .. raw, .. adler];
    }

    [Fact]
    public void Adler32_MatchesReferenceValues()
    {
        Assert.Equal(1u, MiqBinaryReader.Adler32([]));
        Assert.Equal(0x11E60398u, MiqBinaryReader.Adler32(Encoding.ASCII.GetBytes("Wikipedia")));
        // Past one 5552-byte run, where the sums must be reduced to stay exact.
        var big = Enumerable.Repeat((byte)0xFF, 100_000).ToArray();
        Assert.Equal(Adler32Reference(big), MiqBinaryReader.Adler32(big));
    }

    static uint Adler32Reference(byte[] data)
    {
        ulong a = 1, b = 0;
        foreach (var x in data) { a = (a + x) % 65521; b = (b + a) % 65521; }
        return (uint)(b << 16 | a);
    }

    [Fact]
    public void FailedAdler32_IsRejected_ButValidStreamParses()
    {
        int[] sizes = [4, 3, 2];
        var file = MetaImage(sizes, MiqDatatype.Int16, compressed: true);
        AssertVoxels(MetaImageParser.Parse(file), sizes, MiqDatatype.Int16);
        var bad = (byte[])file.Clone();
        bad[^1] ^= 0xFF;
        var e = Assert.Throws<MiqException>(() => MetaImageParser.Parse(bad));
        Assert.Equal(MiqException.DecompressionFailed().Message, e.Message);
    }

    [Fact]
    public void CorruptStoredBlock_IsRejected_ButIntactBlockParses()
    {
        int[] sizes = [4, 3, 2];
        var file = MetaImage(sizes, MiqDatatype.Int16, compressed: true);
        var raw = MetaImage(sizes, MiqDatatype.Int16)[^(4 * 3 * 2 * 2)..];
        var stream = StoredZlib(raw);
        AssertVoxels(MetaImageParser.Parse(WithStream(file, stream)), sizes, MiqDatatype.Int16);
        stream[7 + 5] ^= 0x40; // a data byte; the block still decodes
        var e = Assert.Throws<MiqException>(() => MetaImageParser.Parse(WithStream(file, stream)));
        Assert.Equal(MiqException.DecompressionFailed().Message, e.Message);
    }

    // The Adler-32 trailer sits where the DEFLATE data ends, not at the end of the
    // input: bytes after it (no CompressedDataSize, say) must not move the check.
    static readonly byte[] StoredOneToEight =
        [120, 1, 1, 8, 0, 247, 255, 1, 2, 3, 4, 5, 6, 7, 8, 0, 128, 0, 37];

    /// Streams that exercise every DEFLATE block type: stored, fixed Huffman
    /// (short input) and dynamic Huffman (long, repetitive input).
    static (byte[] stream, byte[] raw) ZlibOf(string blockType)
    {
        var raw = blockType switch
        {
            "stored" => [1, 2, 3, 4, 5, 6, 7, 8],
            "fixed" => Encoding.ASCII.GetBytes("abcabcabc"),
            _ => Enumerable.Range(0, 200_000).Select(i => (byte)(i * i % 251 ^ i / 1000)).ToArray(),
        };
        return (blockType == "stored" ? StoredOneToEight : Zlib(raw), raw);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(100)]
    [InlineData(100_000)] // beyond any tail window: the end is found on a second pass
    public void TrailingBytesAfterValidStream_AreIgnored(int trailing)
    {
        foreach (var blockType in new[] { "stored", "fixed", "dynamic" })
        {
            var (stream, raw) = ZlibOf(blockType);
            Assert.Equal(raw, MiqBinaryReader.InflateZlib([.. stream, .. new byte[trailing]], raw.Length));
        }
    }

    [Theory]
    [InlineData("stored")]
    [InlineData("fixed")]
    [InlineData("dynamic")]
    public void FailedAdler32_IsRejected_WithTrailingBytes(string blockType)
    {
        var (stream, raw) = ZlibOf(blockType);
        var bad = (byte[])stream.Clone();
        bad[^1] ^= 0xFF;
        foreach (var trailing in new[] { 0, 4, 100_000 })
        {
            var e = Assert.Throws<MiqException>(() =>
                MiqBinaryReader.InflateZlib([.. bad, .. new byte[trailing]], raw.Length));
            Assert.Equal(MiqException.DecompressionFailed().Message, e.Message);
        }
        // The right check value in the trailing bytes doesn't stand in for the trailer.
        Assert.Throws<MiqException>(() => MiqBinaryReader.InflateZlib([.. bad, .. stream[^4..]], raw.Length));
    }

    [Theory]
    [InlineData("stored")]
    [InlineData("fixed")]
    [InlineData("dynamic")]
    public void MissingTrailerBytes_AreRejected(string blockType)
    {
        var (stream, raw) = ZlibOf(blockType);
        for (var cut = 1; cut <= 4; cut++)
            Assert.Throws<MiqException>(() => MiqBinaryReader.InflateZlib(stream[..^cut], raw.Length));
    }

    [Fact]
    public void CorruptStoredBlock_IsRejected_WithTrailingBytes()
    {
        var corrupt = (byte[])StoredOneToEight.Clone();
        corrupt[7 + 5] ^= 0x40;
        Assert.Throws<MiqException>(() => MiqBinaryReader.InflateZlib([.. corrupt, 0, 0, 0, 0], 8));
    }

    // GZipStream checks the CRC itself once it reaches the footer (on net8 already
    // within the last read); this pins that, since the managed path relies on it.
    [Fact]
    public void FailedGzipCrc_IsRejected()
    {
        int[] sizes = [4, 3, 2];
        var file = MetaImage(sizes, MiqDatatype.Int16, compressed: true);
        var raw = MetaImage(sizes, MiqDatatype.Int16)[^(4 * 3 * 2 * 2)..];
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true)) gz.Write(raw);
        var gzip = ms.ToArray();
        AssertVoxels(MetaImageParser.Parse(WithStream(file, gzip)), sizes, MiqDatatype.Int16);
        gzip[^8] ^= 0xFF; // CRC32, ahead of ISIZE
        Assert.Throws<MiqException>(() => MetaImageParser.Parse(WithStream(file, gzip)));
    }

    // Deliberate (parity with macOS): a stream longer than the header declares
    // keeps its prefix, whose check value can't be verified without decoding on.
    [Fact]
    public void StreamLongerThanDeclared_KeepsPrefix()
    {
        int[] sizes = [4, 3, 2];
        var file = MetaImage(sizes, MiqDatatype.Int16, compressed: true, extraFields: ["DimSize = 4 3 1"]);
        AssertVoxels(MetaImageParser.Parse(file), [4, 3, 1], MiqDatatype.Int16);
    }
}
