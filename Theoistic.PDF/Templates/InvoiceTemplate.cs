using System.Globalization;
using Theoistic.PDF.Layout;
using Theoistic.PDF.Writing;

namespace Theoistic.PDF.Templates;

/// <summary>
/// Turns an <see cref="Invoice"/> into a finished PDF, with no coordinates and no HTML.
/// <code>
/// byte[] pdf = InvoiceTemplate.Modern.ToPdf(invoice, new InvoiceOptions
/// {
///     Logo = PdfImage.FromFile("logo.png"),
///     AccentColor = PdfColor.FromHex("#0f766e"),
/// });
/// </code>
/// <para>
/// Three designs are built in: <see cref="Modern"/>, <see cref="Classic"/> and <see cref="Minimal"/>.
/// To make your own, derive from this class and arrange the sections in <see cref="Compose"/>;
/// the section builders here do the parts every invoice shares.
/// </para>
/// <para>
/// <see cref="ToDocument"/> returns the laid-out <see cref="FlowDocument"/> before it is rendered,
/// so terms and conditions or a cover note can be appended to any template.
/// </para>
/// </summary>
public abstract class InvoiceTemplate
{
    public static InvoiceTemplate Modern { get; } = new ModernInvoiceTemplate();

    public static InvoiceTemplate Classic { get; } = new ClassicInvoiceTemplate();

    public static InvoiceTemplate Minimal { get; } = new MinimalInvoiceTemplate();

    public static IReadOnlyList<InvoiceTemplate> All { get; } = new[] { Modern, Classic, Minimal };

    public abstract string Name { get; }

    protected abstract PdfColor DefaultAccent { get; }

    protected abstract PdfFontFamily DefaultFonts { get; }

    /// <summary>Lays the invoice out, checking first that it is complete and that its fonts cover its text.</summary>
    /// <exception cref="ArgumentException">The invoice is missing something every invoice needs.</exception>
    /// <exception cref="PdfTextCoverageException">Some text cannot be shown in the chosen fonts.</exception>
    public FlowDocument ToDocument(Invoice invoice, InvoiceOptions? options = null)
    {
        Validate(invoice);

        options ??= new InvoiceOptions();

        var context = new InvoiceContext(
            invoice,
            options,
            options.AccentColor ?? DefaultAccent,
            options.Fonts ?? DefaultFonts);

        FlowDocument document = Compose(context);

        document.Info.Title ??= $"{context.Title} {invoice.Number}";
        document.Info.Author ??= invoice.Seller.Name;
        document.Info.Creator ??= "Theoistic.PDF";

        IReadOnlyList<UnsupportedText> problems = document.FindUnsupportedText();

        if (problems.Count > 0)
        {
            throw new PdfTextCoverageException(
                problems,
                "Set InvoiceOptions.Fonts to a TrueType family that covers these characters, for example "
                + "PdfFontFamily.FromFiles(\"NotoSans-Regular.ttf\", \"NotoSans-Bold.ttf\").");
        }

        return document;
    }

    public byte[] ToPdf(Invoice invoice, InvoiceOptions? options = null) => ToDocument(invoice, options).ToArray();

    public void Save(Invoice invoice, string path, InvoiceOptions? options = null) => ToDocument(invoice, options).Save(path);

    /// <summary>Arranges the sections into a document.</summary>
    protected abstract FlowDocument Compose(InvoiceContext context);

    private static void Validate(Invoice invoice)
    {
        ArgumentNullException.ThrowIfNull(invoice);

        if (string.IsNullOrWhiteSpace(invoice.Number))
        {
            throw new ArgumentException("An invoice needs a number.", nameof(invoice));
        }

        if (string.IsNullOrWhiteSpace(invoice.Seller?.Name))
        {
            throw new ArgumentException("An invoice needs the seller's name.", nameof(invoice));
        }

        if (string.IsNullOrWhiteSpace(invoice.Currency))
        {
            throw new ArgumentException("An invoice needs a currency.", nameof(invoice));
        }

        if (invoice.Lines.Count == 0)
        {
            throw new ArgumentException("An invoice needs at least one line.", nameof(invoice));
        }

        foreach (InvoiceLine line in invoice.Lines)
        {
            if (line.DiscountRate is < 0 or > 1)
            {
                throw new ArgumentException(
                    $"The discount on '{line.Description}' is {line.DiscountRate}; a discount is a fraction between 0 and 1.",
                    nameof(invoice));
            }

            if ((line.TaxRate ?? invoice.TaxRate) is < 0)
            {
                throw new ArgumentException($"The tax rate on '{line.Description}' is negative.", nameof(invoice));
            }
        }
    }

    // ---------------------------------------------------------------- shared sections

