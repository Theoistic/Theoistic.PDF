namespace Theoistic.PDF.Templates;

/// <summary>
/// What an invoice says: who is billing whom, for what, and how to pay. The same model drives a
/// quote, a receipt and a credit note - the template's <see cref="InvoiceOptions.Kind"/> decides
/// which of those it is printed as.
/// <para>
/// Totals are never supplied: they are calculated from the lines by
/// <see cref="InvoiceTotals.Calculate"/>, so a printed total can never disagree with the lines
/// above it.
/// </para>
/// </summary>
public sealed class Invoice
{
    public string Number { get; set; } = string.Empty;

    public DateOnly IssueDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);

    /// <summary>When payment is due - or, on a quote, how long the quote is valid.</summary>
    public DateOnly? DueDate { get; set; }

    /// <summary>ISO 4217 code, such as EUR, DKK or USD.</summary>
    public string Currency { get; set; } = "EUR";

    /// <summary>
    /// Decimal places for amounts. Leave unset to use the currency's own: two for most, none for
    /// JPY or ISK, three for KWD or BHD.
    /// </summary>
    public int? CurrencyDecimals { get; set; }

    public Party Seller { get; set; } = new Party();

    public Party Buyer { get; set; } = new Party();

    /// <summary>A delivery address, when it differs from the buyer's.</summary>
    public Party? ShipTo { get; set; }

    public List<InvoiceLine> Lines { get; } = new List<InvoiceLine>();

    /// <summary>
    /// The tax rate for lines that do not set their own, as a fraction: 0.25 for 25%. Null means
    /// no tax.
    /// </summary>
    public decimal? TaxRate { get; set; }

    /// <summary>
    /// Whether unit prices already include tax. Consumer prices usually do; business prices
    /// usually do not. It changes how tax is extracted, not what the total is.
    /// </summary>
    public bool PricesIncludeTax { get; set; }

    /// <summary>A purchase-order number - or, on a credit note, the invoice being credited.</summary>
    public string? Reference { get; set; }

    public string? Notes { get; set; }

    /// <summary>Payment terms, such as "Net 30" or a late-payment clause.</summary>
    public string? Terms { get; set; }

    public PaymentDetails? Payment { get; set; }

    /// <summary>
    /// What has already been paid. On a receipt, leaving this at zero means paid in full.
    /// </summary>
    public decimal AmountPaid { get; set; }
}

/// <summary>A business or person on an invoice.</summary>
public sealed class Party
{
    public string Name { get; set; } = string.Empty;

    /// <summary>The postal address, one line per line.</summary>
    public string? Address { get; set; }

    /// <summary>VAT number or equivalent.</summary>
    public string? TaxId { get; set; }

    /// <summary>Company registration number.</summary>
    public string? RegistrationNumber { get; set; }

    public string? Email { get; set; }

    public string? Phone { get; set; }

    public string? Website { get; set; }
}

/// <summary>One line of an invoice.</summary>
public sealed class InvoiceLine
{
    public InvoiceLine()
    {
    }

    public InvoiceLine(string description, decimal quantity, decimal unitPrice)
    {
        Description = description;
        Quantity = quantity;
        UnitPrice = unitPrice;
    }

    public string Description { get; set; } = string.Empty;

    /// <summary>A second, quieter line under the description: a SKU, a period, a serial number.</summary>
    public string? Detail { get; set; }

    public decimal Quantity { get; set; } = 1;

    /// <summary>The unit the quantity is counted in, such as "hrs" or "pcs".</summary>
    public string? Unit { get; set; }

    public decimal UnitPrice { get; set; }

    /// <summary>This line's tax rate as a fraction; overrides <see cref="Invoice.TaxRate"/>. Zero is a valid rate.</summary>
    public decimal? TaxRate { get; set; }

    /// <summary>A discount as a fraction of the line: 0.1 for 10% off.</summary>
    public decimal? DiscountRate { get; set; }
}

/// <summary>How to pay. Only what is set is printed.</summary>
public sealed class PaymentDetails
{
    public string? BankName { get; set; }

    public string? AccountName { get; set; }

    public string? Iban { get; set; }

    public string? Bic { get; set; }

    /// <summary>A domestic account number, for where IBAN is not used.</summary>
    public string? AccountNumber { get; set; }

    /// <summary>What the payer should quote, so the payment can be matched.</summary>
    public string? Reference { get; set; }

    /// <summary>Anything else, such as a payment link.</summary>
    public string? Other { get; set; }
}

/// <summary>Which document an <see cref="Invoice"/> is printed as.</summary>
public enum InvoiceDocumentKind
{
    /// <summary>A request for payment, with a due date and an amount due.</summary>
    Invoice,

    /// <summary>An offer, with a validity date and no payment section.</summary>
    Quote,

    /// <summary>Proof of payment, marked paid.</summary>
    Receipt,

    /// <summary>A reduction of an earlier invoice, which <see cref="Invoice.Reference"/> names.</summary>
    CreditNote
}
