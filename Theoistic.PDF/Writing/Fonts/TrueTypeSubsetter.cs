using static Theoistic.PDF.Writing.Fonts.BigEndian;

namespace Theoistic.PDF.Writing.Fonts;

/// <summary>
/// Builds a standalone TrueType font containing only the glyphs a document used.
/// <para>
/// Glyphs are renumbered densely, which is what makes the subset small even when the used
/// glyphs sit at high ids in a CJK font: the outline, location and metric tables only cover the
/// kept glyphs. Content streams keep referring to original glyph ids, and the font dictionary's
/// CIDToGIDMap translates them, so text can be encoded before the final glyph set is known.
/// </para>
/// </summary>
internal static class TrueTypeSubsetter
{
    // Composite glyph component flags, from the 'glyf' table specification.
    private const int ArgsAreWords = 0x0001;
    private const int HasScale = 0x0008;
    private const int MoreComponents = 0x0020;
    private const int HasXYScale = 0x0040;
    private const int HasTwoByTwo = 0x0080;

    // Font-wide hinting tables are copied as they are: they refer to no glyph ids.
    private static readonly string[] CopiedTables = { "cvt ", "fpgm", "prep", "OS/2" };

    /// <summary>A built font, and how original glyph ids map into it.</summary>
    public sealed record Result(byte[] Font, IReadOnlyDictionary<ushort, ushort> NewGlyphIds);

    /// <summary>Builds a subset holding <paramref name="usedGlyphs"/>, .notdef, and every glyph they are built from.</summary>
    public static Result Subset(TrueTypeFont font, IEnumerable<ushort> usedGlyphs)
    {
        List<ushort> kept = Closure(font, usedGlyphs);

        var newIds = new Dictionary<ushort, ushort>(kept.Count);

        for (int i = 0; i < kept.Count; i++)
        {
            newIds[kept[i]] = (ushort)i;
        }

        var glyf = new MemoryStream();
        var loca = new byte[(kept.Count + 1) * 4];

        for (int i = 0; i < kept.Count; i++)
        {
            Write32(loca, i * 4, (uint)glyf.Length);

            byte[] glyph = font.GetGlyphData(kept[i]).ToArray();

            if (glyph.Length > 0 && I16(glyph, 0) < 0)
            {
                RemapComponents(glyph, newIds);
            }

            glyf.Write(glyph);

            // Long loca offsets permit any alignment, but four-byte alignment is what the
            // specification recommends and what some rasterisers quietly assume.
            while (glyf.Length % 4 != 0)
            {
                glyf.WriteByte(0);
            }
        }

        Write32(loca, kept.Count * 4, (uint)glyf.Length);

        var hmtx = new byte[kept.Count * 4];

        for (int i = 0; i < kept.Count; i++)
        {
            Write16(hmtx, i * 4, font.GetAdvance(kept[i]));
            Write16(hmtx, (i * 4) + 2, font.GetLeftSideBearing(kept[i]));
        }

        byte[] head = CopyTable(font, "head");
        Write32(head, 8, 0);       // checkSumAdjustment, recomputed once the font is assembled
        Write16(head, 50, 1);      // indexToLocFormat: long offsets

        byte[] hhea = CopyTable(font, "hhea");
        Write16(hhea, 34, kept.Count);

        byte[] maxp = CopyTable(font, "maxp");
        Write16(maxp, 4, kept.Count);

        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["glyf"] = glyf.ToArray(),
            ["head"] = head,
            ["hhea"] = hhea,
            ["hmtx"] = hmtx,
            ["loca"] = loca,
            ["maxp"] = maxp,
            ["post"] = BuildPost(font),
        };

        foreach (string tag in CopiedTables)
        {
            if (font.Tables.ContainsKey(tag))
            {
                tables[tag] = CopyTable(font, tag);
            }
        }