    /// <summary>A starting document with the template's margins, base style and footer.</summary>
    protected static FlowDocument NewDocument(InvoiceContext c, Thickness margins, TextStyle baseStyle, TextStyle footerStyle)
    {
        var document = new FlowDocument(c.Options.PaperSize)
        {
            Margins = margins,
            DefaultStyle = baseStyle,
        };

        Party seller = c.Invoice.Seller;

        document.Footer.Left = string.Join("  ·  ", new[]
        {
            seller.Name,
            seller.TaxId is { Length: > 0 } tax ? $"{c.Labels.TaxId} {tax}" : null,
            seller.RegistrationNumber is { Length: > 0 } reg ? $"{c.Labels.RegistrationNumber} {reg}" : null,
        }.OfType<string>());

        document.Footer.Right = c.Labels.Page;
        document.Footer.Style = footerStyle;

        return document;
    }

    /// <summary>A party's name and details as one paragraph, a line each.</summary>
    protected static Paragraph PartyParagraph(InvoiceContext c, Party party, TextStyle name, TextStyle body)
    {
        var paragraph = new Paragraph { Style = body };

        paragraph.Add(party.Name, name);

        IEnumerable<string?> lines = new[]
        {
            party.Address,
            party.TaxId is { Length: > 0 } tax ? $"{c.Labels.TaxId}: {tax}" : null,
            party.RegistrationNumber is { Length: > 0 } reg ? $"{c.Labels.RegistrationNumber}: {reg}" : null,
            party.Email,
            party.Phone,
            party.Website,
        };

        foreach (string line in lines.OfType<string>().Where(l => l.Length > 0))
        {
            paragraph.Add("\n" + line.Trim());
        }

        return paragraph;
    }

    /// <summary>Number, dates and reference as a label-and-value table with no header.</summary>
    protected static TableBlock MetaTable(InvoiceContext c, TextStyle label, TextStyle value)
    {
        var table = new TableBlock
        {
            Style = new TableStyle { ShowHeader = false, RuleColor = null, CellPadding = new Thickness(1.5, 0, 1.5, 10) },
        };

        table.AddColumn(new TableColumn { Width = ColumnWidth.Relative(), Align = TextAlign.Right, Style = label });
        table.AddColumn(new TableColumn { Width = ColumnWidth.Auto, Align = TextAlign.Right, Style = value });

        table.AddRow(c.Labels.Number, c.Invoice.Number);
        table.AddRow(c.Labels.IssueDate, c.Date(c.Invoice.IssueDate));

        if (c.Invoice.DueDate is { } due && c.Kind is InvoiceDocumentKind.Invoice or InvoiceDocumentKind.Quote)
        {
            table.AddRow(c.Kind == InvoiceDocumentKind.Quote ? c.Labels.ValidUntil : c.Labels.DueDate, c.Date(due));
        }

        if (c.Invoice.Reference is { Length: > 0 } reference)
        {
            table.AddRow(c.Kind == InvoiceDocumentKind.CreditNote ? c.Labels.OriginalInvoice : c.Labels.Reference, reference);
        }

        return table;
    }

    /// <summary>
    /// The line items. Discount and tax columns appear only when they say something: a discount
    /// column when any line is discounted, a tax column when lines are taxed at different rates.
    /// </summary>
    protected static TableBlock LinesTable(InvoiceContext c, TableStyle style, TextStyle detail)
    {
        var table = new TableBlock { Style = style, SpaceAfter = 10 };

        table.AddColumn(new TableColumn { Header = c.Labels.Description, Width = ColumnWidth.Relative() });
        table.AddColumn(new TableColumn { Header = c.Labels.Quantity, Width = ColumnWidth.Auto, Align = TextAlign.Right });
        table.AddColumn(new TableColumn { Header = c.Labels.UnitPrice, Width = ColumnWidth.Auto, Align = TextAlign.Right });

        if (c.ShowDiscountColumn)
        {
            table.AddColumn(new TableColumn { Header = c.Labels.Discount, Width = ColumnWidth.Auto, Align = TextAlign.Right });
        }

        if (c.ShowTaxColumn)
        {
            table.AddColumn(new TableColumn { Header = c.Labels.Tax, Width = ColumnWidth.Auto, Align = TextAlign.Right });
        }

        table.AddColumn(new TableColumn { Header = c.Labels.Amount, Width = ColumnWidth.Auto, Align = TextAlign.Right });

        for (int i = 0; i < c.Invoice.Lines.Count; i++)
        {
            InvoiceLine line = c.Invoice.Lines[i];

            var description = new Paragraph(line.Description);

            if (line.Detail is { Length: > 0 } extra)
            {
                description.Add("\n" + extra, detail);
            }

            var cells = new List<Paragraph>
            {
                description,
                Right(line.Unit is { Length: > 0 } unit ? $"{c.Quantity(line.Quantity)} {unit}" : c.Quantity(line.Quantity)),
                Right(c.Amount(line.UnitPrice)),
            };

            if (c.ShowDiscountColumn)
            {
                cells.Add(Right(line.DiscountRate is decimal d and > 0 ? c.Percent(d) : string.Empty));
            }

            if (c.ShowTaxColumn)
            {
                cells.Add(Right((line.TaxRate ?? c.Invoice.TaxRate) is { } rate ? c.Percent(rate) : string.Empty));
            }

            cells.Add(Right(c.Amount(c.Totals.LineAmounts[i])));

            table.AddRow(cells.ToArray());
        }

        return table;

        static Paragraph Right(string text) => new Paragraph(text) { Align = TextAlign.Right };
    }

