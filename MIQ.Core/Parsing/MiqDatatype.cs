namespace MIQ.Parsing;

/// <summary>
/// Voxel data type. Raw values are the NIfTI datatype codes.
/// Mirrors MIQCore's <c>MIQDatatype</c>.
/// </summary>
public enum MiqDatatype : short
{
    Int8 = 256,
    Uint8 = 2,
    Int16 = 4,
    Uint16 = 512,
    Int32 = 8,
    Uint32 = 768,
    Float32 = 16,
    Float64 = 64,
    Rgb24 = 128,
    Rgba32 = 2304,
}

public static class MiqDatatypeExtensions
{
    public static bool IsKnown(short raw) => Enum.IsDefined(typeof(MiqDatatype), raw);

    public static int BytesPerVoxel(this MiqDatatype dt) => dt switch
    {
        MiqDatatype.Int8 or MiqDatatype.Uint8 => 1,
        MiqDatatype.Int16 or MiqDatatype.Uint16 => 2,
        MiqDatatype.Int32 or MiqDatatype.Uint32 or MiqDatatype.Float32 => 4,
        MiqDatatype.Float64 => 8,
        MiqDatatype.Rgb24 => 3,
        MiqDatatype.Rgba32 => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(dt)),
    };

    public static string Label(this MiqDatatype dt) => dt switch
    {
        MiqDatatype.Int8 => "int8",
        MiqDatatype.Uint8 => "uint8",
        MiqDatatype.Int16 => "int16",
        MiqDatatype.Uint16 => "uint16",
        MiqDatatype.Int32 => "int32",
        MiqDatatype.Uint32 => "uint32",
        MiqDatatype.Float32 => "float32",
        MiqDatatype.Float64 => "float64",
        MiqDatatype.Rgb24 => "rgb24",
        MiqDatatype.Rgba32 => "rgba32",
        _ => throw new ArgumentOutOfRangeException(nameof(dt)),
    };
}
