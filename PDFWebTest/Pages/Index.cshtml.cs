using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Theoistic.PDF;
using Theoistic.PDF.Rendering;
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

        using var builder = new PDFBuilder(renderer);

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
