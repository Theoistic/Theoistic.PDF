namespace Theoistic.PDF.Templates;

/// <summary>
/// The arithmetic of an invoice, done once and in <see cref="decimal"/>.
/// <para>
/// Line amounts are rounded to the currency's precision. Tax is then calculated once per rate,
/// on the sum of the lines at that rate, and rounded once. That is how EU VAT is meant to be
/// computed, and it avoids the cent-out discrepancies that rounding tax on every line produces.
/// Midpoints round away from zero, as people do with money, not to even as .NET does by
/// default.
/// </para>
/// </summary>
public sealed class InvoiceTotals
{
    private InvoiceTotals(
        int decimals,
        IReadOnlyList<decimal> lineAmounts,
        decimal discountTotal,
        IReadOnlyList<TaxGroup> taxes,
        decimal subtotal,
        decimal total,
        decimal amountPaid)
    {
        Decimals = decimals;
        LineAmounts = lineAmounts;
        DiscountTotal = discountTotal;
        Taxes = taxes;
        Subtotal = subtotal;
        Total = total;
        AmountPaid = amountPaid;
    }

    /// <summary>The number of decimal places amounts are rounded to.</summary>
    public int Decimals { get; }

    /// <summary>
    /// Each line's amount after its discount, in the same order as <see cref="Invoice.Lines"/>.
    /// Net of tax when prices exclude it, gross when they include it - whichever the line's
    /// unit price is.
    /// </summary>
    public IReadOnlyList<decimal> LineAmounts { get; }

    /// <summary>The total of all line discounts, as a positive amount.</summary>
    public decimal DiscountTotal { get; }

    /// <summary>Tax per rate, in ascending rate order. Lines with no rate contribute no group.</summary>
    public IReadOnlyList<TaxGroup> Taxes { get; }

    public decimal TaxTotal => Taxes.Sum(tax => tax.Amount);

    /// <summary>The total before tax.</summary>
    public decimal Subtotal { get; }

    /// <summary>The total including tax.</summary>
    public decimal Total { get; }

    public decimal AmountPaid { get; }

    public decimal AmountDue => Total - AmountPaid;

    public static InvoiceTotals Calculate(Invoice invoice, InvoiceDocumentKind kind = InvoiceDocumentKind.Invoice)
    {
        ArgumentNullException.ThrowIfNull(invoice);

        int decimals = invoice.CurrencyDecimals ?? CurrencyDecimals(invoice.Currency);

        var amounts = new List<decimal>(invoice.Lines.Count);
        var byRate = new SortedDictionary<decimal, decimal>();
        decimal untaxed = 0;
        decimal discounts = 0;

        foreach (InvoiceLine line in invoice.Lines)
        {
            decimal gross = line.Quantity * line.UnitPrice;
            decimal discount = Round(gross * (line.DiscountRate ?? 0), decimals);
            decimal amount = Round(gross, decimals) - discount;

            amounts.Add(amount);
            discounts += discount;

            if ((line.TaxRate ?? invoice.TaxRate) is { } rate)
            {
                byRate[rate] = byRate.GetValueOrDefault(rate) + amount;
            }
            else
            {
                untaxed += amount;
            }
        }

        var taxes = new List<TaxGroup>();
        decimal subtotal = untaxed;
        decimal total = untaxed;

        foreach ((decimal rate, decimal sum) in byRate)
        {
            if (invoice.PricesIncludeTax)
            {
                // The sum already contains the tax; extract it rather than adding it.
                decimal tax = Round(sum - (sum / (1 + rate)), decimals);

                taxes.Add(new TaxGroup(rate, sum - tax, tax));
                subtotal += sum - tax;
                total += sum;
            }
            else
            {
                decimal tax = Round(sum * rate, decimals);

                taxes.Add(new TaxGroup(rate, sum, tax));
                subtotal += sum;
                total += sum + tax;
            }
        }

        // A receipt states that something was paid; one that was left at zero means in full.
        decimal paid = kind == InvoiceDocumentKind.Receipt && invoice.AmountPaid == 0
            ? total
            : invoice.AmountPaid;

        return new InvoiceTotals(decimals, amounts, discounts, taxes, subtotal, total, paid);
    }

    /// <summary>Rounds to <paramref name="decimals"/> places, midpoints away from zero.</summary>
    public static decimal Round(decimal value, int decimals) =>
        Math.Round(value, decimals, MidpointRounding.AwayFromZero);

    /// <summary>The ISO 4217 minor-unit count for a currency code; two when it is not one of the exceptions.</summary>
    public static int CurrencyDecimals(string currency) => currency?.ToUpperInvariant() switch
    {
        "BIF" or "CLP" or "DJF" or "GNF" or "ISK" or "JPY" or "KMF" or "KRW" or "PYG"
            or "RWF" or "UGX" or "UYI" or "VND" or "VUV" or "XAF" or "XOF" or "XPF" => 0,
        "BHD" or "IQD" or "JOD" or "KWD" or "LYD" or "OMR" or "TND" => 3,
        _ => 2
    };
}

/// <summary>Tax at one rate: the amount it applies to and the tax itself.</summary>
/// <param name="Rate">As a fraction: 0.25 for 25%.</param>
/// <param name="Base">The net amount the rate applies to.</param>
/// <param name="Amount">The tax.</param>
public readonly record struct TaxGroup(decimal Rate, decimal Base, decimal Amount);
