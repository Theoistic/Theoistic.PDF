using System.Globalization;
using Theoistic.PDF.Layout;
using Theoistic.PDF.Writing;

namespace Theoistic.PDF.Templates;

/// <summary>How an invoice is printed - as opposed to <see cref="Invoice"/>, which is what it says.</summary>
public sealed record InvoiceOptions
{
    public InvoiceDocumentKind Kind { get; init; } = InvoiceDocumentKind.Invoice;

    /// <summary>Your logo, JPEG or PNG. A PNG with a transparent background sits cleanly on any template.</summary>
    public PdfImage? Logo { get; init; }

    /// <summary>The largest the logo is drawn, in points; it is scaled down to fit, keeping its proportions.</summary>
    public double LogoMaxWidth { get; init; } = 170;

    public double LogoMaxHeight { get; init; } = 60;

    /// <summary>The template's highlight colour; each template has its own default.</summary>
    public PdfColor? AccentColor { get; init; }

    /// <summary>
    /// The typeface. Each template defaults to a standard font, which can only show WinAnsi
    /// (Western European) text. If names or addresses may contain anything else - Polish,
    /// Czech, Turkish, Greek, Cyrillic, CJK - supply a TrueType family that covers them:
    /// <c>PdfFontFamily.FromFiles("NotoSans-Regular.ttf", "NotoSans-Bold.ttf")</c>.
    /// </summary>
    public PdfFontFamily? Fonts { get; init; }

    /// <summary>Formats numbers and dates. Defaults to the invariant culture.</summary>
    public CultureInfo Culture { get; init; } = CultureInfo.InvariantCulture;

    public string DateFormat { get; init; } = "d MMM yyyy";

    /// <summary>Every piece of fixed text, for translating the template.</summary>
    public InvoiceLabels Labels { get; init; } = InvoiceLabels.English;

    public PdfPaperSize PaperSize { get; init; } = PdfPaperSize.A4;

    /// <summary>
    /// Overrides how amounts are written. By default they use the culture's grouping and the
    /// currency's decimals, with the currency code only on the totals.
    /// </summary>
    public Func<decimal, string>? FormatAmount { get; init; }
}

/// <summary>
/// The fixed text on an invoice. Change any of it with <c>with</c>:
/// <code>InvoiceLabels.English with { TaxName = "VAT", BillTo = "Customer" }</code>
/// </summary>
public sealed record InvoiceLabels
{
    public static InvoiceLabels English { get; } = new InvoiceLabels();

    public string InvoiceTitle { get; init; } = "Invoice";

    public string QuoteTitle { get; init; } = "Quote";

    public string ReceiptTitle { get; init; } = "Receipt";

    public string CreditNoteTitle { get; init; } = "Credit note";

    public string Number { get; init; } = "Number";

    public string IssueDate { get; init; } = "Date";

    public string DueDate { get; init; } = "Due date";

    public string ValidUntil { get; init; } = "Valid until";

    public string Reference { get; init; } = "Reference";

    public string OriginalInvoice { get; init; } = "Credits invoice";

    public string From { get; init; } = "From";

    public string BillTo { get; init; } = "Bill to";

    public string ShipTo { get; init; } = "Ship to";

    public string Description { get; init; } = "Description";

    public string Quantity { get; init; } = "Qty";

    public string UnitPrice { get; init; } = "Unit price";

    public string Discount { get; init; } = "Discount";

    public string Tax { get; init; } = "Tax";

    public string Amount { get; init; } = "Amount";

    public string Subtotal { get; init; } = "Subtotal";

    public string DiscountTotal { get; init; } = "Discounts";

    /// <summary>The name tax is given in the totals, followed by its rate: "VAT", "GST", "Sales tax".</summary>
    public string TaxName { get; init; } = "Tax";

    public string Total { get; init; } = "Total";

    public string AmountPaid { get; init; } = "Paid";

    public string AmountDue { get; init; } = "Amount due";

    public string TotalCredit { get; init; } = "Total credit";

    public string PaidStamp { get; init; } = "PAID";

    public string PaymentDetails { get; init; } = "Payment details";

    public string Bank { get; init; } = "Bank";

    public string AccountName { get; init; } = "Account name";

    public string Iban { get; init; } = "IBAN";

    public string Bic { get; init; } = "BIC";

    public string AccountNumber { get; init; } = "Account no.";

    public string PaymentReference { get; init; } = "Payment reference";

    public string Notes { get; init; } = "Notes";

    public string Terms { get; init; } = "Terms";

    public string TaxId { get; init; } = "Tax ID";

    public string RegistrationNumber { get; init; } = "Reg. no.";

    /// <summary>The page counter in the footer; <c>{page}</c> and <c>{pages}</c> are replaced.</summary>
    public string Page { get; init; } = "Page {page} of {pages}";

    public string Title(InvoiceDocumentKind kind) => kind switch
    {
        InvoiceDocumentKind.Quote => QuoteTitle,
        InvoiceDocumentKind.Receipt => ReceiptTitle,
        InvoiceDocumentKind.CreditNote => CreditNoteTitle,
        _ => InvoiceTitle
    };
}
