namespace Theoistic.PDF.Writing;

/// <summary>
/// One of the standard typefaces every PDF reader is required to provide. Using these means no
/// font program is embedded and no font licence applies to the output.
/// <para>
/// Widths are exact for the printable ASCII range. Above that, the advance of an accented Latin
/// letter is taken from its unaccented base, which holds for these typefaces, and the remaining
/// symbols fall back to a documented approximation - see <see cref="GetGlyphWidth"/>. Exact
/// metrics for the whole WinAnsi range arrive with embedded fonts, which need a real font
/// parser anyway.
/// </para>
/// </summary>
public sealed class StandardFont
{
    // Widths are in glyph space: thousandths of the font size, which is how PDF font metrics
    // are always expressed.
    private const int WidthsStart = 32;
    private const int WidthsEnd = 126;

    private readonly int[] widths;

    private StandardFont(string baseFontName, int[] widths, bool bold, bool italic)
    {
        BaseFontName = baseFontName;
        this.widths = widths;
        IsBold = bold;
        IsItalic = italic;
    }

    /// <summary>The name written into the font dictionary, such as Helvetica-BoldOblique.</summary>
    public string BaseFontName { get; }

    public bool IsBold { get; }

    public bool IsItalic { get; }

    public override string ToString() => BaseFontName;

    // ---------------------------------------------------------------- metrics

    /// <summary>
    /// Advance width of a single character, in thousandths of the font size.
    /// <para>
    /// Characters outside printable ASCII resolve through their unaccented base where one
    /// exists. Anything else returns the width of a lowercase 'n', which is close to the
    /// average advance in all of these typefaces but is an approximation, not a metric.
    /// </para>
    /// </summary>
    public int GetGlyphWidth(char c)
    {
        if (c >= WidthsStart && c <= WidthsEnd)
        {
            return widths[c - WidthsStart];
        }

        char baseCharacter = ToBaseCharacter(c);

        if (baseCharacter >= WidthsStart && baseCharacter <= WidthsEnd)
        {
            return widths[baseCharacter - WidthsStart];
        }

        return widths['n' - WidthsStart];
    }

