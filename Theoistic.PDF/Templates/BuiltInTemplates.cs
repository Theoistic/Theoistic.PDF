using Theoistic.PDF.Layout;
using Theoistic.PDF.Writing;

namespace Theoistic.PDF.Templates;

/// <summary>
/// A coloured header row, a filled table header with striped rows, and an accent bar for the
/// amount due. Sans-serif.
/// </summary>
internal sealed class ModernInvoiceTemplate : InvoiceTemplate
{
    public override string Name => "Modern";

    protected override PdfColor DefaultAccent => PdfColor.FromHex("#1f3a5f");

    protected override PdfFontFamily DefaultFonts => PdfFontFamily.Helvetica;

    protected override FlowDocument Compose(InvoiceContext c)
    {
        PdfColor ink = PdfColor.FromHex("#1e2329");
        PdfColor muted = PdfColor.FromHex("#6b7280");
        PdfColor tint = Tint(c.Accent, 0.93);

        TextStyle body = c.Style(9.5, color: ink);
        TextStyle small = c.Style(8.5, color: muted);
        TextStyle label = c.Style(7.5, bold: true, color: c.Accent) with { LineHeight = 1.6 };

        FlowDocument doc = NewDocument(c, Thickness.Millimetres(20, 18, 22, 18), body, c.Style(7.5, color: muted));

        doc.Add(new ColumnsBlock()
            .Add(ColumnWidth.Relative(), Letterhead(c, c.Style(18, bold: true, color: c.Accent)))
            .Add(ColumnWidth.Relative(),
                new Paragraph(c.Title.ToUpperInvariant(), c.Style(24, bold: true, color: c.Accent) with { LineHeight = 1.1 })
                {
                    Align = TextAlign.Right,
                    SpaceAfter = 6,
                },
                MetaTable(c, small, body with { Bold = true })));

        doc.Add(new SpacerBlock(22));

        var parties = new ColumnsBlock { Gap = 24 }
            .Add(ColumnWidth.Relative(), Labelled(c.Labels.From, PartyParagraph(c, c.Invoice.Seller, body with { Bold = true }, small), label));

        if (c.Invoice.Buyer.Name is { Length: > 0 })
        {
            parties.Add(ColumnWidth.Relative(), Labelled(c.Labels.BillTo, PartyParagraph(c, c.Invoice.Buyer, body with { Bold = true }, small), label));
        }

        if (c.Invoice.ShipTo is { Name.Length: > 0 } shipTo)
        {
            parties.Add(ColumnWidth.Relative(), Labelled(c.Labels.ShipTo, PartyParagraph(c, shipTo, body with { Bold = true }, small), label));
        }

        doc.Add(parties);
        doc.Add(new SpacerBlock(20));

        doc.Add(LinesTable(c, new TableStyle
        {
            HeaderText = c.Style(8.5, bold: true, color: PdfColor.White),
            HeaderBackground = c.Accent,
            AlternateRowBackground = tint,
            RuleColor = PdfColor.FromHex("#e3e7ec"),
            CellPadding = new Thickness(6, 8),
        }, small));

        IReadOnlyList<TotalRow> rows = TotalRows(c);
        TextStyle barText = c.Style(11, bold: true, color: PdfColor.White);
        var summary = new List<Block>();

        // The last figure - what is due, or failing that the total - moves out of the table into
        // the accent bar, so it is printed once and is the first thing the eye lands on.
        TotalRow? featured = c.Kind == InvoiceDocumentKind.Receipt
            ? null
            : rows.LastOrDefault(r => r.Kind == TotalRowKind.Due) ?? rows.Last(r => r.Kind == TotalRowKind.Grand);

        IEnumerable<TotalRow> tableRows = featured is null ? rows : rows.Where(r => !ReferenceEquals(r, featured));

        if (tableRows.Any())
        {
            summary.Add(TotalsTable(tableRows, body with { Color = muted }, body, body with { Bold = true }, body with { Bold = true }, new Thickness(3, 8)));
        }

        if (featured is not null)
        {
            // An unpaid invoice's total is, to the reader, the amount due.
            string barLabel = featured.Kind == TotalRowKind.Grand && c.Kind == InvoiceDocumentKind.Invoice
                ? c.Labels.AmountDue
                : featured.Label;

            summary.Add(Bar(featured with { Label = barLabel }, barText, c.Accent));
        }
        else
        {
            summary.Add(Bar(new TotalRow(c.Labels.PaidStamp, c.Money(c.Totals.AmountPaid), TotalRowKind.Due), barText, PdfColor.FromHex("#15803d")));
        }

        doc.Add(new KeepTogether(summary.ToArray()) { SpaceAfter = 18 });

        var footer = new ColumnsBlock { Gap = 24 };

        if (PaymentParagraph(c, small with { Bold = true, Color = ink }, small) is { } payment)
        {
            footer.Add(ColumnWidth.Relative(), new PanelBlock(
                new Paragraph(c.Labels.PaymentDetails.ToUpperInvariant(), label) { SpaceAfter = 2 },
                payment)
            {
                Background = tint,
                Padding = new Thickness(10, 12),
            });
        }

        IReadOnlyList<Block> notes = NotesBlocks(c, label, small, uppercaseHeadings: true);

        if (notes.Count > 0)
        {
            footer.Add(ColumnWidth.Relative(), notes.ToArray());
        }

        if (footer.Columns.Count > 0)
        {
            doc.Add(new KeepTogether(footer));
        }

        return doc;
    }

