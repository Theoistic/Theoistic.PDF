using System.Globalization;
using System.Text;
using Theoistic.PDF.Layout;
using Theoistic.PDF.Templates;
using Theoistic.PDF.Writing;

namespace Theoistic.PDF.Tests;

/// <summary>
/// Covers the invoice model, its arithmetic, and the built-in templates. The arithmetic tests
/// pin the rounding rules, because a total that is a cent out is the bug an invoice cannot have.
/// </summary>
[TestClass]
public sealed class InvoiceTemplateTests
{
    private static Invoice Sample(params InvoiceLine[] lines)
    {
        var invoice = new Invoice
        {
            Number = "2026-0042",
            IssueDate = new DateOnly(2026, 10, 22),
            DueDate = new DateOnly(2026, 11, 21),
            Currency = "EUR",
            TaxRate = 0.25m,
            Seller = new Party { Name = "Theoistic ApS", Address = "Niels Finsens gøta 1\nFO-100 Tórshavn", TaxId = "FO 123456" },
            Buyer = new Party { Name = "Acme Industries Ltd", Address = "14 Harbour Road\nBelfast" },
        };

        invoice.Lines.AddRange(lines.Length > 0 ? lines : new[] { new InvoiceLine("Widget", 2, 50m) });

        return invoice;
    }

