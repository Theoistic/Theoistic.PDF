using System.Globalization;
using System.Text;

namespace Theoistic.PDF.Writing;

/// <summary>
/// A PDF document under construction: an indirect object table, a page tree, and the logic to
/// serialise both into a valid file.
/// <para>
/// Output is deterministic. Nothing timestamped or random is written unless asked for, so the
/// same calls produce byte-identical files and generated output can be compared against a
/// fixture.
/// </para>
/// </summary>
public sealed class PdfDocument
{
    // Object numbering starts at 1, so slot 0 is a placeholder that is never written. It
    // corresponds to the head of the free list in the cross-reference table.
    private readonly List<PdfObject?> objects = new List<PdfObject?> { null };

    private readonly List<PdfPage> pages = new List<PdfPage>();

    // A list alongside the lookup because finalisation order decides object numbering, and
    // output has to be byte-for-byte reproducible.
    private readonly Dictionary<PdfFont, FontResource> fontResources = new Dictionary<PdfFont, FontResource>();
    private readonly List<FontResource> fontOrder = new List<FontResource>();

    // One XObject per image however many times it is drawn, so a logo repeated on every page is
    // stored once.
    private readonly Dictionary<PdfImage, PdfReference> imageObjects = new Dictionary<PdfImage, PdfReference>();

    private readonly PdfDictionary catalog;
    private readonly PdfDictionary pageTree;
    private readonly PdfReference catalogReference;
    private readonly PdfReference pageTreeReference;

    private PdfReference? infoReference;

    public PdfDocument()
    {
        // Both are registered up front because a page has to reference the tree as its parent
        // and the tree has to list the page as a kid, so neither can wait for the other.
        catalog = new PdfDictionary().Set("Type", PdfName.Catalog);
        pageTree = new PdfDictionary().Set("Type", PdfName.Pages);

        catalogReference = Add(catalog);
        pageTreeReference = Add(pageTree);

        catalog.Set("Pages", pageTreeReference);
    }

    /// <summary>The pages in the document, in the order they will appear.</summary>
    public IReadOnlyList<PdfPage> Pages => pages;

    /// <summary>
    /// Optional document metadata. Left empty by default: a creation date would make output
    /// non-reproducible, so it is only written if set explicitly.
    /// </summary>
    public PdfDocumentInfo Info { get; } = new PdfDocumentInfo();

    /// <summary>
    /// Registers an object and returns the reference that points at it. The object is written
    /// exactly once however many times the reference appears.
    /// </summary>
    public PdfReference Add(PdfObject value)
    {
        ArgumentNullException.ThrowIfNull(value);

        objects.Add(value);

        return new PdfReference(objects.Count - 1);
    }

    /// <summary>Appends a page of the given size in points.</summary>
    public PdfPage AddPage(double width, double height) =>
        AddPage(PdfRectangle.Page(width, height));

    /// <summary>Appends a page of a standard paper size.</summary>
    public PdfPage AddPage(PdfPaperSize size, PdfOrientation orientation = PdfOrientation.Portrait)
    {
        (double width, double height) = size.ToPoints();

        if (orientation == PdfOrientation.Landscape)
        {
            (width, height) = (height, width);
        }

        return AddPage(width, height);
    }

    public PdfPage AddPage(PdfRectangle mediaBox)
    {
        if (mediaBox.Width <= 0 || mediaBox.Height <= 0)
        {
            throw new ArgumentException(
                $"A page must have a positive extent; got {mediaBox.Width} x {mediaBox.Height}.",
                nameof(mediaBox));
        }

        var content = new PdfStream();
        PdfReference contentReference = Add(content);

        var resources = new PdfDictionary();

        var pageDictionary = new PdfDictionary()
            .Set("Type", PdfName.PageType)
            .Set("Parent", pageTreeReference)
            .Set("MediaBox", PdfArray.Of(mediaBox))
            .Set("Resources", resources)
            .Set("Contents", contentReference);

        Add(pageDictionary);

        var page = new PdfPage(this, pageDictionary, resources, content, mediaBox);
        pages.Add(page);

        return page;
    }