    private static Block[] Labelled(string title, Block content, TextStyle style) =>
        new Block[] { new Paragraph(title.ToUpperInvariant(), style) { SpaceAfter = 2 }, content };

    private static Block Bar(TotalRow row, TextStyle style, PdfColor fill)
    {
        var table = new TableBlock { Style = new TableStyle { ShowHeader = false, RuleColor = null, CellPadding = new Thickness(0) } };

        table.AddColumn(new TableColumn { Width = ColumnWidth.Relative() });
        table.AddColumn(new TableColumn { Width = ColumnWidth.Auto, Align = TextAlign.Right });
        table.AddRow(new Paragraph(row.Label, style), new Paragraph(row.Value, style) { Align = TextAlign.Right });

        return new PanelBlock(table)
        {
            Background = fill,
            Padding = new Thickness(8, 8),
            MaxWidth = 250,
            HorizontalAlign = TextAlign.Right,
            SpaceBefore = 4,
        };
    }

    /// <summary>The colour blended towards white, for a pale fill that still reads as the accent.</summary>
    internal static PdfColor Tint(PdfColor color, double amount) =>
        color.Space == PdfColorSpace.DeviceRgb
            ? PdfColor.Rgb(
                color.Component0 + ((1 - color.Component0) * amount),
                color.Component1 + ((1 - color.Component1) * amount),
                color.Component2 + ((1 - color.Component2) * amount))
            : PdfColor.Gray(0.95);
}

/// <summary>
/// A letterhead over a heavy rule, a ruled table with no fills, and a double rule above the
/// total, in a serif face.
/// </summary>
internal sealed class ClassicInvoiceTemplate : InvoiceTemplate
{
    public override string Name => "Classic";

    protected override PdfColor DefaultAccent => PdfColor.FromHex("#111111");

    protected override PdfFontFamily DefaultFonts => PdfFontFamily.Times;