    private static string Fixture(string folder, string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", folder, name);

    private static PdfFontFamily Wide => new PdfFontFamily(
        TrueTypeFont.Load(Fixture("Fonts", "TestWide.ttf")),
        TrueTypeFont.Load(Fixture("Fonts", "TestWide-Bold.ttf")));

    private static string AllContent(PdfDocument pdf) =>
        string.Concat(pdf.Pages.Select(page => Encoding.Latin1.GetString(page.Content.ToArray())));

    // ---------------------------------------------------------------- arithmetic

    [TestMethod]
    public void Totals_GroupTaxByRate()
    {
        Invoice invoice = Sample(
            new InvoiceLine("Standard", 1, 100m) { TaxRate = 0.25m },
            new InvoiceLine("Reduced", 1, 50m) { TaxRate = 0.12m },
            new InvoiceLine("Exempt", 1, 10m));

        invoice.TaxRate = null;

        InvoiceTotals totals = InvoiceTotals.Calculate(invoice);

        Assert.AreEqual(160m, totals.Subtotal);
        Assert.AreEqual(2, totals.Taxes.Count, "an untaxed line must not form a tax group");
        Assert.AreEqual(new TaxGroup(0.12m, 50m, 6.00m), totals.Taxes[0], "groups are in ascending rate order");
        Assert.AreEqual(new TaxGroup(0.25m, 100m, 25.00m), totals.Taxes[1]);
        Assert.AreEqual(191m, totals.Total);
    }

    [TestMethod]
    public void Totals_RoundTaxOncePerRateRatherThanPerLine()
    {
        // Per line, 0.025 tax rounds to 0.03 three times: 0.09. Per rate, 0.075 rounds once: 0.08.
        Invoice invoice = Sample(
            new InvoiceLine("a", 1, 0.10m),
            new InvoiceLine("b", 1, 0.10m),
            new InvoiceLine("c", 1, 0.10m));

        Assert.AreEqual(0.08m, InvoiceTotals.Calculate(invoice).TaxTotal);
    }

    [TestMethod]
    public void Totals_RoundMidpointsAwayFromZero()
    {
        // Banker's rounding, .NET's default, would give 0.12.
        Invoice invoice = Sample(new InvoiceLine("half cent", 1, 0.125m));
        invoice.TaxRate = null;

        Assert.AreEqual(0.13m, InvoiceTotals.Calculate(invoice).Total);
    }

    [TestMethod]
    public void Totals_ApplyLineDiscounts()
    {
        Invoice invoice = Sample(new InvoiceLine("discounted", 1, 950m) { DiscountRate = 0.10m });
        invoice.TaxRate = null;

        InvoiceTotals totals = InvoiceTotals.Calculate(invoice);

        Assert.AreEqual(855m, totals.LineAmounts[0]);
        Assert.AreEqual(95m, totals.DiscountTotal);
    }

    [TestMethod]
    public void Totals_ExtractTaxWhenPricesIncludeIt()
    {
        Invoice invoice = Sample(new InvoiceLine("gross priced", 1, 125m));
        invoice.PricesIncludeTax = true;

        InvoiceTotals totals = InvoiceTotals.Calculate(invoice);

        Assert.AreEqual(25m, totals.TaxTotal);
        Assert.AreEqual(100m, totals.Subtotal);
        Assert.AreEqual(125m, totals.Total, "including tax must not add it a second time");
    }

    [TestMethod]
    [DataRow("JPY", "1234.5", "1235")]
    [DataRow("KWD", "1.2345", "1.235")]
    [DataRow("EUR", "1.005", "1.01")]
    public void Totals_UseTheCurrencysOwnDecimals(string currency, string price, string expected)
    {
        Invoice invoice = Sample(new InvoiceLine("item", 1, decimal.Parse(price, CultureInfo.InvariantCulture)));
        invoice.Currency = currency;
        invoice.TaxRate = null;

        Assert.AreEqual(decimal.Parse(expected, CultureInfo.InvariantCulture), InvoiceTotals.Calculate(invoice).Total);
    }

    [TestMethod]
    public void Totals_CurrencyDecimalsCanBeOverridden()
    {
        Invoice invoice = Sample(new InvoiceLine("item", 1, 99.5m));
        invoice.TaxRate = null;
        invoice.CurrencyDecimals = 0;

        Assert.AreEqual(100m, InvoiceTotals.Calculate(invoice).Total);
    }

    [TestMethod]
    public void Totals_ZeroRateIsStillDisclosed()
    {
        // Zero-rated supplies have to appear on an EU VAT invoice, so 0% is a real group.
        Invoice invoice = Sample(new InvoiceLine("book", 1, 20m) { TaxRate = 0m });

        Assert.AreEqual(new TaxGroup(0m, 20m, 0m), InvoiceTotals.Calculate(invoice).Taxes.Single());
    }

    [TestMethod]
    public void Totals_ReceiptLeftAtZeroIsPaidInFull()
    {
        InvoiceTotals totals = InvoiceTotals.Calculate(Sample(), InvoiceDocumentKind.Receipt);

        Assert.AreEqual(totals.Total, totals.AmountPaid);
        Assert.AreEqual(0m, totals.AmountDue);
    }

    [TestMethod]
    public void Totals_PartialPaymentLeavesTheRestDue()
    {
        Invoice invoice = Sample();
        invoice.AmountPaid = 25m;

        Assert.AreEqual(100m, InvoiceTotals.Calculate(invoice).AmountDue);
    }

    // ---------------------------------------------------------------- totals rows

    /// <summary>A custom template, which is also how the protected section builders are reached.</summary>
    private sealed class ProbeTemplate : InvoiceTemplate
    {
        public IReadOnlyList<TotalRow> Rows { get; private set; } = Array.Empty<TotalRow>();

        public override string Name => "Probe";

        protected override PdfColor DefaultAccent => PdfColor.Black;

        protected override PdfFontFamily DefaultFonts => PdfFontFamily.Helvetica;

        protected override FlowDocument Compose(InvoiceContext c)
        {
            Rows = TotalRows(c);

            FlowDocument doc = NewDocument(c, new Thickness(40), c.Style(10), c.Style(8));

            doc.Add(Letterhead(c, c.Style(16, bold: true)));
            doc.Add(LinesTable(c, new TableStyle(), c.Style(8)));
            doc.Add(TotalsTable(Rows, c.Style(10), c.Style(10), c.Style(10, bold: true), c.Style(10, bold: true), new Thickness(2)));

            return doc;
        }
    }

    private static IReadOnlyList<TotalRow> RowsFor(Invoice invoice, InvoiceDocumentKind kind)
    {
        var probe = new ProbeTemplate();
        probe.ToDocument(invoice, new InvoiceOptions { Kind = kind });

        return probe.Rows;
    }

    [TestMethod]
    public void TotalRows_UnpaidInvoiceDoesNotRepeatItsTotalAsAmountDue()
    {
        IReadOnlyList<TotalRow> rows = RowsFor(Sample(), InvoiceDocumentKind.Invoice);

        Assert.IsFalse(rows.Any(r => r.Kind == TotalRowKind.Due));
        Assert.AreEqual(1, rows.Count(r => r.Kind == TotalRowKind.Grand));
    }

    [TestMethod]
    public void TotalRows_PartlyPaidInvoiceShowsWhatIsPaidAndWhatIsDue()
    {
        Invoice invoice = Sample();
        invoice.AmountPaid = 25m;

        IReadOnlyList<TotalRow> rows = RowsFor(invoice, InvoiceDocumentKind.Invoice);

        Assert.AreEqual("-25.00 EUR", rows.Single(r => r.Label == "Paid").Value);
        Assert.AreEqual("100.00 EUR", rows.Single(r => r.Kind == TotalRowKind.Due).Value);
    }

    [TestMethod]
    public void TotalRows_FullyPaidReceiptIsJustItsTotal()
    {
        IReadOnlyList<TotalRow> rows = RowsFor(Sample(), InvoiceDocumentKind.Receipt);

        Assert.IsFalse(rows.Any(r => r.Label == "Paid"), "a paid row would repeat the PAID mark");
    }

    [TestMethod]
    public void TotalRows_QuoteNeverAsksForPayment()
    {
        Invoice invoice = Sample();
        invoice.AmountPaid = 25m;

        Assert.IsFalse(RowsFor(invoice, InvoiceDocumentKind.Quote).Any(r => r.Kind == TotalRowKind.Due));
    }

    [TestMethod]
    public void TotalRows_UntaxedInvoiceHasNoSubtotal()
    {
        Invoice invoice = Sample();
        invoice.TaxRate = null;

        IReadOnlyList<TotalRow> rows = RowsFor(invoice, InvoiceDocumentKind.Invoice);

        Assert.AreEqual(1, rows.Count, "with no tax a subtotal would just repeat the total");
    }

    [TestMethod]
    public void CustomTemplate_RendersThroughTheSameSections()
    {
        byte[] pdf = new ProbeTemplate().ToPdf(Sample());

        Assert.AreEqual("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
    }

    // ---------------------------------------------------------------- validation

    [TestMethod]
    public void Validation_RequiresANumber()
    {
        Invoice invoice = Sample();
        invoice.Number = " ";

        Assert.ThrowsException<ArgumentException>(() => InvoiceTemplate.Modern.ToDocument(invoice));
    }

    [TestMethod]
    public void Validation_RequiresTheSellersName()
    {
        Invoice invoice = Sample();
        invoice.Seller.Name = string.Empty;

        Assert.ThrowsException<ArgumentException>(() => InvoiceTemplate.Modern.ToDocument(invoice));
    }

    [TestMethod]
    public void Validation_RequiresALine()
    {
        Invoice invoice = Sample();
        invoice.Lines.Clear();

        Assert.ThrowsException<ArgumentException>(() => InvoiceTemplate.Modern.ToDocument(invoice));
    }

    [TestMethod]
    public void Validation_RejectsADiscountThatIsNotAFraction()
    {
        // 10 meaning 10% is the likely mistake; as a fraction it would be a 1000% discount.
        Assert.ThrowsException<ArgumentException>(
            () => InvoiceTemplate.Modern.ToDocument(Sample(new InvoiceLine("x", 1, 10m) { DiscountRate = 10m })));
    }

    [TestMethod]
    public void Validation_RejectsANegativeTaxRate()
    {
        Assert.ThrowsException<ArgumentException>(
            () => InvoiceTemplate.Modern.ToDocument(Sample(new InvoiceLine("x", 1, 10m) { TaxRate = -0.1m })));
    }

    // ---------------------------------------------------------------- rendering

    public static IEnumerable<object[]> TemplatesAndKinds =>
        from template in new[] { "Modern", "Classic", "Minimal" }
        from kind in Enum.GetValues<InvoiceDocumentKind>()
        select new object[] { template, kind };

    private static InvoiceTemplate ByName(string name) => InvoiceTemplate.All.Single(t => t.Name == name);

    [TestMethod]
    [DynamicData(nameof(TemplatesAndKinds))]
    public void EveryTemplate_RendersEveryKindWithItsTitle(string template, InvoiceDocumentKind kind)
    {
        Invoice invoice = Sample();
        invoice.Payment = new PaymentDetails { Iban = "FO97 6460 0001 6312 34" };
        invoice.Notes = "Thanks.";

        PdfDocument pdf = ByName(template).ToDocument(invoice, new InvoiceOptions { Kind = kind }).Render();
        string content = AllContent(pdf);
        string title = InvoiceLabels.English.Title(kind);

        Assert.IsTrue(
            content.Contains($"({title}) Tj", StringComparison.Ordinal) || content.Contains($"({title.ToUpperInvariant()}) Tj", StringComparison.Ordinal),
            $"{template} {kind}: no title");

        Assert.AreEqual(kind == InvoiceDocumentKind.Invoice, content.Contains("(FO97 6460 0001 6312 34) Tj", StringComparison.Ordinal),
            $"{template} {kind}: payment details belong on invoices only");
    }

    [TestMethod]
    public void Metadata_NamesTheDocumentAndItsAuthor()
    {
        PdfDocument pdf = InvoiceTemplate.Modern.ToDocument(Sample()).Render();

        Assert.AreEqual("Invoice 2026-0042", pdf.Info.Title);
        Assert.AreEqual("Theoistic ApS", pdf.Info.Author);
    }

    [TestMethod]
    public void Logo_IsEmbedded()
    {
        byte[] pdf = InvoiceTemplate.Modern.ToPdf(Sample(), new InvoiceOptions { Logo = PdfImage.FromFile(Fixture("Images", "logo.png")) });

        Assert.IsTrue(Encoding.Latin1.GetString(pdf).Contains("/Subtype /Image", StringComparison.Ordinal));
    }

    [TestMethod]
    public void DiscountColumn_AppearsOnlyWhenALineIsDiscounted()
    {
        string plain = AllContent(InvoiceTemplate.Modern.ToDocument(Sample()).Render());
        string discounted = AllContent(InvoiceTemplate.Modern.ToDocument(Sample(new InvoiceLine("x", 1, 10m) { DiscountRate = 0.1m })).Render());

        Assert.IsFalse(plain.Contains("(Discount) Tj", StringComparison.Ordinal));
        Assert.IsTrue(discounted.Contains("(Discount) Tj", StringComparison.Ordinal));
    }

    [TestMethod]
    public void TaxColumn_AppearsOnlyWhenRatesDiffer()
    {
        string single = AllContent(InvoiceTemplate.Modern.ToDocument(Sample()).Render());
        string mixed = AllContent(InvoiceTemplate.Modern.ToDocument(Sample(
            new InvoiceLine("a", 1, 10m),
            new InvoiceLine("b", 1, 10m) { TaxRate = 0m })).Render());

        Assert.IsFalse(single.Contains("(Tax) Tj", StringComparison.Ordinal));
        Assert.IsTrue(mixed.Contains("(Tax) Tj", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Labels_CanBeTranslated()
    {
        string content = AllContent(InvoiceTemplate.Modern.ToDocument(Sample(), new InvoiceOptions
        {
            Labels = InvoiceLabels.English with { TaxName = "VAT", Description = "Item" },
        }).Render());

        Assert.IsTrue(content.Contains("(VAT 25%) Tj", StringComparison.Ordinal));
        Assert.IsTrue(content.Contains("(Item) Tj", StringComparison.Ordinal));
    }

    [TestMethod]
    public void LongInvoice_SpansPagesAndKeepsItsTotalsTogether()
    {
        Invoice invoice = Sample(Enumerable.Range(1, 80).Select(i => new InvoiceLine($"Line {i}", 1, i)).ToArray());

        PdfDocument pdf = InvoiceTemplate.Modern.ToDocument(invoice).Render();
        string last = Encoding.Latin1.GetString(pdf.Pages[^1].Content.ToArray());

        Assert.IsTrue(pdf.Pages.Count >= 2);
        Assert.IsTrue(last.Contains("(Subtotal) Tj", StringComparison.Ordinal) && last.Contains("(Amount due) Tj", StringComparison.Ordinal),
            "the totals were split from the amount due");
    }

    [TestMethod]
    public void ToDocument_CanBeExtendedBeforeRendering()
    {
        FlowDocument doc = InvoiceTemplate.Classic.ToDocument(Sample());

        doc.PageBreak();
        doc.Heading("Terms and conditions");

        PdfDocument pdf = doc.Render();

        Assert.IsTrue(Encoding.Latin1.GetString(pdf.Pages[^1].Content.ToArray()).Contains("(Terms and conditions) Tj", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Rendering_IsReproducible()
    {
        CollectionAssert.AreEqual(InvoiceTemplate.Minimal.ToPdf(Sample()), InvoiceTemplate.Minimal.ToPdf(Sample()));
    }

    // ---------------------------------------------------------------- fonts and cultures

    private static Invoice PolishBuyer()
    {
        Invoice invoice = Sample();
        invoice.Seller.Address = "Warszawa";
        invoice.Buyer = new Party { Name = "Łukasz Żółć", Address = "ul. Świętokrzyska 12" };

        return invoice;
    }

    [TestMethod]
    public void StandardFonts_RefuseAPolishNameAndListEveryCharacter()
    {
        PdfTextCoverageException error = Assert.ThrowsException<PdfTextCoverageException>(
            () => InvoiceTemplate.Modern.ToDocument(PolishBuyer()));

        string[] missing = error.Problems.Select(p => p.Character).Distinct().ToArray();

        // All of them at once, not just the first one met while drawing.
        // Ł Ż ł ć Ś ę - but not ó, which WinAnsi has.
        CollectionAssert.IsSubsetOf(new[] { "Ł", "Ż", "ł", "ć", "Ś", "ę" }, missing);
        CollectionAssert.DoesNotContain(missing, "ó");
        Assert.IsTrue(error.Message.Contains("InvoiceOptions.Fonts", StringComparison.Ordinal), error.Message);
    }

    [TestMethod]
    public void TrueTypeFamily_RendersThePolishName()
    {
        byte[] pdf = InvoiceTemplate.Modern.ToPdf(PolishBuyer(), new InvoiceOptions { Fonts = Wide });
        string text = Encoding.Latin1.GetString(pdf);

        Assert.IsTrue(text.Contains("/FontFile2", StringComparison.Ordinal), "the font was not embedded");
        Assert.IsTrue(text.Contains("TheoisticTestWide", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Culture_FormatsAmounts()
    {
        PdfDocument pdf = InvoiceTemplate.Modern.ToDocument(Sample(new InvoiceLine("x", 1, 1234.5m)), new InvoiceOptions
        {
            Culture = CultureInfo.GetCultureInfo("de-DE"),
        }).Render();

        Assert.IsTrue(AllContent(pdf).Contains("(1.234,50) Tj", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("fr-FR")]
    [DataRow("de-CH")]
    [DataRow("nb-NO")]
    public void Culture_DigitGroupingNeverNeedsAnEmbeddedFont(string culture)
    {
        // These group digits with narrow or thin spaces, which WinAnsi lacks; they are swapped for
        // a no-break space so a standard-font invoice in these locales still renders.
        Invoice invoice = Sample(new InvoiceLine("x", 1, 1234567.5m));

        PdfDocument pdf = InvoiceTemplate.Modern.ToDocument(invoice, new InvoiceOptions
        {
            Culture = CultureInfo.GetCultureInfo(culture),
        }).Render();

        Assert.IsTrue(pdf.Pages.Count >= 1);
    }
}