    /// <summary>
    /// Returns this document's state for <paramref name="font"/>, creating it on first use. One
    /// font object per typeface is shared by every page that uses it.
    /// </summary>
    internal FontResource GetFontResource(PdfFont font)
    {
        ArgumentNullException.ThrowIfNull(font);

        if (fontResources.TryGetValue(font, out FontResource? existing))
        {
            return existing;
        }

        FontResource resource = font.CreateResource(this);

        fontResources[font] = resource;
        fontOrder.Add(resource);

        return resource;
    }

    /// <summary>Returns the shared XObject for <paramref name="image"/>, writing it on first use.</summary>
    internal PdfReference GetImageObject(PdfImage image)
    {
        ArgumentNullException.ThrowIfNull(image);

        if (!imageObjects.TryGetValue(image, out PdfReference? reference))
        {
            reference = image.AddTo(this);
            imageObjects[image] = reference;
        }

        return reference;
    }

    /// <summary>Serialises the document to <paramref name="destination"/>.</summary>
    public void Save(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);

        if (pages.Count == 0)
        {
            throw new InvalidOperationException(
                "A PDF must contain at least one page; call AddPage before saving.");
        }

        Finalise();

        var writer = new PdfWriter(destination);

        WriteHeader(writer);

        // One offset per object, recorded as it is written, because the cross-reference table
        // has to state where each one begins.
        var offsets = new long[objects.Count];

        for (int number = 1; number < objects.Count; number++)
        {
            PdfObject? value = objects[number];

            if (value is null)
            {
                continue;
            }

            offsets[number] = writer.Position;

            writer.WriteRaw(number.ToString(CultureInfo.InvariantCulture));
            writer.WriteRaw(" 0 obj");
            writer.WriteLine();
            writer.WriteObject(value);
            writer.WriteLine();
            writer.WriteLine("endobj");
        }

        long crossReferenceOffset = writer.Position;

        WriteCrossReferenceTable(writer, offsets);
        WriteTrailer(writer, crossReferenceOffset);
    }

    /// <summary>Serialises the document and returns the bytes.</summary>
    public byte[] ToArray()
    {
        using var buffer = new MemoryStream();

        Save(buffer);

        return buffer.ToArray();
    }

    /// <summary>Serialises the document to a file, replacing it if it exists.</summary>
    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using FileStream file = File.Create(path);

        Save(file);
    }

    /// <summary>
    /// Flushes pending page content and completes the page tree. Separated from
    /// <see cref="Save(Stream)"/> so that saving twice produces the same bytes rather than
    /// appending to the tree a second time.
    /// </summary>
    private void Finalise()
    {
        var kids = new PdfArray();

        foreach (PdfPage page in pages)
        {
            page.FlushContent();
            kids.Add(new PdfReference(IndexOf(page.Dictionary)));
        }

        pageTree.Set("Kids", kids);
        pageTree.Set("Count", pages.Count);

        // After the pages, because an embedded font's subset is whatever the content streams
        // drew with it, and that is only known once all of them are complete.
        foreach (FontResource font in fontOrder)
        {
            font.Finalise(this);
        }

        // The information dictionary is only emitted when something was set, which keeps the
        // default output free of anything that varies between runs. Registered at most once,
        // so that saving the same document twice does not append a second copy.
        if (Info.HasValues && infoReference is null)
        {
            infoReference = Add(Info.ToDictionary());
        }
    }

    private int IndexOf(PdfObject value)
    {
        int index = objects.IndexOf(value);

        if (index < 1)
        {
            throw new InvalidOperationException(
                "A page dictionary is missing from the object table, which should be impossible.");
        }

        return index;
    }

    private static void WriteHeader(PdfWriter writer)
    {
        writer.WriteLine("%PDF-1.7");

        // A comment of high-bit bytes immediately after the header is the convention that tells
        // transfer tools the file is binary, so that they do not "helpfully" rewrite line
        // endings and corrupt every stream in it.
        writer.WriteRaw("%");
        writer.WriteBytes(new byte[] { 0xE2, 0xE3, 0xCF, 0xD3 });
        writer.WriteLine();
    }

    private static void WriteCrossReferenceTable(PdfWriter writer, long[] offsets)
    {
        writer.WriteLine("xref");
        writer.WriteLine($"0 {offsets.Length.ToString(CultureInfo.InvariantCulture)}");

        // Entries are fixed width: ten digits of offset, five of generation, a type flag, and a
        // two-byte terminator, for exactly twenty bytes. Readers are entitled to seek straight
        // to an entry by multiplying, so a single byte out of place breaks the whole table.
        writer.WriteRaw("0000000000 65535 f \n");

        for (int number = 1; number < offsets.Length; number++)
        {
            string offset = offsets[number].ToString(CultureInfo.InvariantCulture).PadLeft(10, '0');

            writer.WriteRaw($"{offset} 00000 n \n");
        }
    }

    private void WriteTrailer(PdfWriter writer, long crossReferenceOffset)
    {
        var trailer = new PdfDictionary()
            .Set("Size", objects.Count)
            .Set("Root", catalogReference);

        if (infoReference is not null)
        {
            trailer.Set("Info", infoReference);
        }

        writer.WriteLine("trailer");
        writer.WriteObject(trailer);
        writer.WriteLine();
        writer.WriteLine("startxref");
        writer.WriteLine(crossReferenceOffset.ToString(CultureInfo.InvariantCulture));
        writer.WriteRaw("%%EOF");
        writer.WriteLine();
    }
}

