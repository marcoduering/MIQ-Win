namespace MIQ.Parsing;

/// <summary>
/// Parsing/format failures. Mirrors MIQCore's <c>MIQError</c>.
/// </summary>
public sealed class MiqException : Exception
{
    public MiqException(string message) : base(message) { }

    public static MiqException InvalidHeaderSize(int value) =>
        new($"Invalid NIfTI header size: {value}.");

    public static MiqException UnsupportedDatatype(int value) =>
        new($"Unsupported datatype code: {value}.");

    public static MiqException InvalidDimensions() =>
        new("Invalid or missing image dimensions.");

    public static MiqException TruncatedData() =>
        new("File appears truncated.");

    public static MiqException DecompressionFailed() =>
        new("Failed to decompress gzipped data.");

    public static MiqException UnsupportedFileFormat() =>
        new("Unsupported file format. Expected .nii, .nii.gz, .mgh, .mgz, .mgh.gz, .mif, .mif.gz, or .nrrd.");
}
