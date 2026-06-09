namespace MIQ.Parsing;

/// <summary>
/// FreeSurfer MGH/MGZ parser.
/// Direct port of MIQCore's <c>MIQParser+MGH.swift</c>.
/// Big-endian 284-byte fixed header; voxel data follows immediately.
/// </summary>
public static class MghParser
{
    private const int HeaderSize = 284;

    public static MiqImage Parse(byte[] data, string? formatLabel = null)
    {
        var header = ParseHeader(data, formatLabel);

        var totalVoxels = (long)header.Width * header.Height * header.Depth * header.Volumes;
        var required = (long)HeaderSize + totalVoxels * header.Datatype.BytesPerVoxel();
        if (data.Length < required)
            throw MiqException.TruncatedData();

        return new MiqImage
        {
            Header = header,
            Storage = data,
            PayloadOffset = HeaderSize,
        };
    }

    private static MiqHeader ParseHeader(byte[] data, string? formatLabel)
    {
        if (data.Length < HeaderSize) throw MiqException.TruncatedData();

        var version = MiqBinaryReader.Int32(data, 0, littleEndian: false);
        if (version != 1)
            throw new MiqException($"Unsupported MGH version: {version}.");

        var width   = MiqBinaryReader.Int32(data,  4, littleEndian: false);
        var height  = MiqBinaryReader.Int32(data,  8, littleEndian: false);
        var depth   = MiqBinaryReader.Int32(data, 12, littleEndian: false);
        var nframes = MiqBinaryReader.Int32(data, 16, littleEndian: false);
        var type    = MiqBinaryReader.Int32(data, 20, littleEndian: false);

        if (width <= 0 || height <= 0 || depth <= 0 || nframes <= 0)
            throw MiqException.InvalidDimensions();

        var datatype = MghTypeToDatatype(type);
        var goodRAS  = MiqBinaryReader.Int16(data, 28, littleEndian: false);

        var pixdim = new float[] { 1f, 1f, 1f, 1f };
        OrientationFrame? orientationFrame = null;

        if (goodRAS != 0)
        {
            var sx = MiqBinaryReader.Float32(data, 30, littleEndian: false);
            var sy = MiqBinaryReader.Float32(data, 34, littleEndian: false);
            var sz = MiqBinaryReader.Float32(data, 38, littleEndian: false);
            pixdim = new[] { 1f, sx, sy, sz };

            // Direction cosines: each trio is the direction of one voxel axis in RAS space.
            // xras[0..2]: R,A,S components of the voxel x-axis direction.
            var xras = MiqBinaryReader.Float32Array(data, 42, 3, littleEndian: false);
            var yras = MiqBinaryReader.Float32Array(data, 54, 3, littleEndian: false);
            var zras = MiqBinaryReader.Float32Array(data, 66, 3, littleEndian: false);

            // Build srow-style row vectors so OrientationFrame.From can read per-column
            // world directions: srowX[col] = R-component of voxel-col's direction.
            var srowX = new[] { xras[0] * sx, yras[0] * sy, zras[0] * sz, 0f };
            var srowY = new[] { xras[1] * sx, yras[1] * sy, zras[1] * sz, 0f };
            var srowZ = new[] { xras[2] * sx, yras[2] * sy, zras[2] * sz, 0f };
            orientationFrame = OrientationFrame.From(srowX, srowY, srowZ);
        }

        return new MiqHeader
        {
            LittleEndian  = false,
            Dimensions    = new[] { width, height, depth, nframes },
            Pixdim        = pixdim,
            Datatype      = datatype,
            VoxOffset     = HeaderSize,
            SclSlope      = 0f,
            SclInter      = 0f,
            QformCode     = 0,
            SformCode     = 0,
            SrowX         = new float[] { 0f, 0f, 0f, 0f },
            SrowY         = new float[] { 0f, 0f, 0f, 0f },
            SrowZ         = new float[] { 0f, 0f, 0f, 0f },
            FormatLabel   = formatLabel,
            OrientationFrame = orientationFrame,
        };
    }

    private static MiqDatatype MghTypeToDatatype(int type) => type switch
    {
        0 => MiqDatatype.Uint8,
        1 => MiqDatatype.Int32,
        3 => MiqDatatype.Float32,
        4 => MiqDatatype.Int16,
        _ => throw MiqException.UnsupportedDatatype(type),
    };
}