    /// <summary>
    /// Width of a string at a given font size, in points. Does not account for character or
    /// word spacing, which are graphics state and belong to whoever set them.
    /// </summary>
    public double GetTextWidth(string text, double size)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (size <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "Font size must be positive.");
        }

        long total = 0;

        foreach (char c in text)
        {
            total += GetGlyphWidth(c);
        }

        return total * size / 1000.0;
    }

    /// <summary>
    /// Maps an accented Latin-1 letter to the letter it is built on. The advance width of the
    /// composed glyph equals the base letter's in all fourteen standard typefaces, because the
    /// accent is drawn within the same advance.
    /// </summary>
    private static char ToBaseCharacter(char c) => c switch
    {
        >= 'À' and <= 'Å' => 'A',
        'Ç' => 'C',
        >= 'È' and <= 'Ë' => 'E',
        >= 'Ì' and <= 'Ï' => 'I',
        'Ð' => 'D',
        'Ñ' => 'N',
        >= 'Ò' and <= 'Ö' or 'Ø' => 'O',
        >= 'Ù' and <= 'Ü' => 'U',
        'Ý' or 'Ÿ' => 'Y',
        'Þ' => 'P',
        'ß' => 'B',
        >= 'à' and <= 'å' => 'a',
        'ç' => 'c',
        >= 'è' and <= 'ë' => 'e',
        >= 'ì' and <= 'ï' => 'i',
        'ð' or (>= 'ò' and <= 'ö') or 'ø' => 'o',
        'ñ' => 'n',
        >= 'ù' and <= 'ü' => 'u',
        'ý' or 'ÿ' => 'y',
        'þ' => 'p',
        'Š' => 'S',
        'š' => 's',
        'Ž' => 'Z',
        'ž' => 'z',
        '×' or '÷' => '+',
        '‘' or '’' => '\'',
        '“' or '”' => '"',
        '–' or '—' => '-',
        ' ' => ' ',
        _ => '\0'
    };

    // ---------------------------------------------------------------- encoding

    /// <summary>
    /// Encodes text as WinAnsi, the encoding declared on these font dictionaries.
    /// <para>
    /// The codepage is not taken from the runtime: .NET Core does not register CP1252 by
    /// default, and depending on an encoding provider would make output differ between hosts.
    /// The eight-bit range is mapped here instead.
    /// </para>
    /// </summary>
    internal static PdfString Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var bytes = new byte[text.Length];

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (c <= '\u007F')
            {
                bytes[i] = (byte)c;
                continue;
            }

            // WinAnsi agrees with Latin-1 from 0xA0 up, and differs only in the block that
            // Latin-1 leaves as control codes.
            if (c >= ' ' && c <= 'ÿ')
            {
                bytes[i] = (byte)c;
                continue;
            }

            byte mapped = MapHighRange(c);

            if (mapped == 0)
            {
                throw new ArgumentException(
                    $"U+{(int)c:X4} has no WinAnsi encoding and cannot be shown with a standard font. Embedded fonts with a Unicode encoding are needed for this text.",
                    nameof(text));
            }

            bytes[i] = mapped;
        }

        return new PdfString(bytes);
    }

    /// <summary>
    /// The characters WinAnsi places in 0x80-0x9F, where Latin-1 has control codes. Returns
    /// zero for anything unmapped, since no printable character encodes to a null byte.
    /// </summary>
    private static byte MapHighRange(char c) => c switch
    {
        '€' => 0x80, // euro sign
        '‚' => 0x82, // single low quotation mark
        'ƒ' => 0x83, // latin small letter f with hook
        '„' => 0x84, // double low quotation mark
        '…' => 0x85, // horizontal ellipsis
        '†' => 0x86, // dagger
        '‡' => 0x87, // double dagger
        'ˆ' => 0x88, // modifier letter circumflex
        '‰' => 0x89, // per mille sign
        'Š' => 0x8A, // latin capital letter s with caron
        '‹' => 0x8B, // single left angle quotation
        'Œ' => 0x8C, // latin capital ligature oe
        'Ž' => 0x8E, // latin capital letter z with caron
        '‘' => 0x91, // left single quotation mark
        '’' => 0x92, // right single quotation mark
        '“' => 0x93, // left double quotation mark
        '”' => 0x94, // right double quotation mark
        '•' => 0x95, // bullet
        '–' => 0x96, // en dash
        '—' => 0x97, // em dash
        '˜' => 0x98, // small tilde
        '™' => 0x99, // trade mark sign
        'š' => 0x9A, // latin small letter s with caron
        '›' => 0x9B, // single right angle quotation
        'œ' => 0x9C, // latin small ligature oe
        'ž' => 0x9E, // latin small letter z with caron
        'Ÿ' => 0x9F, // latin capital letter y with diaeresis
        _ => 0
    };

    // ---------------------------------------------------------------- the fourteen

    // Metrics come from the Adobe Font Metrics files for the standard typefaces. The oblique
    // variants share their upright counterpart's advances, which is why Helvetica-Oblique
    // reuses the Helvetica table.

    private static readonly int[] HelveticaWidths =
    {
        278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278,
        556, 556, 556, 556, 556, 556, 556, 556, 556, 556,
        278, 278, 584, 584, 584, 556, 1015,
        667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778, 667,
        778, 722, 667, 611, 722, 667, 944, 667, 667, 611,
        278, 278, 278, 469, 556, 333,
        556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556, 556,
        556, 333, 500, 278, 556, 500, 722, 500, 500, 500,
        334, 260, 334, 584
    };

    private static readonly int[] HelveticaBoldWidths =
    {
        278, 333, 474, 556, 556, 889, 722, 238, 333, 333, 389, 584, 278, 333, 278, 278,
        556, 556, 556, 556, 556, 556, 556, 556, 556, 556,
        333, 333, 584, 584, 584, 611, 975,
        722, 722, 722, 722, 667, 611, 778, 722, 278, 556, 722, 611, 833, 722, 778, 667,
        778, 722, 667, 611, 722, 667, 944, 667, 667, 611,
        333, 278, 333, 584, 556, 333,
        556, 611, 556, 611, 556, 333, 611, 611, 278, 278, 556, 278, 889, 611, 611, 611,
        611, 389, 556, 333, 611, 556, 778, 556, 556, 500,
        389, 280, 389, 584
    };

    private static readonly int[] TimesRomanWidths =
    {
        250, 333, 408, 500, 500, 833, 778, 180, 333, 333, 500, 564, 250, 333, 250, 278,
        500, 500, 500, 500, 500, 500, 500, 500, 500, 500,
        278, 278, 564, 564, 564, 444, 921,
        722, 667, 667, 722, 611, 556, 722, 722, 333, 389, 722, 611, 889, 722, 722, 556,
        722, 667, 556, 611, 722, 722, 944, 722, 722, 611,
        333, 278, 333, 469, 500, 333,
        444, 500, 444, 500, 444, 333, 500, 500, 278, 278, 500, 278, 778, 500, 500, 500,
        500, 333, 389, 278, 500, 500, 722, 500, 500, 444,
        480, 200, 480, 541
    };

    private static readonly int[] TimesBoldWidths =
    {
        250, 333, 555, 500, 500, 1000, 833, 278, 333, 333, 500, 570, 250, 333, 250, 278,
        500, 500, 500, 500, 500, 500, 500, 500, 500, 500,
        333, 333, 570, 570, 570, 500, 930,
        722, 667, 722, 722, 667, 611, 778, 778, 389, 500, 778, 667, 944, 722, 778, 611,
        778, 722, 556, 667, 722, 722, 1000, 722, 722, 667,
        333, 278, 333, 581, 500, 333,
        500, 556, 444, 556, 444, 333, 500, 556, 278, 333, 556, 278, 833, 556, 500, 556,
        556, 444, 389, 333, 556, 500, 722, 500, 500, 444,
        394, 220, 394, 520
    };

    private static readonly int[] TimesItalicWidths =
    {
        250, 333, 420, 500, 500, 833, 778, 214, 333, 333, 500, 675, 250, 333, 250, 278,
        500, 500, 500, 500, 500, 500, 500, 500, 500, 500,
        333, 333, 675, 675, 675, 500, 920,
        611, 611, 667, 722, 611, 611, 722, 722, 333, 444, 667, 556, 833, 667, 722, 611,
        722, 611, 500, 556, 722, 611, 833, 611, 556, 556,
        389, 278, 389, 422, 500, 333,
        500, 500, 444, 500, 444, 278, 500, 500, 278, 278, 444, 278, 722, 500, 500, 500,
        500, 389, 389, 278, 500, 444, 667, 444, 444, 389,
        400, 275, 400, 541
    };

    private static readonly int[] TimesBoldItalicWidths =
    {
        250, 389, 555, 500, 500, 833, 778, 278, 333, 333, 500, 570, 250, 333, 250, 278,
        500, 500, 500, 500, 500, 500, 500, 500, 500, 500,
        333, 333, 570, 570, 570, 500, 832,
        667, 667, 667, 722, 667, 667, 722, 778, 389, 500, 667, 611, 889, 722, 722, 611,
        722, 667, 556, 611, 722, 667, 889, 667, 611, 611,
        333, 278, 333, 570, 500, 333,
        500, 500, 444, 500, 444, 333, 500, 556, 278, 278, 500, 278, 778, 556, 500, 500,
        500, 389, 389, 278, 556, 444, 667, 500, 444, 389,
        348, 220, 348, 570
    };

    /// <summary>Courier is monospaced: every glyph advances the same 600 units.</summary>
    private static readonly int[] CourierWidths = CreateMonospaced(600);

    private static int[] CreateMonospaced(int width)
    {
        var table = new int[WidthsEnd - WidthsStart + 1];

        Array.Fill(table, width);

        return table;
    }

    public static StandardFont Helvetica { get; } =
        new StandardFont("Helvetica", HelveticaWidths, bold: false, italic: false);

    public static StandardFont HelveticaBold { get; } =
        new StandardFont("Helvetica-Bold", HelveticaBoldWidths, bold: true, italic: false);

    public static StandardFont HelveticaOblique { get; } =
        new StandardFont("Helvetica-Oblique", HelveticaWidths, bold: false, italic: true);

    public static StandardFont HelveticaBoldOblique { get; } =
        new StandardFont("Helvetica-BoldOblique", HelveticaBoldWidths, bold: true, italic: true);

    public static StandardFont TimesRoman { get; } =
        new StandardFont("Times-Roman", TimesRomanWidths, bold: false, italic: false);

    public static StandardFont TimesBold { get; } =
        new StandardFont("Times-Bold", TimesBoldWidths, bold: true, italic: false);

    public static StandardFont TimesItalic { get; } =
        new StandardFont("Times-Italic", TimesItalicWidths, bold: false, italic: true);

    public static StandardFont TimesBoldItalic { get; } =
        new StandardFont("Times-BoldItalic", TimesBoldItalicWidths, bold: true, italic: true);

    public static StandardFont Courier { get; } =
        new StandardFont("Courier", CourierWidths, bold: false, italic: false);

    public static StandardFont CourierBold { get; } =
        new StandardFont("Courier-Bold", CourierWidths, bold: true, italic: false);

    public static StandardFont CourierOblique { get; } =
        new StandardFont("Courier-Oblique", CourierWidths, bold: false, italic: true);

    public static StandardFont CourierBoldOblique { get; } =
        new StandardFont("Courier-BoldOblique", CourierWidths, bold: true, italic: true);

    /// <summary>
    /// Picks a variant of one of the three families by weight and slope, which is what a style
    /// lookup from a layout layer needs.
    /// </summary>
    public static StandardFont Get(StandardFontFamily family, bool bold = false, bool italic = false) =>
        family switch
        {
            StandardFontFamily.Helvetica => (bold, italic) switch
            {
                (false, false) => Helvetica,
                (true, false) => HelveticaBold,
                (false, true) => HelveticaOblique,
                (true, true) => HelveticaBoldOblique
            },

            StandardFontFamily.Times => (bold, italic) switch
            {
                (false, false) => TimesRoman,
                (true, false) => TimesBold,
                (false, true) => TimesItalic,
                (true, true) => TimesBoldItalic
            },

            StandardFontFamily.Courier => (bold, italic) switch
            {
                (false, false) => Courier,
                (true, false) => CourierBold,
                (false, true) => CourierOblique,
                (true, true) => CourierBoldOblique
            },

            _ => throw new ArgumentOutOfRangeException(nameof(family), family, "Unknown font family.")
        };
}

/// <summary>
/// The three standard families with a full set of weights and slopes. Symbol and ZapfDingbats
/// are the other two standard fonts but use their own encodings rather than WinAnsi, so they
/// are not covered here.
/// </summary>
public enum StandardFontFamily
{
    Helvetica,
    Times,
    Courier
}