    /// <summary>The rows of the totals section, in order, with which one is the grand total.</summary>
    protected static IReadOnlyList<TotalRow> TotalRows(InvoiceContext c)
    {
        InvoiceTotals totals = c.Totals;
        var rows = new List<TotalRow>();

        // With a single untaxed total there is nothing for a subtotal to add.
        if (totals.Taxes.Count > 0)
        {
            rows.Add(new TotalRow(c.Labels.Subtotal, c.Amount(totals.Subtotal), TotalRowKind.Normal));
        }

        foreach (TaxGroup tax in totals.Taxes)
        {
            rows.Add(new TotalRow($"{c.Labels.TaxName} {c.Percent(tax.Rate)}", c.Amount(tax.Amount), TotalRowKind.Normal));
        }

        rows.Add(new TotalRow(
            c.Kind == InvoiceDocumentKind.CreditNote ? c.Labels.TotalCredit : c.Labels.Total,
            c.Money(totals.Total),
            TotalRowKind.Grand));

        // Paid and amount-due rows only appear when they say something the total does not: an
        // unpaid invoice's amount due is its total, and a receipt paid in full is just its total.
        bool showPayment = c.Kind switch
        {
            InvoiceDocumentKind.Invoice => totals.AmountPaid != 0,
            InvoiceDocumentKind.Receipt => totals.AmountPaid != totals.Total,
            _ => false
        };

        if (showPayment)
        {
            rows.Add(new TotalRow(c.Labels.AmountPaid, c.Money(-totals.AmountPaid), TotalRowKind.Normal));
            rows.Add(new TotalRow(c.Labels.AmountDue, c.Money(totals.AmountDue), TotalRowKind.Due));
        }

        return rows;
    }

    /// <summary>A label-and-value table of the given rows, sized for the right-hand side of the page.</summary>
    protected static TableBlock TotalsTable(IEnumerable<TotalRow> rows, TextStyle label, TextStyle value, TextStyle grandLabel, TextStyle grandValue, Thickness padding)
    {
        var table = new TableBlock
        {
            MaxWidth = 250,
            HorizontalAlign = TextAlign.Right,
            Style = new TableStyle { ShowHeader = false, RuleColor = null, CellPadding = padding },
        };

        table.AddColumn(new TableColumn { Width = ColumnWidth.Relative() });
        table.AddColumn(new TableColumn { Width = ColumnWidth.Auto, Align = TextAlign.Right });

        foreach (TotalRow row in rows)
        {
            bool grand = row.Kind != TotalRowKind.Normal;

            table.AddRow(
                new Paragraph(row.Label, grand ? grandLabel : label),
                new Paragraph(row.Value, grand ? grandValue : value) { Align = TextAlign.Right });
        }

        return table;
    }

    /// <summary>Payment instructions, or null when there are none or the document is not one that asks for payment.</summary>
    protected static Paragraph? PaymentParagraph(InvoiceContext c, TextStyle label, TextStyle value)
    {
        if (c.Kind != InvoiceDocumentKind.Invoice || c.Invoice.Payment is not { } payment)
        {
            return null;
        }

        var lines = new (string Label, string? Value)[]
        {
            (c.Labels.Bank, payment.BankName),
            (c.Labels.AccountName, payment.AccountName),
            (c.Labels.Iban, payment.Iban),
            (c.Labels.Bic, payment.Bic),
            (c.Labels.AccountNumber, payment.AccountNumber),
            (c.Labels.PaymentReference, payment.Reference),
        };

        var paragraph = new Paragraph { Style = value };
        bool first = true;

        foreach ((string name, string? text) in lines)
        {
            if (text is not { Length: > 0 })
            {
                continue;
            }

            paragraph.Add((first ? string.Empty : "\n") + name + ": ", label).Add(text, value);
            first = false;
        }

        if (payment.Other is { Length: > 0 } other)
        {
            paragraph.Add((first ? string.Empty : "\n") + other, value);
            first = false;
        }

        return first ? null : paragraph;
    }

