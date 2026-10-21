using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Theoistic.PDF.Writing;

namespace Theoistic.PDF.Tests;

/// <summary>
/// Coverage over the from-scratch PDF writer: file structure, the cross-reference table, the
/// numeric and string syntax rules that readers are unforgiving about, and the standard font
/// metrics.
/// <para>
/// These tests avoid asserting on whole-file byte sequences. They parse the structure back out
/// instead, so that a harmless formatting change does not break them but a malformed offset
/// does.
/// </para>
/// </summary>
[TestClass]
public sealed class PdfWriterTests
{
    private static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    /// <summary>
    /// Builds the smallest document that exercises text: one page, one font, one string.
    /// </summary>
    private static byte[] SimpleDocument()
    {
        var document = new PdfDocument();
        PdfPage page = document.AddPage(PdfPaperSize.A4);

        page.Content.DrawText("Hello", StandardFont.Helvetica, 12, 72, 720);

        return document.ToArray();
    }

    // ---------------------------------------------------------------- file structure

    [TestMethod]
    public void Save_WritesHeaderWithBinaryMarker()
    {
        byte[] bytes = SimpleDocument();

        Assert.AreEqual("%PDF-1.7", Latin1(bytes)[..8]);

        // The four high-bit bytes on the second line are what makes transfer tools treat the
        // file as binary instead of rewriting its line endings.
        int marker = Array.IndexOf(bytes, (byte)'\n') + 1;

        Assert.AreEqual((byte)'%', bytes[marker]);
        Assert.IsTrue(bytes[marker + 1] > 127, "byte 1 of the binary marker is not high-bit");
        Assert.IsTrue(bytes[marker + 4] > 127, "byte 4 of the binary marker is not high-bit");
    }

    [TestMethod]
    public void Save_EndsWithStartxrefAndEof()
    {
        string text = Latin1(SimpleDocument());

        Assert.IsTrue(text.TrimEnd().EndsWith("%%EOF", StringComparison.Ordinal), "file does not end with %%EOF");
        Assert.IsTrue(text.Contains("startxref", StringComparison.Ordinal), "file has no startxref");
        Assert.IsTrue(text.Contains("trailer", StringComparison.Ordinal), "file has no trailer");
    }

    [TestMethod]
    public void Save_StartxrefPointsAtTheCrossReferenceTable()
    {
        byte[] bytes = SimpleDocument();
        string text = Latin1(bytes);

        Match match = Regex.Match(text, @"startxref\s*\n(\d+)");

        Assert.IsTrue(match.Success, "could not find a startxref offset");

        int offset = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);

