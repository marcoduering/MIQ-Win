using System.Buffers.Binary;
using System.Text;
using MIQ.Parsing;
using MIQ.Rendering;
using Xunit;

namespace MIQ.Tests;

// MiqHeader.FileVoxelIndex must name the voxel MIQ actually reads, in the file's
// own index terms. The fixtures encode each voxel's file index in its value, so
// every check is against the file, not against the mapping code.
// Port of MIQ's VoxelIndexTests (metaimage-mha branch, MIQ@da2aa8d).
public class VoxelIndexTests
{
    /// Int16 MIF storing image voxel (i, j, k[, v]) as i + 10j + 100k + 1000v.
    /// Laid out from MRtrix's layout rule directly (not through MifParser's stride
    /// code), so the parser is checked against an independent writer:
    /// |layout[a]| ranks axis a (0 = fastest) and a '-' stores it from its last
    /// voxel to its first.
    static byte[] Mif(int[] dims, string[] layout)
    {
        var ranks = layout.Select(t => Math.Abs(int.Parse(t))).ToArray();
        var reversed = layout.Select(t => t.StartsWith('-')).ToArray();
        var n = dims.Length;
        var total = dims.Aggregate(1, (a, b) => a * b);
        var payload = new byte[total * 2];
        var voxel = new int[n];
        for (var linear = 0; linear < total; linear++)
        {
            var rest = linear;
            for (var a = 0; a < n; a++) { voxel[a] = rest % dims[a]; rest /= dims[a]; }

            var element = 0;
            var value = 0;
            for (var axis = 0; axis < n; axis++)
            {
                var stride = Enumerable.Range(0, n).Where(b => ranks[b] < ranks[axis])
                    .Aggregate(1, (s, b) => s * dims[b]);
                var position = reversed[axis] ? dims[axis] - 1 - voxel[axis] : voxel[axis];
                element += position * stride;
                value += voxel[axis] * (int)Math.Pow(10, axis);
            }
            BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(element * 2), (short)value);
        }