/// <summary>Optional document metadata written into the information dictionary.</summary>
public sealed class PdfDocumentInfo
{
    public string? Title { get; set; }

    public string? Author { get; set; }

    public string? Subject { get; set; }

    public string? Keywords { get; set; }

    public string? Creator { get; set; }

    public string? Producer { get; set; }

    /// <summary>
    /// Written only when set. Leaving it null is what keeps output reproducible, so it is not
    /// defaulted to the current time.
    /// </summary>
    public DateTimeOffset? CreationDate { get; set; }

    internal bool HasValues =>
        Title is not null || Author is not null || Subject is not null ||
        Keywords is not null || Creator is not null || Producer is not null ||
        CreationDate is not null;

    internal PdfDictionary ToDictionary()
    {
        var dictionary = new PdfDictionary();

        AddIfSet("Title", Title);
        AddIfSet("Author", Author);
        AddIfSet("Subject", Subject);
        AddIfSet("Keywords", Keywords);
        AddIfSet("Creator", Creator);
        AddIfSet("Producer", Producer);

        if (CreationDate is { } created)
        {
            dictionary.Set("CreationDate", PdfString.Latin1(FormatDate(created)));
        }

        return dictionary;

        void AddIfSet(string key, string? value)
        {
            if (value is not null)
            {
                // UTF-16 with a byte-order mark, so that a title outside Latin-1 survives.
                dictionary.Set(key, PdfString.Utf16(value));
            }
        }
    }

    /// <summary>
    /// Formats a date in the PDF date syntax, whose offset uses an apostrophe separator rather
    /// than the colon that ISO 8601 would use.
    /// </summary>
    internal static string FormatDate(DateTimeOffset value)
    {
        var text = new StringBuilder("D:");

        text.Append(value.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture));

        TimeSpan offset = value.Offset;

        if (offset == TimeSpan.Zero)
        {
            text.Append('Z');
        }
        else
        {
            text.Append(offset < TimeSpan.Zero ? '-' : '+');
            text.Append(Math.Abs(offset.Hours).ToString("00", CultureInfo.InvariantCulture));
            text.Append('\'');
            text.Append(Math.Abs(offset.Minutes).ToString("00", CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }
}

public enum PdfOrientation
{
    Portrait,
    Landscape
}