    /// <summary>Notes and terms, each under its own label; empty when there are neither.</summary>
    protected static IReadOnlyList<Block> NotesBlocks(InvoiceContext c, TextStyle heading, TextStyle body, bool uppercaseHeadings = false)
    {
        var blocks = new List<Block>();

        void Section(string title, string? text)
        {
            if (text is not { Length: > 0 })
            {
                return;
            }

            string shown = uppercaseHeadings ? title.ToUpper(c.Options.Culture) : title;

            blocks.Add(new Paragraph(shown, heading) { SpaceBefore = blocks.Count > 0 ? 8 : 0, SpaceAfter = 2 });
            blocks.Add(new Paragraph(text, body));
        }

        Section(c.Labels.Notes, c.Invoice.Notes);
        Section(c.Labels.Terms, c.Invoice.Terms);

        return blocks;
    }

    /// <summary>The logo if there is one, otherwise the seller's name set large.</summary>
    protected static Block Letterhead(InvoiceContext c, TextStyle nameStyle, TextAlign align = TextAlign.Left)
    {
        if (c.Options.Logo is { } logo)
        {
            return new ImageBlock(logo, c.Options.LogoMaxHeight) { MaxWidth = c.Options.LogoMaxWidth, HorizontalAlign = align };
        }

        return new Paragraph(c.Invoice.Seller.Name, nameStyle) { Align = align };
    }
}

/// <summary>One row of an invoice's totals.</summary>
public sealed record TotalRow(string Label, string Value, TotalRowKind Kind);

public enum TotalRowKind
{
    Normal,

    /// <summary>The document total.</summary>
    Grand,

    /// <summary>What remains to be paid after a partial payment. Absent when it would equal the total.</summary>
    Due
}

/// <summary>
/// An invoice in the middle of being laid out: the data, the options, the computed totals, and
/// formatting that follows the chosen culture and currency.
/// </summary>
public sealed class InvoiceContext
{
    internal InvoiceContext(Invoice invoice, InvoiceOptions options, PdfColor accent, PdfFontFamily fonts)
    {
        Invoice = invoice;
        Options = options;
        Accent = accent;
        Fonts = fonts;
        Totals = InvoiceTotals.Calculate(invoice, options.Kind);
    }

    public Invoice Invoice { get; }

    public InvoiceOptions Options { get; }

    public InvoiceTotals Totals { get; }

    public PdfColor Accent { get; }

    public PdfFontFamily Fonts { get; }

    public InvoiceDocumentKind Kind => Options.Kind;

    public InvoiceLabels Labels => Options.Labels;

    public string Title => Labels.Title(Kind);

    private CultureInfo Culture => Options.Culture;

    /// <summary>A text style in the invoice's font family.</summary>
    public TextStyle Style(double size, bool bold = false, PdfColor? color = null, bool italic = false) =>
        new TextStyle { Family = Fonts, Size = size, Bold = bold, Italic = italic, Color = color ?? PdfColor.Black };

    /// <summary>An amount with the currency's decimals and the culture's grouping, without the currency.</summary>
    public string Amount(decimal value) =>
        Options.FormatAmount?.Invoke(value)
        ?? Printable(value.ToString("N" + Totals.Decimals.ToString(CultureInfo.InvariantCulture), Culture));

    /// <summary>An amount followed by the currency code.</summary>
    public string Money(decimal value) => $"{Amount(value)} {Invoice.Currency}";

    /// <summary>A quantity without trailing zeros: 3, 1.5, 0.25.</summary>
    public string Quantity(decimal value) => Printable(value.ToString("0.####", Culture));

    /// <summary>A fractional rate as a percentage: 0.25 becomes 25%.</summary>
    public string Percent(decimal rate) => Printable((rate * 100).ToString("0.##", Culture) + "%");

    public string Date(DateOnly date) => Printable(date.ToString(Options.DateFormat, Culture));

    /// <summary>
    /// Swaps the narrow and thin spaces some cultures group digits with for a no-break space.
    /// They look alike, but only the no-break space is in WinAnsi - without this, a French or
    /// Swiss invoice in a standard font would be refused over its number formatting alone.
    /// </summary>
    private static string Printable(string text) =>
        text.Replace(' ', ' ').Replace(' ', ' ');

    public bool ShowDiscountColumn => Invoice.Lines.Any(line => line.DiscountRate is > 0);

    /// <summary>A per-line tax column is only informative when lines are taxed differently.</summary>
    public bool ShowTaxColumn => Invoice.Lines.Select(line => line.TaxRate ?? Invoice.TaxRate).Distinct().Count() > 1;
}
