using Theoistic.PDF.Writing.Images;

namespace Theoistic.PDF.Writing;

/// <summary>
/// A raster image - typically a logo - that can be drawn on a page.
/// <para>
/// JPEG and PNG are supported and recognised from their content, not their file extension.
/// A JPEG is embedded byte for byte, since PDF decodes JPEG itself. A PNG is decoded and
/// re-compressed, and its transparency is preserved as a soft mask, so a logo with a transparent
/// background sits cleanly on any colour.
/// </para>
/// <para>
/// An image is decoded once and can be drawn any number of times, on any number of pages and in
/// any number of documents. Each document stores it once however often it appears.
/// </para>
/// </summary>
public sealed class PdfImage
{
    private readonly JpegInfo? jpeg;
    private readonly PngDecoder.Result? png;
    private readonly byte[] source;

    private PdfImage(byte[] source, JpegInfo? jpeg, PngDecoder.Result? png)
    {
        this.source = source;
        this.jpeg = jpeg;
        this.png = png;

        Width = jpeg?.Width ?? png!.Width;
        Height = jpeg?.Height ?? png!.Height;
    }

    /// <summary>Width in pixels.</summary>
    public int Width { get; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; }

    /// <summary>Width divided by height, for sizing the image without distorting it.</summary>
    public double AspectRatio => (double)Width / Height;

    /// <summary>Whether any pixel is less than fully opaque.</summary>
    public bool HasTransparency => png?.Alpha is not null;

    public static PdfImage FromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return FromBytes(File.ReadAllBytes(path));
    }

    public static PdfImage FromStream(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);

        return FromBytes(buffer.ToArray());
    }

    public static PdfImage FromBytes(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return new PdfImage(data, JpegInfo.Read(data), null);
        }

        if (data.Length >= 8 && data[0] == 0x89 && data[1] == (byte)'P' && data[2] == (byte)'N' && data[3] == (byte)'G')
        {
            return new PdfImage(data, null, PngDecoder.Decode(data));
        }

        throw new NotSupportedException(
            "The image is neither JPEG nor PNG. Convert it to one of those to embed it; SVG and WebP are not supported.");
    }

    /// <summary>
    /// The size that fits the image inside a box without distorting it, never scaling it larger
    /// than the box in either direction.
    /// </summary>
    public (double Width, double Height) FitWithin(double maxWidth, double maxHeight)
    {
        if (maxWidth <= 0 || maxHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxWidth), "The box must have a positive size.");
        }

        double scale = Math.Min(maxWidth / Width, maxHeight / Height);

        return (Width * scale, Height * scale);
    }

    /// <summary>Writes the image XObject (and its soft mask) into a document.</summary>
    internal PdfReference AddTo(PdfDocument document)
    {
        if (jpeg is not null)
        {
            var stream = new PdfStream(source);

            stream
                .Set("Type", PdfName.XObject)
                .SetName("Subtype", "Image")
                .Set("Width", Width)
                .Set("Height", Height)
                .SetName("ColorSpace", jpeg.Components switch
                {
                    1 => "DeviceGray",
                    3 => "DeviceRGB",
                    _ => "DeviceCMYK"
                })
                .Set("BitsPerComponent", jpeg.BitsPerComponent)

                // Declaring the filter is what tells the writer the bytes are already encoded,
                // so the JPEG goes in exactly as it came.
                .Set("Filter", new PdfName("DCTDecode"));

            if (jpeg.AdobeInverted)
            {
                stream.Set("Decode", PdfArray.Numbers(1, 0, 1, 0, 1, 0, 1, 0));
            }

            return document.Add(stream);
        }

        PngDecoder.Result image = png!;

        var color = new PdfStream(image.Color);

        color
            .Set("Type", PdfName.XObject)
            .SetName("Subtype", "Image")
            .Set("Width", Width)
            .Set("Height", Height)
            .SetName("ColorSpace", image.Channels == 3 ? "DeviceRGB" : "DeviceGray")
            .Set("BitsPerComponent", 8);

        if (image.Alpha is not null)
        {
            // PDF keeps transparency in a separate greyscale image the same size as the colour
            // one, referenced as its soft mask.
            var mask = new PdfStream(image.Alpha);

            mask
                .Set("Type", PdfName.XObject)
                .SetName("Subtype", "Image")
                .Set("Width", Width)
                .Set("Height", Height)
                .SetName("ColorSpace", "DeviceGray")
                .Set("BitsPerComponent", 8);

            color.Set("SMask", document.Add(mask));
        }

        return document.Add(color);
    }
}
