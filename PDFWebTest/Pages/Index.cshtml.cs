using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Theoistic.PDF;
using Theoistic.PDF.Rendering;
using Theoistic.PDF.Templates;
using Theoistic.PDF.Writing;
using Theoistic.PDFWebTest.Models;

namespace Theoistic.PDFWebTest.Pages;

public class IndexModel : PageModel
{
    private readonly IWebHostEnvironment environment;
    private readonly IPdfRenderer renderer;
    private readonly ILogger<IndexModel> logger;

    public IndexModel(IWebHostEnvironment environment, IPdfRenderer renderer, ILogger<IndexModel> logger)
    {
        this.environment = environment;
        this.renderer = renderer;
        this.logger = logger;
    }

    /// <summary>What the renderer found, shown on the page so a missing browser is obvious.</summary>
    public PdfRendererStatus Status { get; private set; }

    public async Task OnGetAsync()
    {
        Status = await renderer.CheckAsync();

        if (!Status.IsAvailable)
        {
            logger.LogWarning("No browser available for rendering: {Reason}", Status.Description);
        }
    }

    /// <summary>
    /// Reached at <c>/?handler=Invoice&amp;template=Modern&amp;kind=Invoice</c>. Uses a built-in
    /// template, so it needs no browser and works even where <see cref="Status"/> says none is
    /// installed.
    /// </summary>
    public IActionResult OnGetInvoice(string template = "Modern", InvoiceDocumentKind kind = InvoiceDocumentKind.Invoice)
    {
        InvoiceTemplate chosen = InvoiceTemplate.All.FirstOrDefault(t => string.Equals(t.Name, template, StringComparison.OrdinalIgnoreCase))
            ?? InvoiceTemplate.Modern;

        var invoice = new Invoice
        {
            Number = "2026-0042",
            IssueDate = DateOnly.FromDateTime(DateTime.Today),
            DueDate = DateOnly.FromDateTime(DateTime.Today.AddDays(30)),
            Currency = "EUR",
            TaxRate = 0.25m,
            Reference = "PO-88213",
            Seller = new Party
            {
                Name = "Theoistic ApS",
                Address = "Niels Finsens gøta 1\nFO-100 Tórshavn\nFaroe Islands",
                TaxId = "FO 123456",
                Email = "billing@theoistic.com",
            },
            Buyer = new Party
            {
                Name = "Acme Industries Ltd",
                Address = "14 Harbour Road\nBelfast BT3 9DT\nUnited Kingdom",
                Email = "accounts@acme.example",
            },
            Lines =
            {
                new InvoiceLine("PDF writer core", 1, 1200m) { Detail = "Object model, cross-reference table, content streams" },
                new InvoiceLine("TrueType embedding", 1, 950m) { Detail = "Subsetting and ToUnicode maps", DiscountRate = 0.10m },
                new InvoiceLine("Consulting", 6.5m, 140m) { Unit = "hrs", Detail = "On-site architecture review" },
                new InvoiceLine("Printed manual", 3, 18.50m) { Unit = "pcs", TaxRate = 0m },
            },
            Notes = "Thank you for your business.",
            Terms = "Payment due within 30 days.",
            Payment = new PaymentDetails { BankName = "Betri Banki", Iban = "FO97 6460 0001 6312 34", Bic = "EIKBFOTF", Reference = "2026-0042" },
        };

        byte[] pdf = chosen.ToPdf(invoice, new InvoiceOptions
        {
            Kind = kind,
            Logo = PdfImage.FromFile(Path.Combine(environment.WebRootPath, "logo.png")),
        });

        Response.Headers.ContentDisposition = $"inline; filename=\"{chosen.Name.ToLowerInvariant()}-{kind.ToString().ToLowerInvariant()}.pdf\"";

        return File(pdf, "application/pdf");
    }

    /// <summary>
    /// Reached at <c>/?handler=Pdf</c>. Renders the Mustache template and returns the PDF.
    /// </summary>
    public async Task<IActionResult> OnGetPdfAsync()
    {
        var invoice = new InvoiceModel
        {
            Number = "2026-0042",
            Date = DateTime.Now.ToString("d MMMM yyyy"),
            Customer = "Acme Industries",
            Total = "2,580.00",
            Items =
            {
                new InvoiceModel.Line { Name = "PDF writer core", Value = "1,200.00" },
                new InvoiceModel.Line { Name = "Cross-reference table", Value = "320.00" },
                new InvoiceModel.Line { Name = "Standard-14 font metrics", Value = "450.00" },
                new InvoiceModel.Line { Name = "Content stream builder", Value = "610.00" },
            },
        };

        // Fully qualified because PageModel has its own File method, which wins here.
        string template = await System.IO.File.ReadAllTextAsync(
            Path.Combine(environment.ContentRootPath, "Templates", "Invoice.html"));

        string html = new MustacheRenderer().Render(template, invoice);

        using var builder = new HtmlPdfBuilder(renderer);

        byte[] pdf = await builder
            .Settings(settings =>
            {
                settings.PaperSize = PaperKind.A4;
                settings.Orientation = Orientation.Portrait;
                settings.DocumentTitle = $"Invoice {invoice.Number}";
                settings.Margins = new MarginSettings
                {
                    Unit = Unit.Millimeters,
                    Top = 20,
                    Bottom = 18,
                    Left = 15,
                    Right = 15,
                };
            })
            // The stylesheet is rewritten to an absolute file url, so its @font-face can load
            // the font sitting next to it by relative url.
            .InjectCSS(Path.Combine(environment.WebRootPath, "PDFStyle.css"))
            .BuildAsync(html, content =>
            {
                content.HeaderSettings.Right = "[title]";
                content.HeaderSettings.FontSize = 9;
                content.HeaderSettings.Line = true;

                content.FooterSettings.Left = "Theoistic.PDF sample";
                content.FooterSettings.Right = "Page [page] of [topage]";
                content.FooterSettings.FontSize = 8;
            });

        // Set explicitly rather than passing a download name, which would force an attachment:
        // the link opens in a new tab, so showing it in the browser's own viewer is friendlier
        // while still offering a sensible name when saved.
        Response.Headers.ContentDisposition = $"inline; filename=\"invoice-{invoice.Number}.pdf\"";

        return File(pdf, "application/pdf");
    }
}