        Assert.AreEqual("xref", text.Substring(offset, 4), "startxref does not point at the xref keyword");
    }

    [TestMethod]
    public void Save_CrossReferenceOffsetsPointAtTheirObjects()
    {
        byte[] bytes = SimpleDocument();
        string text = Latin1(bytes);

        int xrefStart = text.IndexOf("xref\n", StringComparison.Ordinal);
        Match header = Regex.Match(text[xrefStart..], @"xref\n0 (\d+)\n");

        Assert.IsTrue(header.Success, "the xref subsection header is malformed");

        int count = int.Parse(header.Groups[1].Value, CultureInfo.InvariantCulture);
        int entriesStart = xrefStart + header.Length;

        // Object 0 is the free-list head and points nowhere, so checking starts at 1.
        for (int number = 1; number < count; number++)
        {
            string entry = text.Substring(entriesStart + (number * 20), 20);
            int offset = int.Parse(entry[..10], CultureInfo.InvariantCulture);

            Assert.AreEqual(
                $"{number} 0 obj",
                text.Substring(offset, $"{number} 0 obj".Length),
                $"the xref entry for object {number} points at the wrong offset");
        }
    }

    [TestMethod]
    public void Save_CrossReferenceEntriesAreExactlyTwentyBytes()
    {
        string text = Latin1(SimpleDocument());

        int xrefStart = text.IndexOf("xref\n", StringComparison.Ordinal);
        Match header = Regex.Match(text[xrefStart..], @"xref\n0 (\d+)\n");
        int count = int.Parse(header.Groups[1].Value, CultureInfo.InvariantCulture);
        int entriesStart = xrefStart + header.Length;

        // Readers are allowed to seek straight to an entry by multiplying its number by twenty,
        // so every entry has to be padded to exactly that width.
        for (int number = 0; number < count; number++)
        {
            string entry = text.Substring(entriesStart + (number * 20), 20);

            Assert.IsTrue(
                Regex.IsMatch(entry, @"^\d{10} \d{5} [nf] \n$"),
                $"entry {number} is not a well-formed 20-byte record: '{entry}'");
        }
    }

    [TestMethod]
    public void Save_TrailerSizeMatchesTheObjectCount()
    {
        string text = Latin1(SimpleDocument());

        int count = int.Parse(
            Regex.Match(text[text.IndexOf("xref\n", StringComparison.Ordinal)..], @"xref\n0 (\d+)\n").Groups[1].Value,
            CultureInfo.InvariantCulture);

        Match size = Regex.Match(text, @"/Size (\d+)");

        Assert.IsTrue(size.Success, "the trailer has no /Size");
        Assert.AreEqual(count, int.Parse(size.Groups[1].Value, CultureInfo.InvariantCulture));
    }

    [TestMethod]
    public void Save_WithoutPages_Throws()
    {
        var document = new PdfDocument();

        Assert.ThrowsException<InvalidOperationException>(() => document.ToArray());
    }

    // ---------------------------------------------------------------- page tree

    [TestMethod]
    public void AddPage_RecordsEveryPageInTheTree()
    {
        var document = new PdfDocument();

        for (int i = 0; i < 3; i++)
        {
            document.AddPage(PdfPaperSize.A4).Content.DrawText("x", StandardFont.Helvetica, 10, 10, 10);
        }

        string text = Latin1(document.ToArray());

        Assert.AreEqual(3, document.Pages.Count);
        Assert.IsTrue(Regex.IsMatch(text, @"/Count 3"), "the page tree count is wrong");

        // Three leaves plus the tree node itself.
        Assert.AreEqual(3, Regex.Matches(text, @"/Type /Page\b").Count, "wrong number of page leaves");
        Assert.AreEqual(1, Regex.Matches(text, @"/Type /Pages\b").Count, "wrong number of tree nodes");
    }

    [TestMethod]
    public void AddPage_AppliesTheStandardPaperSize()
    {
        var document = new PdfDocument();

        document.AddPage(PdfPaperSize.A4);

        Assert.AreEqual(595, document.Pages[0].Width);
        Assert.AreEqual(842, document.Pages[0].Height);
    }

    [TestMethod]
    public void AddPage_SwapsTheAxesForLandscape()
    {
        var document = new PdfDocument();

        PdfPage page = document.AddPage(PdfPaperSize.A4, PdfOrientation.Landscape);

        Assert.AreEqual(842, page.Width);
        Assert.AreEqual(595, page.Height);
    }

    [TestMethod]
    public void AddPage_WithNoExtent_Throws()
    {
        var document = new PdfDocument();

        Assert.ThrowsException<ArgumentException>(() => document.AddPage(0, 100));
    }

    [TestMethod]
    public void GetFontObject_SharesOneDictionaryAcrossPages()
    {
        var document = new PdfDocument();

        foreach (int _ in Enumerable.Range(0, 3))
        {
            document.AddPage(PdfPaperSize.A4)
                .Content.DrawText("shared", StandardFont.Helvetica, 10, 10, 10);
        }

        string text = Latin1(document.ToArray());

        // One font dictionary however many pages reference it, because the object is cached on
        // the document rather than created per page.
        Assert.AreEqual(1, Regex.Matches(text, @"/BaseFont /Helvetica\b").Count);
    }

    // ---------------------------------------------------------------- numeric syntax

    [TestMethod]
    [DataRow(0.000001, "0")]
    [DataRow(1e-10, "0")]
    [DataRow(-1e-10, "0")]
    [DataRow(1234567.0, "1234567")]
    [DataRow(1e15, "1000000000000000")]
    [DataRow(0.5, "0.5")]
    [DataRow(-0.25, "-0.25")]
    [DataRow(1.0 / 3.0, "0.33333")]
    public void FormatReal_NeverUsesExponentNotation(double value, string expected)
    {
        // Exponent notation is not valid PDF syntax, which rules out the round-trip formats.
        var document = new PdfDocument();
        PdfPage page = document.AddPage(PdfPaperSize.A4);

        page.Content.MoveTo(value, 0).LineTo(1, 1).Stroke();

        // Read the operators off the content stream rather than the saved file, which has them
        // compressed.
        string content = Latin1(page.Content.ToArray());

        Assert.IsFalse(
            Regex.IsMatch(content, @"\d[eE][-+]?\d"),
            "the output contains exponent notation, which no PDF reader accepts");
        Assert.IsTrue(
            content.Contains($"{expected} 0 m", StringComparison.Ordinal),
            $"expected '{expected} 0 m', got: {content}");
    }

    [TestMethod]
    public void FormatReal_DoesNotEmitNegativeZero()
    {
        var document = new PdfDocument();

        PdfPage page = document.AddPage(PdfPaperSize.A4);

        page.Content.MoveTo(-0.0000001, 5).LineTo(1, 1).Stroke();

        string content = Latin1(page.Content.ToArray());

        Assert.IsFalse(content.Contains("-0 5 m", StringComparison.Ordinal), "a rounded-to-zero negative was written as -0");
        Assert.IsTrue(content.Contains("0 5 m", StringComparison.Ordinal), $"expected '0 5 m', got: {content}");
    }

    [TestMethod]
    public void PdfReal_RejectsNonFiniteValues()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new PdfReal(double.NaN));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new PdfReal(double.PositiveInfinity));
    }

    // ---------------------------------------------------------------- string and name syntax

    [TestMethod]
    public void ShowText_EscapesParenthesesAndBackslashes()
    {
        var document = new PdfDocument();

        PdfPage page = document.AddPage(PdfPaperSize.A4);

        page.Content.DrawText(@"a(b)c\d", StandardFont.Helvetica, 12, 10, 10);

        // Left unescaped, the closing parenthesis would end the string early and the rest of
        // the content stream would be read as operators.
        Assert.IsTrue(
            Latin1(page.Content.ToArray()).Contains(@"(a\(b\)c\\d)", StringComparison.Ordinal),
            "parentheses and backslashes were not escaped");
    }

    [TestMethod]
    public void ShowText_EscapesControlCharactersAsOctal()
    {
        var document = new PdfDocument();

        PdfPage page = document.AddPage(PdfPaperSize.A4);

        page.Content.DrawText("a\u0007b", StandardFont.Helvetica, 12, 10, 10);

        Assert.IsTrue(
            Latin1(page.Content.ToArray()).Contains(@"(a\007b)", StringComparison.Ordinal),
            "a control character was not octal-escaped");
    }

    [TestMethod]
    public void WriteName_EscapesDelimiters()
    {
        var document = new PdfDocument();
        PdfPage page = document.AddPage(PdfPaperSize.A4);

        var custom = new PdfDictionary();
        custom.Set("Odd Name/With#Delimiters", new PdfInteger(1));

        page.Dictionary.Set("Custom", custom);
        page.Content.DrawText("x", StandardFont.Helvetica, 10, 10, 10);

        string text = Latin1(document.ToArray());

        // A space, a solidus and a number sign would each terminate or corrupt the name token.
        Assert.IsTrue(
            text.Contains("/Odd#20Name#2FWith#23Delimiters", StringComparison.Ordinal),
            "name delimiters were not hex-escaped");
    }

    [TestMethod]
    public void PdfString_Utf16CarriesAByteOrderMark()
    {
        PdfString value = PdfString.Utf16("Ok");

        Assert.AreEqual(0xFE, value.Bytes[0]);
        Assert.AreEqual(0xFF, value.Bytes[1]);
        Assert.IsTrue(value.Hexadecimal, "UTF-16 metadata strings should use the hex form");
    }

    [TestMethod]
    public void PdfString_Latin1RejectsCharactersItCannotRepresent()
    {
        Assert.ThrowsException<ArgumentException>(() => PdfString.Latin1("中"));
    }

    // ---------------------------------------------------------------- compression

    [TestMethod]
    public void WriteStream_CompressedContentRoundTrips()
    {
        var document = new PdfDocument();
        PdfPage page = document.AddPage(PdfPaperSize.A4);

        // Enough repetition that the compressed form is unmistakably smaller than the input.
        for (int i = 0; i < 200; i++)
        {
            page.Content.DrawText("repeating line of text", StandardFont.Helvetica, 10, 40, 40 + i);
        }

        byte[] bytes = document.ToArray();
        string text = Latin1(bytes);

        Assert.IsTrue(text.Contains("/Filter /FlateDecode", StringComparison.Ordinal), "the stream was not filtered");

        // Pull the stream back out and inflate it, which is the only real check that the
        // zlib wrapper and the declared length agree.
        int start = text.IndexOf("stream\n", StringComparison.Ordinal) + "stream\n".Length;
        int length = int.Parse(Regex.Match(text, @"/Length (\d+)").Groups[1].Value, CultureInfo.InvariantCulture);

        using var compressed = new MemoryStream(bytes, start, length);
        using var inflate = new ZLibStream(compressed, CompressionMode.Decompress);
        using var inflated = new MemoryStream();

        inflate.CopyTo(inflated);

        string content = Latin1(inflated.ToArray());

        Assert.IsTrue(content.Contains("(repeating line of text) Tj", StringComparison.Ordinal),
            "the inflated content stream does not contain the drawn text");
        Assert.IsTrue(length < inflated.Length, "compression did not reduce the stream size");
    }

    [TestMethod]
    public void WriteStream_LeavesAlreadyFilteredDataAlone()
    {
        // A stream that declares its own filter is taken to be encoded already, which is what
        // lets a JPEG be embedded without being re-encoded.
        var stream = new PdfStream(new byte[] { 1, 2, 3, 4 });

        stream.SetName("Filter", "DCTDecode");

        var document = new PdfDocument();
        PdfPage page = document.AddPage(PdfPaperSize.A4);

        page.Content.DrawText("x", StandardFont.Helvetica, 10, 10, 10);
        document.Add(stream);

        string text = Latin1(document.ToArray());

        Assert.IsTrue(text.Contains("/Filter /DCTDecode", StringComparison.Ordinal), "the declared filter was replaced");
        Assert.IsFalse(text.Contains("/Filter /FlateDecode /Filter", StringComparison.Ordinal), "a second filter was added");
    }

    // ---------------------------------------------------------------- determinism

    [TestMethod]
    public void Save_IsReproducible()
    {
        // Nothing timestamped or random is written by default, so the same calls have to give
        // the same bytes - otherwise output cannot be compared against a fixture.
        CollectionAssert.AreEqual(SimpleDocument(), SimpleDocument());
    }

    [TestMethod]
    public void Save_CalledTwiceProducesTheSameBytes()
    {
        var document = new PdfDocument();

        document.Info.Title = "Repeatable";
        document.AddPage(PdfPaperSize.A4).Content.DrawText("x", StandardFont.Helvetica, 10, 10, 10);

        CollectionAssert.AreEqual(document.ToArray(), document.ToArray());
    }

    [TestMethod]
    public void Info_IsOmittedWhenNothingIsSet()
    {
        Assert.IsFalse(
            Latin1(SimpleDocument()).Contains("/Info", StringComparison.Ordinal),
            "an empty information dictionary was written");
    }

    [TestMethod]
    public void Info_IsWrittenWhenSet()
    {
        var document = new PdfDocument();

        document.Info.Title = "Invoice";
        document.Info.Author = "Theoistic";
        document.AddPage(PdfPaperSize.A4).Content.DrawText("x", StandardFont.Helvetica, 10, 10, 10);

        string text = Latin1(document.ToArray());

        Assert.IsTrue(text.Contains("/Info", StringComparison.Ordinal), "the trailer has no /Info reference");
        Assert.IsTrue(text.Contains("/Title", StringComparison.Ordinal), "the title was not written");
    }

    // ---------------------------------------------------------------- content stream state

    [TestMethod]
    public void Content_UnbalancedSaveIsRejected()
    {
        var document = new PdfDocument();

        document.AddPage(PdfPaperSize.A4).Content.Save().Rectangle(0, 0, 10, 10).Fill();

        InvalidOperationException error =
            Assert.ThrowsException<InvalidOperationException>(() => document.ToArray());

        Assert.IsTrue(error.Message.Contains("Save", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Content_RestoreWithoutSaveIsRejected()
    {
        var document = new PdfDocument();
        PdfPage page = document.AddPage(PdfPaperSize.A4);

        Assert.ThrowsException<InvalidOperationException>(() => page.Content.Restore());
    }

    [TestMethod]
    public void Content_UnclosedTextObjectIsRejected()
    {
        var document = new PdfDocument();

        document.AddPage(PdfPaperSize.A4).Content.BeginText().SetFont(StandardFont.Helvetica, 10).ShowText("x");

        Assert.ThrowsException<InvalidOperationException>(() => document.ToArray());
    }

    [TestMethod]
    public void ShowText_OutsideATextObjectIsRejected()
    {
        var document = new PdfDocument();
        PdfPage page = document.AddPage(PdfPaperSize.A4);

        Assert.ThrowsException<InvalidOperationException>(() => page.Content.ShowText("x"));
    }

    [TestMethod]
    public void Content_EmitsTheExpectedOperators()
    {
        var document = new PdfDocument();
        PdfPage page = document.AddPage(PdfPaperSize.A4);

        page.Content
            .Save()
            .SetFillColor(PdfColor.Rgb(1, 0, 0))
            .SetStrokeColor(PdfColor.Gray(0.5))
            .SetLineWidth(2)
            .Rectangle(10, 20, 30, 40)
            .FillAndStroke()
            .Restore();

        // The whole sequence is short enough to pin exactly, which catches a stray operand or
        // a missing separator that a substring check would let through.
        Assert.AreEqual(
            "q\n1 0 0 rg\n0.5 G\n2 w\n10 20 30 40 re\nB\nQ\n",
            Latin1(page.Content.ToArray()));
    }

    [TestMethod]
    public void Content_CmykAndEvenOddVariantsUseTheRightOperators()
    {
        var document = new PdfDocument();
        PdfPage page = document.AddPage(PdfPaperSize.A4);

        page.Content
            .SetFillColor(PdfColor.Cmyk(0, 0.2, 0.4, 1))
            .Rectangle(0, 0, 1, 1)
            .Fill(PdfFillRule.EvenOdd);

        string content = Latin1(page.Content.ToArray());

        Assert.IsTrue(content.Contains("0 0.2 0.4 1 k", StringComparison.Ordinal), "CMYK fill operator is wrong");
        Assert.IsTrue(content.Contains("f*", StringComparison.Ordinal), "even-odd fill operator is wrong");
    }

    [TestMethod]
    public void ShowText_WithAdjustmentsEmitsAnArray()
    {
        var document = new PdfDocument();
        PdfPage page = document.AddPage(PdfPaperSize.A4);

        page.Content
            .BeginText()
            .SetFont(StandardFont.Helvetica, 12)
            .ShowText(new object[] { "A", -120, "V" })
            .EndText();

        string content = Latin1(page.Content.ToArray());

        Assert.IsTrue(content.Contains("[(A)-120(V)] TJ", StringComparison.Ordinal), $"got: {content}");
    }

    // ---------------------------------------------------------------- font metrics

    [TestMethod]
    [DataRow('A', 667)]
    [DataRow('a', 556)]
    [DataRow(' ', 278)]
    [DataRow('i', 222)]
    [DataRow('W', 944)]
    [DataRow('~', 584)]
    public void Helvetica_HasTheDocumentedAdvanceWidths(char c, int expected)
    {
        Assert.AreEqual(expected, StandardFont.Helvetica.GetGlyphWidth(c));
    }

    [TestMethod]
    [DataRow('A', 722)]
    [DataRow('a', 444)]
    [DataRow(' ', 250)]
    public void TimesRoman_HasTheDocumentedAdvanceWidths(char c, int expected)
    {
        Assert.AreEqual(expected, StandardFont.TimesRoman.GetGlyphWidth(c));
    }

    [TestMethod]
    public void Courier_IsMonospaced()
    {
        for (char c = ' '; c <= '~'; c++)
        {
            Assert.AreEqual(600, StandardFont.Courier.GetGlyphWidth(c), $"width of '{c}' is not 600");
        }
    }

    [TestMethod]
    public void ObliqueVariantsShareTheirUprightMetrics()
    {
        // The oblique faces are sheared, not redrawn, so the advances are identical.
        for (char c = ' '; c <= '~'; c++)
        {
            Assert.AreEqual(
                StandardFont.Helvetica.GetGlyphWidth(c),
                StandardFont.HelveticaOblique.GetGlyphWidth(c),
                $"width of '{c}' differs between Helvetica and its oblique");
        }
    }

    [TestMethod]
    public void GetTextWidth_ScalesWithTheFontSize()
    {
        // "AV" in Helvetica is 667 + 667 thousandths of the size.
        Assert.AreEqual(1334 * 12 / 1000.0, StandardFont.Helvetica.GetTextWidth("AV", 12), 1e-9);
        Assert.AreEqual(1334 * 24 / 1000.0, StandardFont.Helvetica.GetTextWidth("AV", 24), 1e-9);
    }

    [TestMethod]
    public void GetTextWidth_OfAnEmptyStringIsZero()
    {
        Assert.AreEqual(0, StandardFont.Helvetica.GetTextWidth(string.Empty, 12));
    }

    [TestMethod]
    public void GetTextWidth_RejectsANonPositiveSize()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => StandardFont.Helvetica.GetTextWidth("x", 0));
    }

    [TestMethod]
    public void GetGlyphWidth_ResolvesAccentedLettersThroughTheirBase()
    {
        // The accent is drawn inside the base letter's advance in all of these typefaces.
        Assert.AreEqual(
            StandardFont.Helvetica.GetGlyphWidth('e'),
            StandardFont.Helvetica.GetGlyphWidth('é'));
        Assert.AreEqual(
            StandardFont.TimesRoman.GetGlyphWidth('A'),
            StandardFont.TimesRoman.GetGlyphWidth('Å'));
    }

    [TestMethod]
    public void Get_SelectsTheVariantByWeightAndSlope()
    {
        Assert.AreEqual("Helvetica", StandardFont.Get(StandardFontFamily.Helvetica).BaseFontName);
        Assert.AreEqual("Helvetica-Bold", StandardFont.Get(StandardFontFamily.Helvetica, bold: true).BaseFontName);
        Assert.AreEqual(
            "Times-BoldItalic",
            StandardFont.Get(StandardFontFamily.Times, bold: true, italic: true).BaseFontName);
        Assert.AreEqual(
            "Courier-Oblique",
            StandardFont.Get(StandardFontFamily.Courier, italic: true).BaseFontName);
    }

    // ---------------------------------------------------------------- WinAnsi encoding

    [TestMethod]
    [DataRow('€', 0x80)] // euro, which Latin-1 has no code for at all
    [DataRow('’', 0x92)] // right single quote, the one Word inserts for an apostrophe
    [DataRow('—', 0x97)] // em dash
    [DataRow('•', 0x95)] // bullet
    [DataRow('é', 0xE9)] // e-acute, where WinAnsi agrees with Latin-1
    [DataRow('A', 0x41)]
    public void Encode_MapsTheWinAnsiRange(char c, int expected)
    {
        var document = new PdfDocument();
        PdfPage page = document.AddPage(PdfPaperSize.A4);

        page.Content.DrawText(c.ToString(), StandardFont.Helvetica, 12, 10, 10);

        byte[] content = page.Content.ToArray();
        int open = Array.IndexOf(content, (byte)'(');

        // A byte that needed escaping is written as an octal triple, so the assertion looks at
        // the escaped form in that case.
        if (expected < 32 || expected > 126)
        {
            string octal = Convert.ToString(expected, 8).PadLeft(3, '0');

            Assert.IsTrue(
                Latin1(content).Contains($@"\{octal}", StringComparison.Ordinal),
                $"U+{(int)c:X4} was not encoded as byte 0x{expected:X2}");
        }
        else
        {
            Assert.AreEqual((byte)expected, content[open + 1]);
        }
    }

    [TestMethod]
    public void Encode_RejectsTextWithNoWinAnsiRepresentation()
    {
        var document = new PdfDocument();
        PdfPage page = document.AddPage(PdfPaperSize.A4);

        // A standard font cannot show this, and silently dropping it would be worse than
        // failing: the caller needs to know an embedded font is required.
        ArgumentException error = Assert.ThrowsException<ArgumentException>(
            () => page.Content.DrawText("中文", StandardFont.Helvetica, 12, 10, 10));

        Assert.IsTrue(error.Message.Contains("embedded", StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------- geometry

    [TestMethod]
    public void PdfRectangle_NormalisesInvertedCorners()
    {
        var rectangle = new PdfRectangle(100, 200, 10, 20);

        Assert.AreEqual(10, rectangle.Left);
        Assert.AreEqual(20, rectangle.Bottom);
        Assert.AreEqual(100, rectangle.Right);
        Assert.AreEqual(200, rectangle.Top);
        Assert.AreEqual(90, rectangle.Width);
        Assert.AreEqual(180, rectangle.Height);
    }

    [TestMethod]
    public void PdfMatrix_MultiplyComposesInPdfOrder()
    {
        PdfMatrix scaleThenTranslate = PdfMatrix.Scaling(2, 2).Multiply(PdfMatrix.Translation(10, 0));

        // Scaling is applied first, so the translation is not scaled with it.
        Assert.AreEqual(new PdfPoint(14, 4), scaleThenTranslate.Transform(new PdfPoint(2, 2)));
    }

    [TestMethod]
    public void PdfMatrix_RotationQuarterTurnMapsXOntoY()
    {
        PdfPoint rotated = PdfMatrix.Rotation(90).Transform(new PdfPoint(1, 0));

        Assert.AreEqual(0, rotated.X, 1e-9);
        Assert.AreEqual(1, rotated.Y, 1e-9);
    }

    [TestMethod]
    [DataRow("#fff", 1.0, 1.0, 1.0)]
    [DataRow("#000", 0.0, 0.0, 0.0)]
    [DataRow("#ff0000", 1.0, 0.0, 0.0)]
    [DataRow("336699", 0.2, 0.4, 0.6)]
    public void PdfColor_ParsesHex(string hex, double red, double green, double blue)
    {
        PdfColor color = PdfColor.FromHex(hex);

        Assert.AreEqual(PdfColorSpace.DeviceRgb, color.Space);
        Assert.AreEqual(red, color.Component0, 0.004);
        Assert.AreEqual(green, color.Component1, 0.004);
        Assert.AreEqual(blue, color.Component2, 0.004);
    }

    [TestMethod]
    public void PdfColor_RejectsMalformedHex()
    {
        Assert.ThrowsException<FormatException>(() => PdfColor.FromHex("#ff"));
        Assert.ThrowsException<FormatException>(() => PdfColor.FromHex("#gggggg"));
    }

    [TestMethod]
    public void PdfColor_ClampsOutOfRangeComponents()
    {
        PdfColor color = PdfColor.Rgb(-1, 2, 0.5);

        Assert.AreEqual(0, color.Component0);
        Assert.AreEqual(1, color.Component1);
        Assert.AreEqual(0.5, color.Component2);
    }
}
