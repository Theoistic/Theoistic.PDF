using System.Text;
using Theoistic.PDF.Writing.Fonts;
using static Theoistic.PDF.Writing.Fonts.BigEndian;

namespace Theoistic.PDF.Writing;

/// <summary>
/// A TrueType font loaded from a file, embedded into documents as a subset of the glyphs they
/// actually use.
/// <para>
/// This is what lifts the WinAnsi limit of <see cref="StandardFont"/>: text is mapped through
/// the font's own character map, so whatever the font covers - Polish, Czech, Turkish,
/// Cyrillic, Greek, CJK - can be shown. Only the glyphs a document draws are written into it,
/// so embedding a large font costs a few kilobytes rather than megabytes.
/// </para>
/// <para>
/// Supported: TrueType-outline fonts (<c>.ttf</c>) and collections (<c>.ttc</c>). Not supported:
/// CFF-outline OpenType (<c>.otf</c> beginning with <c>OTTO</c>), which is rejected with a clear
/// error. Text is laid out one glyph per character with no kerning, ligatures or complex-script
/// shaping, so Arabic, Hebrew and Indic scripts will not render correctly - use the HTML path for
/// those.
/// </para>
/// <para>
/// The font's own embedding permission is honoured. A font marked restricted-licence is refused,
/// and a font marked no-subsetting is embedded whole.
/// </para>
/// </summary>
public sealed class TrueTypeFont : PdfFont
{
    private readonly Dictionary<string, (int Offset, int Length)> tables;
    private readonly CharacterMap characterMap;
    private readonly ushort[] advances;
    private readonly int[] glyphOffsets;

    private TrueTypeFont(byte[] data, int collectionIndex)
    {
        Data = data;

        int fontOffset = ResolveFontOffset(data, collectionIndex);
        uint version = U32(data, fontOffset);

        if (version == 0x4F54544F)
        {
            throw new NotSupportedException(
                "This is a CFF-outline OpenType font (it starts with 'OTTO'). Only TrueType-outline fonts are supported; most families also ship a .ttf build.");
        }

        if (version != 0x00010000 && version != 0x74727565)
        {
            throw new InvalidDataException("This is not a TrueType font file.");
        }

        tables = ReadTableDirectory(data, fontOffset);

        foreach (string required in new[] { "head", "hhea", "maxp", "hmtx", "loca", "glyf", "cmap" })
        {
            if (!tables.ContainsKey(required))
            {
                throw new InvalidDataException($"The font has no '{required}' table, which embedding requires.");
            }
        }

        int head = tables["head"].Offset;
        UnitsPerEm = U16(data, head + 18);

        if (UnitsPerEm == 0)
        {
            throw new InvalidDataException("The font declares zero units per em.");
        }

        BoundingBox = (I16(data, head + 36), I16(data, head + 38), I16(data, head + 40), I16(data, head + 42));
        int macStyle = U16(data, head + 44);
        bool longOffsets = I16(data, head + 50) == 1;

        int hhea = tables["hhea"].Offset;
        HheaAscender = I16(data, hhea + 4);
        HheaDescender = I16(data, hhea + 6);
        int horizontalMetrics = U16(data, hhea + 34);

        GlyphCount = U16(data, tables["maxp"].Offset + 4);
        HorizontalMetricsCount = horizontalMetrics;

        advances = ReadAdvances(data, tables["hmtx"].Offset, horizontalMetrics, GlyphCount);
        glyphOffsets = ReadGlyphOffsets(data, tables["loca"].Offset, GlyphCount, longOffsets);

        int weight = 400;
        int selection = 0;

        if (tables.TryGetValue("OS/2", out var os2))
        {
            int table = os2.Offset;
            int os2Version = U16(data, table);

            weight = U16(data, table + 4);
            EmbeddingFlags = U16(data, table + 8);
            selection = U16(data, table + 62);

            if (os2Version >= 2 && os2.Length >= 90)
            {
                CapHeight = I16(data, table + 88);
            }
        }

        if (tables.TryGetValue("post", out var post))
        {
            ItalicAngle = I32(data, post.Offset + 4) / 65536.0;
            IsFixedPitch = U32(data, post.Offset + 12) != 0;
        }

        Weight = weight;
        bold = (selection & 0x20) != 0 || (macStyle & 0x01) != 0 || weight >= 600;
        italic = (selection & 0x01) != 0 || (macStyle & 0x02) != 0 || ItalicAngle != 0;

        Dictionary<int, string> names = tables.TryGetValue("name", out var name)
            ? ReadNames(data, name.Offset)
            : new Dictionary<int, string>();

        FamilyName = names.GetValueOrDefault(1) ?? "Unknown";
        SubfamilyName = names.GetValueOrDefault(2) ?? "Regular";
        PostScriptName = SanitisePostScriptName(
            names.GetValueOrDefault(6) ?? names.GetValueOrDefault(4) ?? FamilyName);

        characterMap = CharacterMap.Read(data, tables["cmap"].Offset);

        if (IsRestricted)
        {
            throw new NotSupportedException(
                $"'{FamilyName} {SubfamilyName}' is marked as restricted-licence: its licence does not permit embedding it in a document.");
        }

        if ((EmbeddingFlags & 0x0200) != 0)
        {
            throw new NotSupportedException(
                $"'{FamilyName} {SubfamilyName}' permits embedding only its bitmaps, not its outlines.");
        }
    }

