namespace Theoistic.PDF.Writing;

/// <summary>
/// One page of a <see cref="PdfDocument"/>: its box, its resource dictionary, and the content
/// stream that holds the drawing operators.
/// </summary>
public sealed class PdfPage
{
    private readonly PdfDocument document;
    private readonly PdfDictionary resources;
    private readonly PdfStream contentStream;

    // Fonts are shared document-wide but named per page, because the resource name only has to
    // be unique within the page that uses it.
    private readonly Dictionary<PdfFont, string> fontNames = new Dictionary<PdfFont, string>();
    private readonly Dictionary<PdfImage, string> imageNames = new Dictionary<PdfImage, string>();

    internal PdfPage(
        PdfDocument document,
        PdfDictionary dictionary,
        PdfDictionary resources,
        PdfStream contentStream,
        PdfRectangle mediaBox)
    {
        this.document = document;
        this.resources = resources;
        this.contentStream = contentStream;

        Dictionary = dictionary;
        MediaBox = mediaBox;
        Content = new ContentStream(this);
    }

    internal PdfDictionary Dictionary { get; }

    internal PdfDocument Document => document;

    /// <summary>The page box, in points.</summary>
    public PdfRectangle MediaBox { get; }

    public double Width => MediaBox.Width;

    public double Height => MediaBox.Height;

    /// <summary>The drawing operators for this page.</summary>
    public ContentStream Content { get; }

    /// <summary>
    /// Returns the resource name under which <paramref name="font"/> is reachable from this
    /// page's content stream, adding it to the resource dictionary on first use.
    /// </summary>
    internal string GetFontResourceName(PdfFont font)
    {
        ArgumentNullException.ThrowIfNull(font);

        if (fontNames.TryGetValue(font, out string? existing))
        {
            return existing;
        }

        string name = "F" + (fontNames.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (resources["Font"] is not PdfDictionary fonts)
        {
            fonts = new PdfDictionary();
            resources.Set("Font", fonts);
        }

        fonts.Set(name, document.GetFontResource(font).Reference);
        fontNames[font] = name;

        return name;
    }

    /// <summary>
    /// Returns the resource name under which <paramref name="image"/> is reachable from this
    /// page's content stream, adding it to the resource dictionary on first use.
    /// </summary>
    internal string GetImageResourceName(PdfImage image)
    {
        ArgumentNullException.ThrowIfNull(image);

        if (imageNames.TryGetValue(image, out string? existing))
        {
            return existing;
        }

        string name = "Im" + (imageNames.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (resources["XObject"] is not PdfDictionary images)
        {
            images = new PdfDictionary();
            resources.Set("XObject", images);
        }

        images.Set(name, document.GetImageObject(image));
        imageNames[image] = name;

        return name;
    }

    /// <summary>
    /// Copies the accumulated operators into the stream object. Called at save time so that
    /// drawing can continue right up until the document is written.
    /// </summary>
    internal void FlushContent() => contentStream.Data = Content.ToArray();
}

/// <summary>Standard paper sizes, in points.</summary>
public readonly record struct PdfPaperSize
{
    private PdfPaperSize(double width, double height)
    {
        WidthInPoints = width;
        HeightInPoints = height;
    }

    public double WidthInPoints { get; }

    public double HeightInPoints { get; }

    public (double Width, double Height) ToPoints() => (WidthInPoints, HeightInPoints);

    /// <summary>Builds a size from millimetres, the unit the ISO 216 series is defined in.</summary>
    public static PdfPaperSize FromMillimetres(double width, double height) =>
        new PdfPaperSize(width * 72.0 / 25.4, height * 72.0 / 25.4);

    /// <summary>Builds a size from inches.</summary>
    public static PdfPaperSize FromInches(double width, double height) =>
        new PdfPaperSize(width * 72.0, height * 72.0);

    // The ISO sizes are rounded to whole points, which is what every other PDF producer does
    // and what keeps A4 at the familiar 595 x 842 rather than 595.276 x 841.89.
    public static PdfPaperSize A3 => new PdfPaperSize(842, 1191);

    public static PdfPaperSize A4 => new PdfPaperSize(595, 842);

    public static PdfPaperSize A5 => new PdfPaperSize(420, 595);

    public static PdfPaperSize Letter => new PdfPaperSize(612, 792);

    public static PdfPaperSize Legal => new PdfPaperSize(612, 1008);

    public static PdfPaperSize Tabloid => new PdfPaperSize(792, 1224);
}
