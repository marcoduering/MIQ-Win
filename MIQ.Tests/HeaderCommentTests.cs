using System.Buffers.Binary;
using System.Text;
using MIQ.Parsing;
using MIQ.Rendering;
using Xunit;

namespace MIQ.Tests;

// The header's free-text description (MiqHeader.Comment) and its metadata row.
// Port of MIQ's HeaderCommentTests (metaimage-mha branch, MIQ@157fe0f).
public class HeaderCommentTests
{
    /// 2×2×2 uint8 NIfTI-1 or -2 with an empty descrip.
    static byte[] Nifti(int version)
    {
        var offset = version == 1 ? 352 : 544;
        var file = new byte[offset + 8];
        var h = file.AsSpan();
        if (version == 1)
        {
            BinaryPrimitives.WriteInt32LittleEndian(h, 348);
            short[] dims = [3, 2, 2, 2];
            for (var i = 0; i < dims.Length; i++)
                BinaryPrimitives.WriteInt16LittleEndian(h[(40 + i * 2)..], dims[i]);
            BinaryPrimitives.WriteInt16LittleEndian(h[70..], (short)MiqDatatype.Uint8);
            BinaryPrimitives.WriteInt16LittleEndian(h[72..], 8);
            BinaryPrimitives.WriteSingleLittleEndian(h[108..], offset);
        }
        else
        {
            BinaryPrimitives.WriteInt32LittleEndian(h, 540);
            BinaryPrimitives.WriteInt16LittleEndian(h[12..], (short)MiqDatatype.Uint8);
            BinaryPrimitives.WriteInt16LittleEndian(h[14..], 8);
            long[] dims = [3, 2, 2, 2, 1, 1, 1, 1];
            for (var i = 0; i < dims.Length; i++)
                BinaryPrimitives.WriteInt64LittleEndian(h[(16 + i * 8)..], dims[i]);
            BinaryPrimitives.WriteInt64LittleEndian(h[168..], offset);
        }
        return file;
    }

    static string? CommentRow(MiqHeader header) =>
        new MiqMetadata(header, "x", null).AsDisplayLines()
            .FirstOrDefault(e => e.Label == "Comment")?.Value;

    [Fact]
    public void Nifti1Descrip_IsReadUpToItsFirstNul()
    {
        var data = Nifti(1);
        byte[] descrip = [.. Encoding.UTF8.GetBytes("TE=30;Time=101112.500"), 0, 0x41, 0x42];
        descrip.CopyTo(data, 148);
        var header = NiftiParser.Parse(data).Header;

        Assert.Equal("TE=30;Time=101112.500", header.Comment);
        Assert.Equal("TE=30;Time=101112.500", CommentRow(header));
    }

    [Fact]
    public void Nifti2Descrip_IsRead()
    {
        var data = Nifti(2);
        Encoding.UTF8.GetBytes("FSL").CopyTo(data, 240);
        Assert.Equal("FSL", NiftiParser.Parse(data).Header.Comment);
    }

    // The vol-0-first partial loads keep the probe header, so it must carry it too.
    [Fact]
    public void Nifti1Descrip_SurvivesHeaderOnlyParse()
    {
        var data = Nifti(1);
        Encoding.UTF8.GetBytes("probe").CopyTo(data, 148);
        Assert.Equal("probe", NiftiParser.ParseHeader(data).Comment);
    }

    [Fact]
    public void EmptyDescrip_HasNoRow()
    {
        var header = NiftiParser.Parse(Nifti(1)).Header;
        Assert.Null(header.Comment);
        Assert.Null(CommentRow(header));
    }

    [Fact]
    public void Mif_CollectsEveryCommentsLine()
    {
        var text = "mrtrix image\ndim: 2,2,2\nvox: 1,1,1\nlayout: +0,+1,+2\ndatatype: UInt8\n"
                   + "comments: DOE^JOHN [12345]\ncomments:   \ncomments: DOB: 01/02/1970\n"
                   + "file: .\nEND\n";
        byte[] data = [.. Encoding.ASCII.GetBytes(text), .. new byte[8]];
        var header = MifParser.Parse(data).Header;

        Assert.Equal("DOE^JOHN [12345]\nDOB: 01/02/1970", header.Comment);
        Assert.Equal("DOE^JOHN [12345]\nDOB: 01/02/1970", CommentRow(header));
    }

    [Fact]
    public void NrrdContent_IsRead()
    {
        var text = "NRRD0004\ncontent: T1 MPRAGE\ntype: uint8\ndimension: 3\nsizes: 2 2 2\n"
                   + "endian: little\nencoding: raw\n\n";
        byte[] data = [.. Encoding.ASCII.GetBytes(text), .. new byte[8]];
        Assert.Equal("T1 MPRAGE", NrrdParser.Parse(data).Header.Comment);
    }

    [Fact]
    public void MetaImageComment_IsRead()
    {
        var data = MetaImageTests.MetaImage([2, 2, 2], MiqDatatype.Uint8, extraFields: ["Comment = resampled"]);
        Assert.Equal("resampled", MetaImageParser.Parse(data).Header.Comment);
    }

    [Fact]
    public void ControlCharacters_BecomeSpaces()
    {
        Assert.Equal("a b c", MiqParser.HeaderComment(["a\tb\u0007c  "]));
        Assert.Null(MiqParser.HeaderComment(["", "  "]));
    }

    [Fact]
    public void ExtraCommentLines_CollapseIntoAnEllipsisLine()
    {
        var lines = Enumerable.Range(1, 10).Select(i => $"line {i}");
        Assert.Equal("line 1\nline 2\nline 3\n…",
            MiqMetadata.DisplayComment(string.Join("\n", lines)));

        // Exactly three lines fit and are shown unchanged; long lines are left to
        // the panel, which caps by wrapped lines at its width.
        var longLine = new string('x', 500);
        Assert.Equal($"a\nb\n{longLine}", MiqMetadata.DisplayComment($"a\nb\n{longLine}"));
    }
}