    protected override FlowDocument Compose(InvoiceContext c)
    {
        PdfColor grey = PdfColor.FromHex("#555555");

        TextStyle body = c.Style(10);
        TextStyle small = c.Style(9, color: grey);
        TextStyle label = c.Style(9, bold: true);

        FlowDocument doc = NewDocument(c, Thickness.Millimetres(18, 20, 20, 20), body, c.Style(8, italic: true, color: grey));

        Party seller = c.Invoice.Seller;

        doc.Add(new ColumnsBlock()
            .Add(ColumnWidth.Relative(), Letterhead(c, c.Style(20, bold: true, color: c.Accent)))
            .Add(ColumnWidth.Relative(), new Paragraph(
                string.Join("\n", new[] { c.Options.Logo is null ? null : seller.Name, seller.Address, seller.Email, seller.Phone, seller.Website }
                    .OfType<string>().Where(s => s.Length > 0)),
                small)
            {
                Align = TextAlign.Right,
            }));

        doc.Add(new RuleBlock { Color = c.Accent, Thickness = 2, SpaceBefore = 8, SpaceAfter = 12 });

        doc.Add(new Paragraph(c.Title.ToUpperInvariant(), c.Style(24, color: c.Accent) with { LineHeight = 1.1 }) { SpaceAfter = 10 });

        // Addresses on the left, the dates on the right, in one row: a stacked layout leaves the
        // right-hand side of the page empty for the height of the address.
        var details = new ColumnsBlock { Gap = 24 };

        if (c.Invoice.Buyer.Name is { Length: > 0 })
        {
            details.Add(ColumnWidth.Relative(),
                new Paragraph(c.Labels.BillTo, label) { SpaceAfter = 3 },
                PartyParagraph(c, c.Invoice.Buyer, body with { Bold = true }, body));
        }

        if (c.Invoice.ShipTo is { Name.Length: > 0 } shipTo)
        {
            details.Add(ColumnWidth.Relative(),
                new Paragraph(c.Labels.ShipTo, label) { SpaceAfter = 3 },
                PartyParagraph(c, shipTo, body with { Bold = true }, body));
        }

        if (details.Columns.Count == 0)
        {
            details.Add(ColumnWidth.Relative());
        }

        details.Add(ColumnWidth.Fixed(210), MetaTable(c, small, body));

        doc.Add(details);
        doc.Add(new SpacerBlock(16));

        doc.Add(LinesTable(c, new TableStyle
        {
            HeaderText = c.Style(10, bold: true),
            HeaderRuleColor = c.Accent,
            RuleColor = PdfColor.FromHex("#cccccc"),
            CellPadding = new Thickness(5, 6),
        }, small with { Italic = true }));

        IReadOnlyList<TotalRow> rows = TotalRows(c);
        int grand = rows.ToList().FindIndex(r => r.Kind == TotalRowKind.Grand);

        var summary = new List<Block>
        {
            TotalsTable(rows.Take(grand), body, body, body, body, new Thickness(2, 6)),

            // The double rule is the ledger convention for "this is the sum".
            new RuleBlock { Color = c.Accent, Thickness = 0.75, MaxWidth = 250, HorizontalAlign = TextAlign.Right, SpaceBefore = 3 },
            new RuleBlock { Color = c.Accent, Thickness = 0.75, MaxWidth = 250, HorizontalAlign = TextAlign.Right, SpaceBefore = 1.5 },
            TotalsTable(rows.Skip(grand), body, body, c.Style(12, bold: true), c.Style(12, bold: true), new Thickness(4, 6)),
        };

        if (c.Kind == InvoiceDocumentKind.Receipt)
        {
            summary.Add(new Paragraph($"{c.Labels.PaidStamp}  {c.Date(c.Invoice.IssueDate)}", c.Style(14, bold: true, color: PdfColor.FromHex("#15803d")))
            {
                Align = TextAlign.Right,
                SpaceBefore = 8,
            });
        }

        doc.Add(new KeepTogether(summary.ToArray()) { SpaceAfter = 20 });

        if (PaymentParagraph(c, label, body) is { } payment)
        {
            doc.Add(new KeepTogether(new Paragraph(c.Labels.PaymentDetails, c.Style(11, bold: true)) { SpaceAfter = 3 }, payment) { SpaceAfter = 12 });
        }

        foreach (Block block in NotesBlocks(c, c.Style(11, bold: true), body with { Italic = true }))
        {
            doc.Add(block);
        }

        return doc;
    }
}

/// <summary>
/// Hairline rules, no fills, quiet grey labels, and the accent kept for the one figure that
/// matters. Sans-serif.
/// </summary>
internal sealed class MinimalInvoiceTemplate : InvoiceTemplate
{
    public override string Name => "Minimal";

    protected override PdfColor DefaultAccent => PdfColor.FromHex("#0f766e");

    protected override PdfFontFamily DefaultFonts => PdfFontFamily.Helvetica;

