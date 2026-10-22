using static Theoistic.PDF.Writing.Fonts.BigEndian;

namespace Theoistic.PDF.Writing.Fonts;

/// <summary>
/// Maps Unicode code points to glyph ids using one subtable of a font's <c>cmap</c>.
/// <para>
/// Formats 4 and 12 are supported. Format 4 is the near-universal BMP table; format 12 covers
/// the supplementary planes, which emoji and rarer CJK live in, and is preferred when present
/// because it is a superset. Lookups binary-search the table in place rather than expanding it,
/// because a CJK font maps tens of thousands of characters and a document uses a handful.
/// </para>
/// </summary>
internal sealed class CharacterMap
{
    private readonly byte[] data;
    private readonly int subtable;
    private readonly int format;
    private readonly bool symbol;
    private readonly Dictionary<int, ushort> cache = new Dictionary<int, ushort>();

    private CharacterMap(byte[] data, int subtable, int format, bool symbol)
    {
        this.data = data;
        this.subtable = subtable;
        this.format = format;
        this.symbol = symbol;
    }

    /// <summary>
    /// Chooses the most capable Unicode subtable in the cmap table at <paramref name="cmapOffset"/>.
    /// </summary>
    public static CharacterMap Read(byte[] data, int cmapOffset)
    {
        int count = U16(data, cmapOffset + 2);

        int best = -1;
        int bestFormat = 0;
        int bestScore = int.MinValue;
        bool bestSymbol = false;

        for (int i = 0; i < count; i++)
        {
            int record = cmapOffset + 4 + (i * 8);
            int platform = U16(data, record);
            int encoding = U16(data, record + 2);
            int offset = cmapOffset + (int)U32(data, record + 4);
            int subtableFormat = U16(data, offset);

            if (subtableFormat is not (4 or 12))
            {
                continue;
            }

            // Full-repertoire tables beat BMP-only ones, and Windows Unicode is preferred over
            // the Unicode platform only because it is what fonts are actually tested against.
            int score = (platform, encoding, subtableFormat) switch
            {
                (3, 10, 12) => 100,
                (0, 4 or 6, 12) => 90,
                (0, _, 12) => 80,
                (3, 1, 4) => 70,
                (0, _, 4) => 60,

                // A symbol font maps its glyphs into the private-use area at U+F0xx; it is a
                // last resort, looked up with that offset applied.
                (3, 0, 4) => 10,
                _ => int.MinValue
            };

            if (score > bestScore)
            {
                best = offset;
                bestFormat = subtableFormat;
                bestScore = score;
                bestSymbol = platform == 3 && encoding == 0;
            }
        }

        if (best < 0)
        {
            throw new NotSupportedException(
                "The font has no Unicode character map in a supported format (4 or 12), so text cannot be mapped to its glyphs.");
        }

        return new CharacterMap(data, best, bestFormat, bestSymbol);
    }

    /// <summary>Returns the glyph id for <paramref name="codePoint"/>, or 0 (.notdef) if unmapped.</summary>
    public ushort Lookup(int codePoint)
    {
        if (cache.TryGetValue(codePoint, out ushort cached))
        {
            return cached;
        }

        ushort glyph = format == 12 ? LookupFormat12(codePoint) : LookupFormat4(codePoint);

        if (glyph == 0 && symbol && codePoint is >= 0x20 and <= 0xFF)
        {
            glyph = LookupFormat4(0xF000 | codePoint);
        }

        cache[codePoint] = glyph;

        return glyph;
    }

    private ushort LookupFormat4(int codePoint)
    {
        if (codePoint > 0xFFFF)
        {
            return 0;
        }

        int segCount = U16(data, subtable + 6) / 2;
        int endCodes = subtable + 14;
        int startCodes = endCodes + (segCount * 2) + 2;
        int deltas = startCodes + (segCount * 2);
        int rangeOffsets = deltas + (segCount * 2);

        // The first segment whose end is at or past the code point is the only candidate.
        int low = 0;
        int high = segCount - 1;

        while (low < high)
        {
            int mid = (low + high) / 2;

            if (U16(data, endCodes + (mid * 2)) < codePoint)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        int segment = low;
        int start = U16(data, startCodes + (segment * 2));
        int end = U16(data, endCodes + (segment * 2));

        if (codePoint < start || codePoint > end)
        {
            return 0;
        }

        int delta = I16(data, deltas + (segment * 2));
        int rangeOffsetPosition = rangeOffsets + (segment * 2);
        int rangeOffset = U16(data, rangeOffsetPosition);

        if (rangeOffset == 0)
        {
            return (ushort)((codePoint + delta) & 0xFFFF);
        }

        // The offset is relative to its own position in the table, which is the format's
        // notorious way of indexing into the glyph id array that follows.
        int glyphAddress = rangeOffsetPosition + rangeOffset + ((codePoint - start) * 2);
        int glyph = U16(data, glyphAddress);

        return glyph == 0 ? (ushort)0 : (ushort)((glyph + delta) & 0xFFFF);
    }

    private ushort LookupFormat12(int codePoint)
    {
        int groups = (int)U32(data, subtable + 12);
        int first = subtable + 16;

        int low = 0;
        int high = groups - 1;

        while (low <= high)
        {
            int mid = (low + high) / 2;
            int group = first + (mid * 12);
            uint start = U32(data, group);
            uint end = U32(data, group + 4);

            if (codePoint < start)
            {
                high = mid - 1;
            }
            else if (codePoint > end)
            {
                low = mid + 1;
            }
            else
            {
                return (ushort)(U32(data, group + 8) + (codePoint - start));
            }
        }

        return 0;
    }
}
