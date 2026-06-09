namespace MIQ.Rendering;

/// A finished slice: either 8-bit grayscale or interleaved RGB. C# port of
/// MIQCore's <c>SliceImage</c> enum (<c>.grayscale</c> / <c>.rgb</c>). Exactly
/// one of <see cref="Grayscale"/>/<see cref="Rgb"/> is non-null; consumers
/// switch on which to pick the right WPF pixel format.
public sealed class SliceImage
{
    public GrayscaleImage? Grayscale { get; }
    public RgbImage? Rgb { get; }

    private SliceImage(GrayscaleImage? grayscale, RgbImage? rgb)
    {
        Grayscale = grayscale;
        Rgb = rgb;
    }

    public static SliceImage Gray(GrayscaleImage image) => new(image, null);
    public static SliceImage FromRgb(RgbImage image) => new(null, image);

    public int Width => Grayscale?.Width ?? Rgb!.Width;
    public int Height => Grayscale?.Height ?? Rgb!.Height;
}