        var header = "mrtrix image\n"
                     + $"dim: {string.Join(",", dims)}\n"
                     + $"vox: {string.Join(",", dims.Select(_ => "1"))}\n"
                     + $"layout: {string.Join(",", layout)}\n"
                     + "datatype: Int16LE\nfile: .\nEND\n";
        return [.. Encoding.ASCII.GetBytes(header), .. payload];
    }

    [Theory]
    [InlineData("+0", "+1", "+2")]
    [InlineData("+1", "-0", "+2")]
    [InlineData("-2", "+0", "-1")]
    [InlineData("+2", "-0", "-1")] // rot60_RIA.mif's layout
    public void MifIndex_MatchesMrtrixImageAxes(string l0, string l1, string l2)
    {
        var image = MifParser.Parse(Mif([5, 4, 3], [l0, l1, l2]));
        var h = image.Header;
        var volume = new MiqVolume(image);
        for (var z = 0; z < h.Depth; z++)
        for (var y = 0; y < h.Height; y++)
        for (var x = 0; x < h.Width; x++)
        {
            var index = h.FileVoxelIndex(x, y, z, 0);
            Assert.Equal(3, index.Length);
            Assert.Equal(index[0] + 10 * index[1] + 100 * index[2], volume.VoxelValue(x, y, z, 0));
        }
    }

    /// 4-D layouts with the volume axis reversed or stored fastest
    /// (volume-interleaved, as MRtrix writes DWI).
    [Theory]
    [InlineData("+0", "+1", "+2", "-3")]
    [InlineData("+1", "+2", "+3", "+0")]
    [InlineData("-2", "+3", "+1", "-0")]
    public void Mif4DIndex_MatchesMrtrixImageAxes(string l0, string l1, string l2, string l3)
    {
        var image = MifParser.Parse(Mif([4, 3, 2, 3], [l0, l1, l2, l3]));
        var h = image.Header;
        var volume = new MiqVolume(image);
        for (var t = 0; t < h.Volumes; t++)
        for (var z = 0; z < h.Depth; z++)
        for (var y = 0; y < h.Height; y++)
        for (var x = 0; x < h.Width; x++)
        {
            var index = h.FileVoxelIndex(x, y, z, t);
            Assert.Equal(4, index.Length);
            Assert.Equal(index[0] + 10 * index[1] + 100 * index[2] + 1000 * index[3],
                volume.VoxelValue(x, y, z, t));
        }
    }

    /// Float32 NRRD with a leading list axis; element i stores i.
    static byte[] NrrdWithLeadingListAxis(int volumes, int w, int h, int d)
    {
        var header = "NRRD0004\ntype: float\ndimension: 4\nspace: right-anterior-superior\n"
                     + $"sizes: {volumes} {w} {h} {d}\n"
                     + "space directions: none (1,0,0) (0,1,0) (0,0,1)\n"
                     + "kinds: list domain domain domain\nendian: little\nencoding: raw\n\n";
        var total = volumes * w * h * d;
        var payload = new byte[total * 4];
        for (var i = 0; i < total; i++)
            BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(i * 4), i);
        return [.. Encoding.ASCII.GetBytes(header), .. payload];
    }

    [Fact]
    public void NrrdIndex_LeadsWithItsListAxis()
    {
        var (volumes, w, h, d) = (3, 4, 3, 2);
        var image = NrrdParser.Parse(NrrdWithLeadingListAxis(volumes, w, h, d));
        var volume = new MiqVolume(image);
        for (var t = 0; t < volumes; t++)
        for (var z = 0; z < d; z++)
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var index = image.Header.FileVoxelIndex(x, y, z, t);
            Assert.Equal(new[] { t, x, y, z }, index);
            // The values follow the file's element order.
            var linear = index[0] + volumes * (index[1] + w * (index[2] + h * index[3]));
            Assert.Equal(linear, volume.VoxelValue(x, y, z, t));
        }
    }

    /// A size-1 list axis is still one of the file's axes: the index keeps its 0.
    [Fact]
    public void NrrdIndex_KeepsASizeOneListAxis() =>
        Assert.Equal(new[] { 0, 3, 1, 1 },
            NrrdParser.Parse(NrrdWithLeadingListAxis(1, 4, 3, 2)).Header.FileVoxelIndex(3, 1, 1, 0));

    [Fact]
    public void PlainNrrd_IndexIsTheCursor()
    {
        var header = "NRRD0004\ntype: uint8\ndimension: 3\nsizes: 4 3 2\nencoding: raw\n\n";
        var h = NrrdParser.Parse([.. Encoding.ASCII.GetBytes(header), .. new byte[24]]).Header;
        Assert.Equal(new[] { 3, 1, 0 }, h.FileVoxelIndex(3, 1, 0, 0));
    }

    [Theory]
    [InlineData(1, new[] { 3, 1, 0 })]
    [InlineData(2, new[] { 3, 1, 0, 1 })]
    public void NiftiIndex_IsTheCursor(int volumes, int[] expected)
    {
        var file = new byte[352 + 4 * 3 * 2 * volumes];
        var s = file.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(s, 348);
        short[] dims = [(short)(volumes > 1 ? 4 : 3), 4, 3, 2, (short)volumes];
        for (var i = 0; i < dims.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(s[(40 + i * 2)..], dims[i]);
        BinaryPrimitives.WriteInt16LittleEndian(s[70..], (short)MiqDatatype.Uint8);
        BinaryPrimitives.WriteInt16LittleEndian(s[72..], 8);
        BinaryPrimitives.WriteSingleLittleEndian(s[108..], 352);

        var h = NiftiParser.Parse(file).Header;
        Assert.Null(h.FileIndexAxes);
        Assert.Equal(expected, h.FileVoxelIndex(3, 1, 0, volumes - 1));
    }
}