    private readonly bool bold;
    private readonly bool italic;

    /// <summary>Loads a font from a <c>.ttf</c> or <c>.ttc</c> file.</summary>
    /// <param name="collectionIndex">Which font to use when the file is a collection.</param>
    public static TrueTypeFont Load(string path, int collectionIndex = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return Load(File.ReadAllBytes(path), collectionIndex);
    }

    public static TrueTypeFont Load(Stream stream, int collectionIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);

        return Load(buffer.ToArray(), collectionIndex);
    }

    public static TrueTypeFont Load(byte[] data, int collectionIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (data.Length < 12)
        {
            throw new InvalidDataException("The data is too short to be a font file.");
        }

        return new TrueTypeFont(data, collectionIndex);
    }

    // ---------------------------------------------------------------- identity and metrics

    public override string Name => PostScriptName;

    public string PostScriptName { get; }

    public string FamilyName { get; }

    public string SubfamilyName { get; }

    public override bool IsBold => bold;

    public override bool IsItalic => italic;

    public int GlyphCount { get; }

    public override double Ascent => HheaAscender * 1000.0 / UnitsPerEm;

    public override double Descent => HheaDescender * 1000.0 / UnitsPerEm;

    /// <inheritdoc />
    public override double GetTextWidth(string text, double size)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (size <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "Font size must be positive.");
        }

        long total = 0;

        foreach (Rune rune in text.EnumerateRunes())
        {
            // A missing glyph measures as .notdef rather than throwing, so that a caller can
            // measure freely and find out about coverage from FindUnsupported or at draw time.
            total += GetAdvance(GetGlyphId(rune.Value));
        }

        return total * size / UnitsPerEm;
    }

    /// <inheritdoc />
    public override string? FindUnsupported(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        foreach (Rune rune in text.EnumerateRunes())
        {
            if (GetGlyphId(rune.Value) == 0)
            {
                return rune.ToString();
            }
        }

        return null;
    }

    internal override FontResource CreateResource(PdfDocument document) =>
        TrueTypeFontResource.Create(this, document);

    // ---------------------------------------------------------------- internals for embedding

    internal byte[] Data { get; }

    internal int UnitsPerEm { get; }

    internal (short XMin, short YMin, short XMax, short YMax) BoundingBox { get; }

    internal short HheaAscender { get; }

    internal short HheaDescender { get; }

    internal int HorizontalMetricsCount { get; }

    internal int EmbeddingFlags { get; }

    internal int Weight { get; }

    internal short? CapHeight { get; }

    internal double ItalicAngle { get; }

    internal bool IsFixedPitch { get; }

    /// <summary>Licence bits 0-3 equal to 2 means restricted: the font may not be embedded at all.</summary>
    internal bool IsRestricted => (EmbeddingFlags & 0x000F) == 0x0002;

    /// <summary>Bit 8: the font may be embedded, but only whole.</summary>
    internal bool AllowsSubsetting => (EmbeddingFlags & 0x0100) == 0;

    internal IReadOnlyDictionary<string, (int Offset, int Length)> Tables => tables;

    internal ushort GetGlyphId(int codePoint) => characterMap.Lookup(codePoint);

    /// <summary>Advance width in font units.</summary>
    internal int GetAdvance(ushort glyph) => glyph < advances.Length ? advances[glyph] : 0;

    /// <summary>The bytes of one glyph's outline, which may be empty for a space.</summary>
    internal ReadOnlySpan<byte> GetGlyphData(ushort glyph)
    {
        if (glyph >= GlyphCount)
        {
            return ReadOnlySpan<byte>.Empty;
        }

        (int glyf, int length) = tables["glyf"];
        int start = glyphOffsets[glyph];
        int end = glyphOffsets[glyph + 1];

        if (end <= start)
        {
            return ReadOnlySpan<byte>.Empty;
        }

        if (end > length)
        {
            throw new InvalidDataException($"Glyph {glyph} extends past the end of the 'glyf' table.");
        }

        return Data.AsSpan(glyf + start, end - start);
    }

    /// <summary>The left side bearing of a glyph, in font units, as the hmtx table records it.</summary>
    internal short GetLeftSideBearing(ushort glyph)
    {
        int hmtx = tables["hmtx"].Offset;

        return glyph < HorizontalMetricsCount
            ? I16(Data, hmtx + (glyph * 4) + 2)
            : I16(Data, hmtx + (HorizontalMetricsCount * 4) + ((glyph - HorizontalMetricsCount) * 2));
    }

    // ---------------------------------------------------------------- parsing

    private static int ResolveFontOffset(byte[] data, int collectionIndex)
    {
        if (Tag(data, 0) != "ttcf")
        {
            if (collectionIndex != 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(collectionIndex),
                    collectionIndex,
                    "The file holds a single font, so the only valid collection index is 0.");
            }

            return 0;
        }

        int count = (int)U32(data, 8);

        if (collectionIndex < 0 || collectionIndex >= count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(collectionIndex),
                collectionIndex,
                $"The collection holds {count} font(s); valid indices are 0 to {count - 1}.");
        }

        // Table offsets inside a collection member are measured from the start of the file, not
        // from the member, which is what lets members share tables.
        return (int)U32(data, 12 + (collectionIndex * 4));
    }

    private static Dictionary<string, (int, int)> ReadTableDirectory(byte[] data, int fontOffset)
    {
        int count = U16(data, fontOffset + 4);
        var result = new Dictionary<string, (int, int)>(StringComparer.Ordinal);

        for (int i = 0; i < count; i++)
        {
            int record = fontOffset + 12 + (i * 16);
            string tag = Tag(data, record);
            int offset = (int)U32(data, record + 8);
            int length = (int)U32(data, record + 12);

            Require(data, offset, length);

            result[tag] = (offset, length);
        }

        return result;
    }

    private static ushort[] ReadAdvances(byte[] data, int hmtx, int metrics, int glyphs)
    {
        if (metrics == 0)
        {
            throw new InvalidDataException("The font declares no horizontal metrics.");
        }

        var result = new ushort[glyphs];
        ushort last = 0;

        for (int i = 0; i < glyphs; i++)
        {
            // Glyphs past the last full metric share its advance - that is how monospaced and
            // CJK fonts keep the table small.
            if (i < metrics)
            {
                last = U16(data, hmtx + (i * 4));
            }

            result[i] = last;
        }

        return result;
    }

    private static int[] ReadGlyphOffsets(byte[] data, int loca, int glyphs, bool longOffsets)
    {
        var result = new int[glyphs + 1];

        for (int i = 0; i <= glyphs; i++)
        {
            result[i] = longOffsets
                ? (int)U32(data, loca + (i * 4))

                // Short offsets are stored halved, which is why they can only address even bytes.
                : U16(data, loca + (i * 2)) * 2;
        }

        return result;
    }

    /// <summary>
    /// Reads the family, subfamily, full and PostScript names. Windows Unicode English is
    /// preferred; anything else is used only when that is missing.
    /// </summary>
    private static Dictionary<int, string> ReadNames(byte[] data, int name)
    {
        int count = U16(data, name + 2);
        int storage = name + U16(data, name + 4);

        var best = new Dictionary<int, (int Score, string Value)>();

        for (int i = 0; i < count; i++)
        {
            int record = name + 6 + (i * 12);
            int platform = U16(data, record);
            int encoding = U16(data, record + 2);
            int language = U16(data, record + 4);
            int id = U16(data, record + 6);
            int length = U16(data, record + 8);
            int offset = storage + U16(data, record + 10);

            if (id is not (1 or 2 or 4 or 6))
            {
                continue;
            }

            int score;
            string value;

            if (platform == 3 && encoding is 1 or 10 || platform == 0)
            {
                score = platform == 3 && language == 0x409 ? 3 : 2;
                Require(data, offset, length);
                value = Encoding.BigEndianUnicode.GetString(data, offset, length);
            }
            else if (platform == 1 && encoding == 0)
            {
                score = 1;
                Require(data, offset, length);
                value = Encoding.Latin1.GetString(data, offset, length);
            }
            else
            {
                continue;
            }

            if (!best.TryGetValue(id, out var current) || score > current.Score)
            {
                best[id] = (score, value.Trim());
            }
        }

        return best.ToDictionary(pair => pair.Key, pair => pair.Value.Value);
    }

    /// <summary>
    /// Keeps the name to the printable ASCII a PostScript name allows. A name that arrives with
    /// spaces or delimiters would otherwise be escaped into something no reader displays nicely.
    /// </summary>
    private static string SanitisePostScriptName(string name)
    {
        var result = new StringBuilder(name.Length);

        foreach (char c in name)
        {
            if (c is > ' ' and < '\u007F' and not ('[' or ']' or '(' or ')' or '{' or '}' or '<' or '>' or '/' or '%'))
            {
                result.Append(c);
            }
        }

        return result.Length > 0 ? result.ToString() : "EmbeddedFont";
    }
}
