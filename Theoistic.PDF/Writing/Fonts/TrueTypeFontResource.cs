using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Theoistic.PDF.Writing.Fonts;

/// <summary>
/// An embedded TrueType font within one document.
/// <para>
/// Written as a Type0 font with Identity-H encoding over a CIDFontType2 descendant: each glyph is
/// addressed by a two-byte code equal to its id in the original font. That is the only PDF font
/// arrangement with no 256-character ceiling, and keeping codes equal to original glyph ids
/// means text can be encoded while drawing, long before the final subset is known.
/// </para>
/// <para>
/// Every object is created and registered up front and filled in by <see cref="Finalise"/>, so
/// object numbers are stable and saving twice writes the same bytes.
/// </para>
/// </summary>
internal sealed class TrueTypeFontResource : FontResource
{
    private readonly TrueTypeFont font;

    // Glyph id to the text it was drawn for, kept sorted so every array written from it comes
    // out in the same order on every run.
    private readonly SortedDictionary<ushort, string> used = new SortedDictionary<ushort, string>();

    private readonly PdfDictionary type0;
    private readonly PdfDictionary cidFont;
    private readonly PdfDictionary descriptor;
    private readonly PdfStream fontFile;
    private readonly PdfStream toUnicode;
    private readonly PdfStream? cidToGidMap;

    private readonly PdfReference cidFontReference;
    private readonly PdfReference descriptorReference;
    private readonly PdfReference fontFileReference;
    private readonly PdfReference toUnicodeReference;
    private readonly PdfReference? cidToGidMapReference;

    private TrueTypeFontResource(TrueTypeFont font, PdfDocument document, PdfDictionary type0, PdfReference reference)
        : base(reference)
    {
        this.font = font;
        this.type0 = type0;

        cidFont = new PdfDictionary();
        descriptor = new PdfDictionary();
        fontFile = new PdfStream();
        toUnicode = new PdfStream();

        cidFontReference = document.Add(cidFont);
        descriptorReference = document.Add(descriptor);
        fontFileReference = document.Add(fontFile);
        toUnicodeReference = document.Add(toUnicode);

        // A whole-font embedding keeps original glyph ids, so the identity mapping is exact and
        // no map is needed; only a renumbered subset needs one.
        if (font.AllowsSubsetting)
        {
            cidToGidMap = new PdfStream();
            cidToGidMapReference = document.Add(cidToGidMap);
        }
    }

    public static TrueTypeFontResource Create(TrueTypeFont font, PdfDocument document)
    {
        var type0 = new PdfDictionary();

        return new TrueTypeFontResource(font, document, type0, document.Add(type0));
    }

    public override PdfString Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var bytes = new List<byte>(text.Length * 2);

        foreach (Rune rune in text.EnumerateRunes())
        {
            ushort glyph = font.GetGlyphId(rune.Value);

            if (glyph == 0)
            {
                // Drawing .notdef would print a box where the reader expects a letter, which is
                // worse than failing: the document would look complete and be wrong.
                throw new ArgumentException(
                    $"U+{rune.Value:X4} ('{rune}') is not in the font '{font.FamilyName} {font.SubfamilyName}'. Use a font that covers it.",
                    nameof(text));
            }

            used.TryAdd(glyph, rune.ToString());

            bytes.Add((byte)(glyph >> 8));
            bytes.Add((byte)glyph);
        }

