using System.Text;
using Theoistic.PDF.Layout;
using Theoistic.PDF.Writing;

namespace Theoistic.PDF.Tests;

/// <summary>
/// Covers the flow layout: wrapping, alignment, page breaking, tables, containers and page
/// bands.
/// <para>
/// Positions are asserted exactly, from Helvetica's metrics. At 10pt with the default 1.3 line
/// height a line box is 13pt; the ascender is 7.18pt and the descender 2.07pt, and the leading
/// is split evenly around them, so a line's baseline sits 9.055pt below its top. With 50pt
/// margins on A4 (595 x 842) the first baseline is therefore at y = 842 - 59.055 = 782.945.
/// </para>
/// </summary>
[TestClass]
public sealed class LayoutTests
{
    private static readonly TextStyle Ten = new TextStyle { Size = 10 };

    private static FlowDocument Document() => new FlowDocument { Margins = new Thickness(50) };

    private static string Content(PdfPage page) => Encoding.Latin1.GetString(page.Content.ToArray());

    private static string FixturesImage(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Images", name);

    private static PdfFontFamily Wide => new PdfFontFamily(
        TrueTypeFont.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Fonts", "TestWide.ttf")),
        TrueTypeFont.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Fonts", "TestWide-Bold.ttf")));

    // ---------------------------------------------------------------- paragraphs

    [TestMethod]
    public void Paragraph_ThatFitsIsOneLine()
    {
        Assert.AreEqual(13, new Paragraph("Hello").MeasureWith(Ten, 500), 1e-9);
    }

    [TestMethod]
    public void Paragraph_WrapsAtSpaces()
    {
        // Each "aaaa" is 22.24pt and a space 2.78pt, so two words fit in 50pt and three do not.
        Assert.AreEqual(26, new Paragraph("aaaa aaaa aaaa").MeasureWith(Ten, 50), 1e-9);
    }

    [TestMethod]
    public void Paragraph_NewlineForcesABreak()
    {
        Assert.AreEqual(26, new Paragraph("a\nb").MeasureWith(Ten, 500), 1e-9);
    }

    [TestMethod]
    public void Paragraph_KeepsABlankLine()
    {
        Assert.AreEqual(39, new Paragraph("a\n\nb").MeasureWith(Ten, 500), 1e-9);
    }

    [TestMethod]
    public void Paragraph_EmptyStillOccupiesALine()
    {
        Assert.AreEqual(13, new Paragraph(string.Empty).MeasureWith(Ten, 500), 1e-9);
    }

    [TestMethod]
    public void Paragraph_BreaksAnOverlongWordBetweenCharacters()
    {
        // Twenty 5.56pt letters in 30pt: five per line, four lines - not one line running off.
        Assert.AreEqual(52, new Paragraph(new string('a', 20)).MeasureWith(Ten, 30), 1e-9);
    }

    [TestMethod]
    public void Paragraph_LargerRunRaisesItsLine()
    {
        var paragraph = new Paragraph().Add("small ", Ten).Add("LARGE", Ten with { Size = 20 });

        Assert.AreEqual(26, paragraph.MeasureWith(Ten, 500), 1e-9);
    }

    [TestMethod]
    public void Paragraph_ChangingItsStyleInvalidatesTheLayout()
    {
        var paragraph = new Paragraph("text");

        Assert.AreEqual(13, paragraph.MeasureWith(Ten, 500), 1e-9);

        paragraph.Style = Ten with { Size = 20 };

        Assert.AreEqual(26, paragraph.MeasureWith(Ten, 500), 1e-9);
    }

    [TestMethod]
    public void Paragraph_StartsAtTheTopMargin()
    {
        FlowDocument doc = Document();
        doc.Add(new Paragraph("Hi", Ten));

        Assert.IsTrue(Content(doc.Render().Pages[0]).Contains("50 782.945 Td", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Paragraph_SpaceBeforeIsDroppedAtTheTopOfAPage()
    {
        FlowDocument doc = Document();
        doc.Add(new Paragraph("Hi", Ten) { SpaceBefore = 100 });

        Assert.IsTrue(Content(doc.Render().Pages[0]).Contains("50 782.945 Td", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Paragraph_RightAlignedEndsAtTheRightMargin()
    {
        // "Hi" is 9.44pt wide; the content area ends at 545.
        FlowDocument doc = Document();
        doc.Add(new Paragraph("Hi", Ten) { Align = TextAlign.Right });

        Assert.IsTrue(Content(doc.Render().Pages[0]).Contains("535.56 782.945 Td", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Paragraph_DrawsAdjacentRunsOfOneStyleAsOneString()
    {
        FlowDocument doc = Document();
        doc.Add(new Paragraph().Add("one ", Ten).Add("two", Ten));

        Assert.IsTrue(Content(doc.Render().Pages[0]).Contains("(one two) Tj", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- tables

    private static TableBlock Table(int rows, TableStyle? style = null)
    {
        var table = new TableBlock { Style = style ?? new TableStyle() };

        table.AddColumn(new TableColumn { Header = "Col A" });
        table.AddColumn(new TableColumn { Header = "Col B", Width = ColumnWidth.Auto, Align = TextAlign.Right });

        for (int i = 0; i < rows; i++)
        {
            table.AddRow($"row {i}", (i * 1000.5).ToString("N2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return table;
    }

    [TestMethod]
    public void Table_SpansPagesAndRepeatsItsHeader()
    {
        FlowDocument doc = Document();
        doc.Add(Table(120));

        PdfDocument pdf = doc.Render();

        Assert.IsTrue(pdf.Pages.Count >= 2, "120 rows should not fit on one page");

        foreach (PdfPage page in pdf.Pages)
        {
            Assert.IsTrue(Content(page).Contains("(Col A) Tj", StringComparison.Ordinal), "a continuation page lost its header");
        }
    }

    [TestMethod]
    public void Table_HeaderCanBeLeftOffContinuationPages()
    {
        FlowDocument doc = Document();
        doc.Add(Table(120, new TableStyle { RepeatHeader = false }));

        PdfDocument pdf = doc.Render();

        Assert.IsTrue(Content(pdf.Pages[0]).Contains("(Col A) Tj", StringComparison.Ordinal));
        Assert.IsFalse(Content(pdf.Pages[1]).Contains("(Col A) Tj", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Table_HeaderIsNeverLeftAloneAtTheFootOfAPage()
    {
        // 30pt left: room for the 21pt header but not for it and its first row.
        FlowDocument doc = Document();
        doc.Add(new SpacerBlock(742 - 30));
        doc.Add(Table(3));

        PdfDocument pdf = doc.Render();

        Assert.IsFalse(Content(pdf.Pages[0]).Contains("(Col A) Tj", StringComparison.Ordinal), "the header was stranded on page 1");
        Assert.IsTrue(Content(pdf.Pages[1]).Contains("(Col A) Tj", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Table_AutoColumnFitsItsWidestCellWithoutWrapping()
    {
        FlowDocument doc = Document();
        doc.Add(Table(3));

        // Wrapped, the number would be drawn as separate chunks.
        Assert.IsTrue(Content(doc.Render().Pages[0]).Contains("(2,001.00) Tj", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Table_RowsMustMatchTheColumnCount()
    {
        var table = new TableBlock();
        table.AddColumn(new TableColumn());

        Assert.ThrowsException<ArgumentException>(() => table.AddRow("one", "two"));
    }

    [TestMethod]
    public void Table_FromObjectsMakesOneRowEach()
    {
        FlowDocument doc = Document();
        doc.Table(new[] { (Name: "alpha", Count: 1), (Name: "beta", Count: 2) }, t => t
            .Column("Name", x => x.Name)
            .Column("Count", x => x.Count.ToString(), ColumnWidth.Auto, TextAlign.Right));

        TableBlock table = (TableBlock)doc.Blocks[0];

        Assert.AreEqual(2, table.RowCount);
        Assert.AreEqual(2, table.Columns.Count);
    }

    // ---------------------------------------------------------------- containers

    [TestMethod]
    public void Columns_AreAsTallAsTheirTallestColumn()
    {
        FlowDocument doc = Document();
        doc.Add(new ColumnsBlock()
            .Add(ColumnWidth.Relative(), new Paragraph("one line", Ten))
            .Add(ColumnWidth.Relative(), new Paragraph("three\nlines\nhere", Ten)));
        doc.Add(new Paragraph("after", Ten));

        // 39pt of columns, then the next line's baseline 9.055pt into its box.
        Assert.IsTrue(Content(doc.Render().Pages[0]).Contains("50 743.945 Td", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Panel_PadsItsContent()
    {
        FlowDocument doc = Document();
        doc.Add(new PanelBlock(new Paragraph("inside", Ten)) { Padding = new Thickness(10) });
        doc.Add(new Paragraph("after", Ten));

        // 10 + 13 + 10 of panel, then the next baseline.
        Assert.IsTrue(Content(doc.Render().Pages[0]).Contains("50 749.945 Td", StringComparison.Ordinal));
    }

    [TestMethod]
    public void KeepTogether_MovesToTheNextPageRatherThanSplitting()
    {
        FlowDocument doc = Document();
        doc.Add(new SpacerBlock(742 - 30));
        doc.Add(new KeepTogether(new Paragraph("first", Ten), new Paragraph("second", Ten), new Paragraph("third", Ten)));

        PdfDocument pdf = doc.Render();

        Assert.IsFalse(Content(pdf.Pages[0]).Contains("(first) Tj", StringComparison.Ordinal));
        Assert.IsTrue(Content(pdf.Pages[1]).Contains("(third) Tj", StringComparison.Ordinal));
    }

    [TestMethod]
    public void KeepTogether_TallerThanAPageFlowsInstead()
    {
        FlowDocument doc = Document();
        doc.Add(new KeepTogether(Enumerable.Range(0, 120).Select(i => (Block)new Paragraph($"line {i}", Ten)).ToArray()));

        Assert.IsTrue(doc.Render().Pages.Count >= 2);
    }

    [TestMethod]
    public void PageBreak_AtTheTopOfAPageAddsNoBlankPage()
    {
        FlowDocument doc = Document();
        doc.PageBreak();
        doc.Paragraph("only page");

        Assert.AreEqual(1, doc.Render().Pages.Count);
    }

    [TestMethod]
    public void PageBreak_MidPageStartsANewOne()
    {
        FlowDocument doc = Document();
        doc.Paragraph("one");
        doc.PageBreak();
        doc.Paragraph("two");

        Assert.AreEqual(2, doc.Render().Pages.Count);
    }

    [TestMethod]
    public void Image_IsScaledToFitItsBox()
    {
        // logo.png is 240 x 80; in a 30pt-high box it becomes 90 x 30.
        FlowDocument doc = Document();
        doc.Add(new ImageBlock(PdfImage.FromFile(FixturesImage("logo.png")), 30));

        Assert.IsTrue(Content(doc.Render().Pages[0]).Contains("90 0 0 30 50 762 cm", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- bands

    private static FlowDocument ThreePages()
    {
        FlowDocument doc = Document();
        doc.Paragraph("one");
        doc.PageBreak();
        doc.Paragraph("two");
        doc.PageBreak();
        doc.Paragraph("three");

        return doc;
    }

    [TestMethod]
    public void Footer_ReplacesPageTokens()
    {
        FlowDocument doc = ThreePages();
        doc.Footer.Center = "Page {page} of {pages}";

        PdfDocument pdf = doc.Render();

        Assert.IsTrue(Content(pdf.Pages[1]).Contains("(Page 2 of 3) Tj", StringComparison.Ordinal));
        Assert.IsTrue(Content(pdf.Pages[2]).Contains("(Page 3 of 3) Tj", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Header_CanSkipTheFirstPage()
    {
        FlowDocument doc = ThreePages();
        doc.Header.Left = "HEADER";
        doc.Header.OnFirstPage = false;

        PdfDocument pdf = doc.Render();

        Assert.IsFalse(Content(pdf.Pages[0]).Contains("(HEADER) Tj", StringComparison.Ordinal));
        Assert.IsTrue(Content(pdf.Pages[1]).Contains("(HEADER) Tj", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- text coverage

    [TestMethod]
    public void FindUnsupportedText_ReportsCharactersOutsideWinAnsi()
    {
        FlowDocument doc = Document();
        doc.Paragraph("Łukasz");

        Assert.IsTrue(doc.FindUnsupportedText().Any(p => p.Character == "Ł"));
    }

    [TestMethod]
    public void FindUnsupportedText_LooksInsideTablesAndBands()
    {
        FlowDocument doc = Document();
        // Ž would be no test: WinAnsi has it, at 0x8E. Ł it does not have.
        doc.Add(Table(0).AddRow("Łódź", "1"));
        doc.Footer.Left = "Ödön Ő";

        IReadOnlyList<UnsupportedText> problems = doc.FindUnsupportedText();

        Assert.IsTrue(problems.Any(p => p.Character == "Ł"), "missed a table cell");
        Assert.IsTrue(problems.Any(p => p.Character == "Ő"), "missed the footer");
    }

    [TestMethod]
    public void FindUnsupportedText_IgnoresLineBreaks()
    {
        FlowDocument doc = Document();
        doc.DefaultStyle = new TextStyle { Family = Wide };
        doc.Paragraph("Łukasz\nŻółć");

        Assert.AreEqual(0, doc.FindUnsupportedText().Count);
    }

    // ---------------------------------------------------------------- document

    [TestMethod]
    public void Render_IsReproducible()
    {
        static byte[] Build()
        {
            FlowDocument doc = Document();
            doc.Heading("Title");
            doc.Add(Table(40));
            doc.Footer.Right = "{page}/{pages}";

            return doc.ToArray();
        }

        CollectionAssert.AreEqual(Build(), Build());
    }

    [TestMethod]
    public void Render_CopiesTheDocumentInfo()
    {
        FlowDocument doc = Document();
        doc.Info.Title = "Report";
        doc.Paragraph("x");

        Assert.AreEqual("Report", doc.Render().Info.Title);
    }

    [TestMethod]
    public void Render_WithMarginsThatLeaveNoRoomIsRefused()
    {
        var doc = new FlowDocument { Margins = new Thickness(400) };
        doc.Paragraph("x");

        Assert.ThrowsException<InvalidOperationException>(() => doc.Render());
    }

    [TestMethod]
    public void Landscape_SwapsThePageSize()
    {
        var doc = new FlowDocument(PdfPaperSize.A4, PdfOrientation.Landscape);

        Assert.AreEqual(842, doc.PageWidth);
        Assert.AreEqual(595, doc.PageHeight);
    }
}
