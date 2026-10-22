using System.Text;
using System.Text.RegularExpressions;
using Theoistic.PDF.Writing;
using Theoistic.PDF.Writing.Fonts;

namespace Theoistic.PDF.Tests;

/// <summary>
/// Covers loading, measuring, subsetting and embedding TrueType fonts, against synthetic fonts
/// made by Fixtures/generate.py so the results do not depend on what is installed.
/// <para>
/// The test fonts have eleven glyphs at 1000 units per em: .notdef, space, A (600), B (580),
/// L (520), a (500), stroke, acute, Lslash (a composite of L and stroke), aacute (a composite of
/// a and a scaled acute) and, in all but the BMP variant, a smile at U+1F600.
/// </para>
/// </summary>
[TestClass]
public sealed class TrueTypeFontTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Fonts", name);

    private static TrueTypeFont Sans => TrueTypeFont.Load(Fixture("TestSans.ttf"));

    private static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    // ---------------------------------------------------------------- loading

    [TestMethod]
    public void Load_ReadsNamesAndGlyphCount()
    {
        TrueTypeFont font = Sans;

        Assert.AreEqual("Theoistic Test Sans", font.FamilyName);
        Assert.AreEqual("Regular", font.SubfamilyName);
        Assert.AreEqual("TheoisticTestSans-Regular", font.PostScriptName);
        Assert.AreEqual(11, font.GlyphCount);
        Assert.IsFalse(font.IsBold);
    }

    [TestMethod]
    public void Load_DetectsBold()
    {
        Assert.IsTrue(TrueTypeFont.Load(Fixture("TestSans-Bold.ttf")).IsBold);
    }

    [TestMethod]
    public void Metrics_AreScaledToThousandths()
    {
        Assert.AreEqual(800, Sans.Ascent, 1e-9);
        Assert.AreEqual(-200, Sans.Descent, 1e-9);
    }

    [TestMethod]
    public void Load_FromStreamAndBytesMatchesFile()
    {
        byte[] data = File.ReadAllBytes(Fixture("TestSans.ttf"));

        using var stream = new MemoryStream(data);

        Assert.AreEqual(Sans.GlyphCount, TrueTypeFont.Load(stream).GlyphCount);
        Assert.AreEqual(Sans.GlyphCount, TrueTypeFont.Load(data).GlyphCount);
    }

    [TestMethod]
    public void Load_RejectsCffOutlinesClearly()
    {
        NotSupportedException error = Assert.ThrowsException<NotSupportedException>(
            () => TrueTypeFont.Load(Fixture("TestCff.otf")));

        Assert.IsTrue(error.Message.Contains("CFF", StringComparison.Ordinal), error.Message);
    }

    [TestMethod]
    public void Load_RefusesARestrictedLicenceFont()
    {
        // The font's own licence bits forbid embedding, and honouring them is the point.
        NotSupportedException error = Assert.ThrowsException<NotSupportedException>(
            () => TrueTypeFont.Load(Fixture("TestRestricted.ttf")));

        Assert.IsTrue(error.Message.Contains("restricted", StringComparison.OrdinalIgnoreCase), error.Message);
    }

    [TestMethod]
    public void Load_RejectsSomethingThatIsNotAFont()
    {
        Assert.ThrowsException<InvalidDataException>(() => TrueTypeFont.Load(Encoding.ASCII.GetBytes("definitely not a font file")));
    }

    [TestMethod]
    public void Load_RejectsATruncatedFont()
    {
        byte[] data = File.ReadAllBytes(Fixture("TestSans.ttf"));

        Assert.ThrowsException<InvalidDataException>(() => TrueTypeFont.Load(data.AsSpan(0, 200).ToArray()));
    }

    // ---------------------------------------------------------------- collections

    [TestMethod]
    public void Collection_SelectsTheMemberByIndex()
    {
        Assert.IsFalse(TrueTypeFont.Load(Fixture("TestCollection.ttc"), 0).IsBold);
        Assert.IsTrue(TrueTypeFont.Load(Fixture("TestCollection.ttc"), 1).IsBold);
    }

    [TestMethod]
    public void Collection_RejectsAnIndexPastTheEnd()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => TrueTypeFont.Load(Fixture("TestCollection.ttc"), 2));
    }

    [TestMethod]
    public void SingleFont_RejectsANonZeroIndex()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => TrueTypeFont.Load(Fixture("TestSans.ttf"), 1));
    }

    // ---------------------------------------------------------------- measuring and coverage

    [TestMethod]
    public void GetTextWidth_UsesTheFontsOwnAdvances()
    {
        Assert.AreEqual((600 + 580) * 10 / 1000.0, Sans.GetTextWidth("AB", 10), 1e-9);
    }

    [TestMethod]
    public void GetTextWidth_HandlesASupplementaryPlaneCharacter()
    {
        // U+1F600 is a surrogate pair in .NET; it must measure as one glyph, via format 12.
        Assert.AreEqual(1000 * 12 / 1000.0, Sans.GetTextWidth("\U0001F600", 12), 1e-9);
    }

    [TestMethod]
    public void FindUnsupported_ReturnsNullWhenEverythingIsCovered()
    {
        Assert.IsNull(Sans.FindUnsupported("ABa Łá"));
    }

    [TestMethod]
    public void FindUnsupported_NamesTheFirstMissingCharacter()
    {
        Assert.AreEqual("Z", Sans.FindUnsupported("ABZ"));
    }

    [TestMethod]
    public void FormatFourOnlyFont_MapsTheBmpButNotBeyond()
    {
        var bmp = TrueTypeFont.Load(Fixture("TestSansBmp.ttf"));

        Assert.IsNull(bmp.FindUnsupported("ABŁ"));
        Assert.AreEqual("\U0001F600", bmp.FindUnsupported("A\U0001F600"));
    }

    // ---------------------------------------------------------------- subsetting

    [TestMethod]
    public void Subset_KeepsTheComponentsOfACompositeGlyph()
    {
        TrueTypeFont font = Sans;
        ushort lslash = font.GetGlyphId(0x0141);

        TrueTypeSubsetter.Result subset = TrueTypeSubsetter.Subset(font, new[] { lslash });

        // .notdef, the composite, and the two glyphs it is built from - nothing else.
        CollectionAssert.AreEquivalent(
            new ushort[] { 0, font.GetGlyphId('L'), GlyphIdByName(font, "stroke"), lslash },
            subset.NewGlyphIds.Keys.ToArray());
    }

    [TestMethod]
    public void Subset_FollowsAComponentWithAScale()
    {
        // The acute in aacute is scaled, which inserts a field into its component record. A
        // walker that ignores the flag reads the next record from the wrong offset.
        TrueTypeFont font = Sans;
        ushort aacute = font.GetGlyphId(0x00E1);

        TrueTypeSubsetter.Result subset = TrueTypeSubsetter.Subset(font, new[] { aacute });

        Assert.IsTrue(subset.NewGlyphIds.ContainsKey(font.GetGlyphId('a')), "the base letter was dropped");
        Assert.IsTrue(subset.NewGlyphIds.ContainsKey(GlyphIdByName(font, "acute")), "the scaled accent was dropped");
    }

    [TestMethod]
    public void Subset_RenumbersCompositeReferences()
    {
        TrueTypeFont font = Sans;
        ushort lslash = font.GetGlyphId(0x0141);

        TrueTypeSubsetter.Result subset = TrueTypeSubsetter.Subset(font, new[] { lslash });
        SubsetFont parsed = SubsetFont.Parse(subset.Font);

        byte[] composite = parsed.Glyph(subset.NewGlyphIds[lslash]);

        // First component's glyph index sits two bytes into the first record, after the header.
        ushort first = (ushort)((composite[12] << 8) | composite[13]);

        Assert.AreEqual(subset.NewGlyphIds[font.GetGlyphId('L')], first, "component index was not renumbered");
        Assert.AreEqual(4, parsed.NumGlyphs);
    }

    [TestMethod]
    public void Subset_HasAValidWholeFontChecksum()
    {
        TrueTypeSubsetter.Result subset = TrueTypeSubsetter.Subset(Sans, new ushort[] { Sans.GetGlyphId('A') });

        // The head table's adjustment is defined to bring the whole file's sum to this constant.
        Assert.AreEqual(0xB1B0AFBAu, SubsetFont.Checksum(subset.Font));
    }

    // ---------------------------------------------------------------- embedding

    private static string Embed(TrueTypeFont font, string text)
    {
        var document = new PdfDocument();
        document.AddPage(PdfPaperSize.A4).Content.DrawText(text, font, 12, 50, 700);

        return Latin1(document.ToArray());
    }

    [TestMethod]
    public void Embed_WritesACompositeFontWithAToUnicodeMap()
    {
        string pdf = Embed(Sans, "ABŁ");

        Assert.IsTrue(pdf.Contains("/Subtype /Type0", StringComparison.Ordinal), "no Type0 font");
        Assert.IsTrue(pdf.Contains("/Encoding /Identity-H", StringComparison.Ordinal), "not Identity-H");
        Assert.IsTrue(pdf.Contains("/Subtype /CIDFontType2", StringComparison.Ordinal), "no CIDFontType2 descendant");
        Assert.IsTrue(pdf.Contains("/FontFile2", StringComparison.Ordinal), "the font program is not embedded");
        Assert.IsTrue(pdf.Contains("/ToUnicode", StringComparison.Ordinal), "text would not be copyable");
        Assert.IsTrue(pdf.Contains("/Length1", StringComparison.Ordinal), "FontFile2 needs Length1");
    }

    [TestMethod]
    public void Embed_TagsTheSubsetName()
    {
        Assert.IsTrue(
            Regex.IsMatch(Embed(Sans, "AB"), @"/BaseFont /[A-Z]{6}\+TheoisticTestSans-Regular"),
            "a subset must carry a six-letter tag");
    }

    [TestMethod]
    public void Embed_MapsOriginalGlyphIdsThroughCidToGidMap()
    {
        Assert.IsTrue(Regex.IsMatch(Embed(Sans, "A"), @"/CIDToGIDMap \d+ 0 R"), "a renumbered subset needs a map stream");
    }

    [TestMethod]
    public void Embed_NoSubsettingFontGoesInWhole()
    {
        string pdf = Embed(TrueTypeFont.Load(Fixture("TestNoSubset.ttf")), "AB");

        Assert.IsTrue(pdf.Contains("/CIDToGIDMap /Identity", StringComparison.Ordinal), "a whole font keeps glyph ids");
        Assert.IsFalse(Regex.IsMatch(pdf, @"/BaseFont /[A-Z]{6}\+"), "a whole font must not be tagged as a subset");
    }

    [TestMethod]
    public void Embed_ShowsTextAsTwoByteGlyphCodes()
    {
        TrueTypeFont font = Sans;
        var document = new PdfDocument();
        PdfPage page = document.AddPage(PdfPaperSize.A4);

        page.Content.DrawText("AB", font, 12, 50, 700);

        string content = Latin1(page.Content.ToArray());
        string expected = $"<{font.GetGlyphId('A'):X4}{font.GetGlyphId('B'):X4}> Tj";

        Assert.IsTrue(content.Contains(expected, StringComparison.Ordinal), content);
    }

    [TestMethod]
    public void Embed_RejectsACharacterTheFontLacksAndNamesIt()
    {
        var document = new PdfDocument();
        PdfPage page = document.AddPage(PdfPaperSize.A4);

        ArgumentException error = Assert.ThrowsException<ArgumentException>(
            () => page.Content.DrawText("AZ", Sans, 12, 50, 700));

        Assert.IsTrue(error.Message.Contains("U+005A", StringComparison.Ordinal), error.Message);
        Assert.IsTrue(error.Message.Contains("Theoistic Test Sans", StringComparison.Ordinal), error.Message);
    }

    [TestMethod]
    public void Embed_IsReproducible()
    {
        TrueTypeFont font = Sans;

        byte[] Build()
        {
            var document = new PdfDocument();
            document.AddPage(PdfPaperSize.A4).Content.DrawText("ABŁá", font, 12, 50, 700);

            return document.ToArray();
        }

        CollectionAssert.AreEqual(Build(), Build());
    }

    [TestMethod]
    public void Embed_SavingTwiceProducesTheSameBytes()
    {
        var document = new PdfDocument();
        document.AddPage(PdfPaperSize.A4).Content.DrawText("AB", Sans, 12, 50, 700);

        CollectionAssert.AreEqual(document.ToArray(), document.ToArray());
    }

    [TestMethod]
    public void Embed_TextDrawnAfterASaveIsIncludedInTheNextOne()
    {
        TrueTypeFont font = Sans;
        var document = new PdfDocument();
        PdfPage page = document.AddPage(PdfPaperSize.A4);

        page.Content.DrawText("A", font, 12, 50, 700);
        byte[] first = document.ToArray();

        page.Content.DrawText("B", font, 12, 50, 680);
        byte[] second = document.ToArray();

        // The widths array lists every used glyph, so B appearing there shows the subset was
        // rebuilt rather than frozen at the first save.
        string widthsB = $"/W [{font.GetGlyphId('A')} [600 580]]";

        Assert.IsFalse(Latin1(first).Contains(widthsB, StringComparison.Ordinal));
        Assert.IsTrue(Latin1(second).Contains(widthsB, StringComparison.Ordinal), Latin1(second));
    }

    [TestMethod]
    public void Embed_OneFontServesTwoDocumentsWithSeparateSubsets()
    {
        TrueTypeFont font = Sans;

        var first = new PdfDocument();
        first.AddPage(PdfPaperSize.A4).Content.DrawText("A", font, 12, 50, 700);

        var second = new PdfDocument();
        second.AddPage(PdfPaperSize.A4).Content.DrawText("B", font, 12, 50, 700);

        // Matched as whole width arrays: a bare number could just as well be a byte offset.
        Assert.IsTrue(Latin1(first.ToArray()).Contains($"/W [{font.GetGlyphId('A')} [600]]", StringComparison.Ordinal));
        Assert.IsTrue(Latin1(second.ToArray()).Contains($"/W [{font.GetGlyphId('B')} [580]]", StringComparison.Ordinal), "the second document's subset is wrong");
    }

    [TestMethod]
    public void SaveAndRestore_TrackTheFontForEncoding()
    {
        // Standard fonts take one byte per character and embedded ones two, so after Restore
        // the stream has to encode for whichever font the restored state holds.
        var document = new PdfDocument();
        PdfPage page = document.AddPage(PdfPaperSize.A4);

        page.Content
            .BeginText()
            .SetFont(StandardFont.Helvetica, 12)
            .EndText()
            .Save()
            .BeginText()
            .SetFont(Sans, 12)
            .ShowText("A")
            .EndText()
            .Restore()
            .BeginText()
            .ShowText("Z")
            .EndText();

        string content = Latin1(page.Content.ToArray());

        Assert.IsTrue(content.Contains("(Z) Tj", StringComparison.Ordinal), "after Restore the text should be WinAnsi again: " + content);
    }

    [TestMethod]
    public void ShowText_WithNoFontSelectedIsRejected()
    {
        var document = new PdfDocument();
        PdfPage page = document.AddPage(PdfPaperSize.A4);

        Assert.ThrowsException<InvalidOperationException>(() => page.Content.BeginText().ShowText("x"));
    }

    // ---------------------------------------------------------------- helpers

    private static ushort GlyphIdByName(TrueTypeFont font, string name) => name switch
    {
        // Glyph order is fixed by the generator: .notdef, space, A, B, L, a, stroke, acute, ...
        "stroke" => 6,
        "acute" => 7,
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };

    /// <summary>Just enough of a font reader to look inside a subset, which has no cmap.</summary>
    private sealed class SubsetFont
    {
        private readonly byte[] data;
        private readonly Dictionary<string, (int Offset, int Length)> tables = new();

        private SubsetFont(byte[] data)
        {
            this.data = data;

            int count = U16(4);

            for (int i = 0; i < count; i++)
            {
                int record = 12 + (i * 16);
                string tag = Encoding.ASCII.GetString(data, record, 4);

                tables[tag] = ((int)U32(record + 8), (int)U32(record + 12));
            }
        }

        public static SubsetFont Parse(byte[] data) => new SubsetFont(data);

        public int NumGlyphs => U16(tables["maxp"].Offset + 4);

        public byte[] Glyph(int glyph)
        {
            int loca = tables["loca"].Offset;
            int start = (int)U32(loca + (glyph * 4));
            int end = (int)U32(loca + ((glyph + 1) * 4));

            return data.AsSpan(tables["glyf"].Offset + start, end - start).ToArray();
        }

        public static uint Checksum(byte[] data)
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

        private int U16(int offset) => (data[offset] << 8) | data[offset + 1];

        private uint U32(int offset) =>
            ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3];
    }
}
