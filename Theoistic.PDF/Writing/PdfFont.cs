namespace Theoistic.PDF.Writing;

/// <summary>
/// A typeface that text can be measured and shown in.
/// <para>
/// Two kinds exist. <see cref="StandardFont"/> names one of the typefaces every reader provides,
/// so nothing is embedded, but it can only show the WinAnsi character set. <see cref="TrueTypeFont"/>
/// embeds a subset of a font file, which costs some bytes and shows whatever the font covers.
/// </para>
/// <para>
/// A font object is not tied to a document. Each document keeps its own record of which glyphs
/// it used, so one loaded font can serve any number of documents and each embeds only its own
/// subset.
/// </para>
/// </summary>
public abstract class PdfFont
{
    /// <summary>The PostScript-style name, for diagnostics and the font dictionary.</summary>
    public abstract string Name { get; }

    /// <summary>Height above the baseline, in thousandths of the font size.</summary>
    public abstract double Ascent { get; }

    /// <summary>Depth below the baseline, in thousandths of the font size. Negative.</summary>
    public abstract double Descent { get; }

    public abstract bool IsBold { get; }

    public abstract bool IsItalic { get; }

    /// <summary>
    /// Width of a string at a given size, in points. Character and word spacing are graphics
    /// state, so they are not included.
    /// </summary>
    public abstract double GetTextWidth(string text, double size);

    /// <summary>
    /// Returns the first character in <paramref name="text"/> this font cannot show, or null if
    /// it can show all of it. Lets a caller check a whole document's text up front and report
    /// every problem at once, instead of failing on the first string it happens to draw.
    /// </summary>
    public abstract string? FindUnsupported(string text);

    /// <summary>Creates the per-document state that tracks usage and writes the font objects.</summary>
    internal abstract FontResource CreateResource(PdfDocument document);

    public override string ToString() => Name;
}

/// <summary>
/// A font as one particular document sees it: the reference its pages point at, and whatever
/// the font has to remember about what was drawn with it.
/// </summary>
internal abstract class FontResource
{
    protected FontResource(PdfReference reference) => Reference = reference;

    public PdfReference Reference { get; }

    /// <summary>
    /// Turns text into the bytes a content stream shows, recording what was used so the font
    /// program written at save time contains it.
    /// </summary>
    public abstract PdfString Encode(string text);

    /// <summary>
    /// Writes or rewrites the font's objects. Called on every save, so it has to be repeatable:
    /// saving twice must produce the same file, and drawing more between saves must be reflected.
    /// </summary>
    public virtual void Finalise(PdfDocument document)
    {
    }
}