    protected override FlowDocument Compose(InvoiceContext c)
    {
        PdfColor ink = PdfColor.FromHex("#18181b");
        PdfColor quiet = PdfColor.FromHex("#a1a1aa");
        PdfColor hairline = PdfColor.FromHex("#e4e4e7");

        TextStyle body = c.Style(9, color: ink);
        TextStyle label = c.Style(7, color: quiet) with { LineHeight = 1.7 };
        TextStyle small = c.Style(8.5, color: PdfColor.FromHex("#52525b"));

        FlowDocument doc = NewDocument(c, Thickness.Millimetres(24, 24, 24, 24), body, c.Style(7, color: quiet));

        doc.Add(new ColumnsBlock()
            .Add(ColumnWidth.Relative(),
                new Paragraph(c.Title, c.Style(15, bold: true, color: ink)) { SpaceAfter = 1 },
                new Paragraph(c.Invoice.Number, small))
            .Add(ColumnWidth.Relative(), Letterhead(c, c.Style(11, bold: true, color: ink), TextAlign.Right)));

        doc.Add(new SpacerBlock(30));

        var parties = new ColumnsBlock { Gap = 20 }
            .Add(ColumnWidth.Relative(), Labelled(c.Labels.From, PartyParagraph(c, c.Invoice.Seller, body with { Bold = true }, small), label));

        if (c.Invoice.Buyer.Name is { Length: > 0 })
        {
            parties.Add(ColumnWidth.Relative(), Labelled(c.Labels.BillTo, PartyParagraph(c, c.Invoice.Buyer, body with { Bold = true }, small), label));
        }

        // The dates sit in the same row as the parties rather than in a header block, which is
        // most of what makes this layout feel uncluttered.
        var dates = new List<Block> { new Paragraph(c.Labels.IssueDate.ToUpperInvariant(), label), new Paragraph(c.Date(c.Invoice.IssueDate), body) { SpaceAfter = 6 } };

        if (c.Invoice.DueDate is { } due && c.Kind is InvoiceDocumentKind.Invoice or InvoiceDocumentKind.Quote)
        {
            dates.Add(new Paragraph((c.Kind == InvoiceDocumentKind.Quote ? c.Labels.ValidUntil : c.Labels.DueDate).ToUpperInvariant(), label));
            dates.Add(new Paragraph(c.Date(due), body) { SpaceAfter = 6 });
        }

        if (c.Invoice.Reference is { Length: > 0 } reference)
        {
            dates.Add(new Paragraph((c.Kind == InvoiceDocumentKind.CreditNote ? c.Labels.OriginalInvoice : c.Labels.Reference).ToUpperInvariant(), label));
            dates.Add(new Paragraph(reference, body));
        }

        parties.Add(ColumnWidth.Fixed(110), dates.ToArray());

        doc.Add(parties);
        doc.Add(new SpacerBlock(28));

        doc.Add(LinesTable(c, new TableStyle
        {
            HeaderText = c.Style(7, color: quiet),
            RuleColor = hairline,
            HeaderRuleColor = PdfColor.FromHex("#d4d4d8"),
            CellPadding = new Thickness(7, 0, 7, 8),
        }, c.Style(8, color: quiet)));

        IReadOnlyList<TotalRow> rows = TotalRows(c);

        TableBlock totals = TotalsTable(
            rows,
            small,
            body,
            body with { Bold = true },
            c.Style(13, bold: true, color: c.Accent),
            new Thickness(3, 0, 3, 8));

        var summary = new List<Block> { totals };

        if (c.Kind == InvoiceDocumentKind.Receipt)
        {
            summary.Add(new Paragraph(c.Labels.PaidStamp, c.Style(9, bold: true, color: c.Accent)) { Align = TextAlign.Right, SpaceBefore = 6 });
        }

        doc.Add(new KeepTogether(summary.ToArray()) { SpaceAfter = 28 });

        var footer = new ColumnsBlock { Gap = 20 };

        if (PaymentParagraph(c, small with { Color = quiet }, small) is { } payment)
        {
            footer.Add(ColumnWidth.Relative(), Labelled(c.Labels.PaymentDetails, payment, label));
        }

        IReadOnlyList<Block> notes = NotesBlocks(c, label, small, uppercaseHeadings: true);

        if (notes.Count > 0)
        {
            footer.Add(ColumnWidth.Relative(), notes.ToArray());
        }

        if (footer.Columns.Count > 0)
        {
            doc.Add(new KeepTogether(footer));
        }

        return doc;
    }

    private static Block[] Labelled(string title, Block content, TextStyle style) =>
        new Block[] { new Paragraph(title.ToUpperInvariant(), style), content };
}