        return new PdfString(bytes.ToArray(), hexadecimal: true);
    }

    public override void Finalise(PdfDocument document)
    {
        byte[] program;
        string baseFont;

        if (font.AllowsSubsetting)
        {
            TrueTypeSubsetter.Result subset = TrueTypeSubsetter.Subset(font, used.Keys);

            program = subset.Font;

            // The six-letter tag marks a subset, as the specification requires, so that two
            // documents' different subsets of one font are never mistaken for each other.
            baseFont = SubsetTag() + "+" + font.PostScriptName;
            cidToGidMap!.Data = BuildCidToGidMap(subset.NewGlyphIds);
        }
        else
        {
            program = TrueTypeSubsetter.Whole(font);
            baseFont = font.PostScriptName;
        }

        fontFile.Data = program;

        // FontFile2 must state the uncompressed length of the font program.
        fontFile.Set("Length1", (long)program.Length);

        double scale = 1000.0 / font.UnitsPerEm;
        (short xMin, short yMin, short xMax, short yMax) = font.BoundingBox;

        // Symbolic, because the glyphs are addressed by id rather than through a standard
        // Latin encoding; italic and fixed-pitch are carried over so readers can substitute
        // sensibly if they ever have to.
        int flags = 4;

        if (font.IsFixedPitch)
        {
            flags |= 1;
        }

        if (font.IsItalic)
        {
            flags |= 64;
        }

        descriptor
            .Set("Type", new PdfName("FontDescriptor"))
            .SetName("FontName", baseFont)
            .Set("Flags", flags)
            .Set("FontBBox", PdfArray.Numbers(
                Math.Round(xMin * scale),
                Math.Round(yMin * scale),
                Math.Round(xMax * scale),
                Math.Round(yMax * scale)))
            .Set("ItalicAngle", font.ItalicAngle)
            .Set("Ascent", Math.Round(font.HheaAscender * scale))
            .Set("Descent", Math.Round(font.HheaDescender * scale))
            .Set("CapHeight", Math.Round((font.CapHeight ?? (short)(font.HheaAscender * 0.7)) * scale))

            // Not recorded in TrueType fonts. Readers only use it when substituting, and a
            // weight-based estimate is what other producers write too.
            .Set("StemV", font.Weight >= 600 ? 120 : 80)
            .Set("FontFile2", fontFileReference);

        cidFont
            .Set("Type", PdfName.Font)
            .SetName("Subtype", "CIDFontType2")
            .SetName("BaseFont", baseFont)
            .Set("CIDSystemInfo", new PdfDictionary()
                .Set("Registry", PdfString.Latin1("Adobe"))
                .Set("Ordering", PdfString.Latin1("Identity"))
                .Set("Supplement", 0))
            .Set("FontDescriptor", descriptorReference)
            .Set("DW", (long)Math.Round(font.GetAdvance(0) * scale))
            .Set("W", BuildWidths(scale));

        if (cidToGidMapReference is not null)
        {
            cidFont.Set("CIDToGIDMap", cidToGidMapReference);
        }
        else
        {
            cidFont.SetName("CIDToGIDMap", "Identity");
        }

        toUnicode.Data = BuildToUnicode();

        type0
            .Set("Type", PdfName.Font)
            .SetName("Subtype", "Type0")
            .SetName("BaseFont", baseFont)
            .SetName("Encoding", "Identity-H")
            .Set("DescendantFonts", new PdfArray().Add(cidFontReference))
            .Set("ToUnicode", toUnicodeReference);
    }

    /// <summary>
    /// Widths for every used glyph, keyed by code. Consecutive codes share one array, which is
    /// the compact form the specification provides for exactly this.
    /// </summary>
    private PdfArray BuildWidths(double scale)
    {
        var widths = new PdfArray();
        ushort[] codes = used.Keys.ToArray();
        int i = 0;

        while (i < codes.Length)
        {
            int start = i;

            while (i + 1 < codes.Length && codes[i + 1] == codes[i] + 1)
            {
                i++;
            }

            var run = new PdfArray();

            for (int j = start; j <= i; j++)
            {
                run.Add(new PdfInteger((long)Math.Round(font.GetAdvance(codes[j]) * scale)));
            }

            widths.Add(new PdfInteger(codes[start])).Add(run);
            i++;
        }

        return widths;
    }

    /// <summary>Two bytes per code, giving the subset glyph id that original glyph id became.</summary>
    private byte[] BuildCidToGidMap(IReadOnlyDictionary<ushort, ushort> newIds)
    {
        int highest = used.Count == 0 ? 0 : used.Keys.Max();
        var map = new byte[(highest + 1) * 2];

        foreach (ushort code in used.Keys)
        {
            ushort glyph = newIds[code];

            map[code * 2] = (byte)(glyph >> 8);
            map[(code * 2) + 1] = (byte)glyph;
        }

        return map;
    }

    /// <summary>
    /// A ToUnicode CMap, which is what makes text in the PDF searchable and copyable. Without it
    /// a reader sees glyph ids and has no idea which letters they are.
    /// </summary>
    private byte[] BuildToUnicode()
    {
        var cmap = new StringBuilder();

        cmap.Append("/CIDInit /ProcSet findresource begin\n");
        cmap.Append("12 dict begin\n");
        cmap.Append("begincmap\n");
        cmap.Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n");
        cmap.Append("/CMapName /Adobe-Identity-UCS def\n");
        cmap.Append("/CMapType 2 def\n");
        cmap.Append("1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");

        KeyValuePair<ushort, string>[] entries = used.ToArray();

        // The format caps each bfchar block at a hundred entries.
        for (int start = 0; start < entries.Length; start += 100)
        {
            int count = Math.Min(100, entries.Length - start);

            cmap.Append(count.ToString(CultureInfo.InvariantCulture)).Append(" beginbfchar\n");

            for (int i = start; i < start + count; i++)
            {
                cmap.Append('<').Append(entries[i].Key.ToString("X4", CultureInfo.InvariantCulture)).Append("> <");

                foreach (byte b in Encoding.BigEndianUnicode.GetBytes(entries[i].Value))
                {
                    cmap.Append(b.ToString("X2", CultureInfo.InvariantCulture));
                }

                cmap.Append(">\n");
            }

            cmap.Append("endbfchar\n");
        }

        cmap.Append("endcmap\n");
        cmap.Append("CMapName currentdict /CMap defineresource pop\n");
        cmap.Append("end\nend\n");

        return Encoding.ASCII.GetBytes(cmap.ToString());
    }

    /// <summary>
    /// Six uppercase letters derived from the font and the glyph set. Derived rather than random
    /// so that the same document always produces the same bytes.
    /// </summary>
    private string SubsetTag()
    {
        var input = new StringBuilder(font.PostScriptName);

        foreach (ushort glyph in used.Keys)
        {
            input.Append(',').Append(glyph.ToString(CultureInfo.InvariantCulture));
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(input.ToString()));

        return string.Create(6, hash, static (span, bytes) =>
        {
            for (int i = 0; i < span.Length; i++)
            {
                span[i] = (char)('A' + (bytes[i] % 26));
            }
        });
    }
}