        return new Result(Assemble(tables), newIds);
    }

    /// <summary>
    /// Extracts the whole font as a standalone file - every table, unchanged - for fonts whose
    /// licence forbids subsetting. Glyph ids are not renumbered.
    /// </summary>
    public static byte[] Whole(TrueTypeFont font)
    {
        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);

        foreach (string tag in font.Tables.Keys)
        {
            tables[tag] = CopyTable(font, tag);
        }

        Write32(tables["head"], 8, 0);

        return Assemble(tables);
    }

    /// <summary>
    /// The used glyphs plus .notdef plus, transitively, every component a composite glyph is
    /// built from - an accented letter is often a base letter and an accent placed together.
    /// </summary>
    private static List<ushort> Closure(TrueTypeFont font, IEnumerable<ushort> usedGlyphs)
    {
        var kept = new SortedSet<ushort> { 0 };
        var pending = new Queue<ushort>();

        foreach (ushort glyph in usedGlyphs)
        {
            if (glyph < font.GlyphCount && kept.Add(glyph))
            {
                pending.Enqueue(glyph);
            }
        }

        // .notdef itself may be composite.
        pending.Enqueue(0);

        while (pending.Count > 0)
        {
            byte[] data = font.GetGlyphData(pending.Dequeue()).ToArray();

            if (data.Length == 0 || I16(data, 0) >= 0)
            {
                continue;
            }

            foreach (int position in ComponentIndexPositions(data))
            {
                ushort component = U16(data, position);

                if (component < font.GlyphCount && kept.Add(component))
                {
                    pending.Enqueue(component);
                }
            }
        }

        return kept.ToList();
    }

    private static void RemapComponents(byte[] glyph, IReadOnlyDictionary<ushort, ushort> newIds)
    {
        foreach (int position in ComponentIndexPositions(glyph))
        {
            Write16(glyph, position, newIds[U16(glyph, position)]);
        }
    }

    /// <summary>Offsets of the glyph-index field in each component record of a composite glyph.</summary>
    private static IEnumerable<int> ComponentIndexPositions(byte[] glyph)
    {
        // A glyph header is ten bytes: contour count and bounding box.
        int position = 10;

        while (true)
        {
            int flags = U16(glyph, position);

            yield return position + 2;

            position += 4;
            position += (flags & ArgsAreWords) != 0 ? 4 : 2;

            if ((flags & HasScale) != 0)
            {
                position += 2;
            }
            else if ((flags & HasXYScale) != 0)
            {
                position += 4;
            }
            else if ((flags & HasTwoByTwo) != 0)
            {
                position += 8;
            }

            if ((flags & MoreComponents) == 0)
            {
                yield break;
            }
        }
    }

    /// <summary>
    /// A version 3 'post' table: the header metrics without glyph names, which renumbering would
    /// invalidate and which a PDF reader never consults.
    /// </summary>
    private static byte[] BuildPost(TrueTypeFont font)
    {
        var post = new byte[32];

        if (font.Tables.TryGetValue("post", out var original))
        {
            Array.Copy(font.Data, original.Offset, post, 0, Math.Min(32, original.Length));
        }

        Write32(post, 0, 0x00030000);

        return post;
    }

    private static byte[] CopyTable(TrueTypeFont font, string tag)
    {
        (int offset, int length) = font.Tables[tag];

        return font.Data.AsSpan(offset, length).ToArray();
    }

    /// <summary>
    /// Writes a table directory and the tables, four-byte aligned, then sets the head table's
    /// whole-font checksum adjustment.
    /// </summary>
    private static byte[] Assemble(SortedDictionary<string, byte[]> tables)
    {
        int count = tables.Count;
        int power = 1;
        int log = 0;

        while (power * 2 <= count)
        {
            power *= 2;
            log++;
        }

        int headerLength = 12 + (count * 16);
        int total = headerLength + tables.Values.Sum(t => Padded(t.Length));
        var font = new byte[total];

        Write32(font, 0, 0x00010000);
        Write16(font, 4, count);
        Write16(font, 6, power * 16);
        Write16(font, 8, log);
        Write16(font, 10, (count * 16) - (power * 16));

        int record = 12;
        int offset = headerLength;
        int headOffset = -1;

        foreach ((string tag, byte[] table) in tables)
        {
            for (int i = 0; i < 4; i++)
            {
                font[record + i] = (byte)tag[i];
            }

            Write32(font, record + 4, Checksum(table));
            Write32(font, record + 8, (uint)offset);
            Write32(font, record + 12, (uint)table.Length);

            table.CopyTo(font, offset);

            if (tag == "head")
            {
                headOffset = offset;
            }

            record += 16;
            offset += Padded(table.Length);
        }

        if (headOffset >= 0)
        {
            Write32(font, headOffset + 8, unchecked(0xB1B0AFBA - Checksum(font)));
        }

        return font;
    }

    private static int Padded(int length) => (length + 3) & ~3;

    private static uint Checksum(byte[] data)
    {
        uint sum = 0;

        for (int i = 0; i < data.Length; i += 4)
        {
            uint word = 0;

            for (int j = 0; j < 4; j++)
            {
                word = (word << 8) | (i + j < data.Length ? data[i + j] : (byte)0);
            }

            sum = unchecked(sum + word);
        }

        return sum;
    }
}
