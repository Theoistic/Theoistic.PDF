# Theoistic.PDF

PDF generation, editing and signing for .NET with **no bundled engine, no native binaries and no
copyleft**.

The package is pure managed code. It ships nothing to install, downloads nothing at runtime, and
carries no LGPL or AGPL obligations — which is unusual in this corner of the .NET ecosystem,
where most options are either a 40 MB native library or a dual licence with a revenue threshold.

> **2.0 is a fresh start and is not backwards compatible with 1.x.** wkhtmltopdf and its native
> binaries are gone, `PDFBuilder` is now `HtmlPdfBuilder` with no alias, and HTML rendering needs
> a browser on the machine. See [Upgrading from 1.x](#upgrading-from-1x) before you update.

There are four levels to work at. The first three need nothing but .NET:

| | You work in | Needs a browser? |
|---|---|---|
| **`Theoistic.PDF.Templates`** | A model you fill in — `InvoiceTemplate.Modern.ToPdf(invoice)` | **No** |
| **`Theoistic.PDF.Layout`** | Blocks that flow: headings, paragraphs, tables, images. Wraps and paginates for you | **No** |
| **`Theoistic.PDF.Writing`** | Points on a page: text, paths, colour, images | **No** |
| **`HtmlPdfBuilder`** | HTML and CSS, rendered by Chrome, Edge or Chromium | **Yes** |

The non-browser levels embed TrueType fonts as subsets, so names like *Łukasz Żółć* or
*Şişli* print correctly, and embed JPEG and PNG images with their transparency. The HTML level
has full browser fidelity, at the cost of needing a browser installed — **if you are deploying
HTML rendering to Docker, Azure or DigitalOcean, read [Deployment](#deployment) first.**

Existing PDFs can be opened too: filled in, extended with more pages, and sealed with a PAdES
digital signature, all in managed code — see
[Editing and signing existing PDFs](#editing-and-signing-existing-pdfs).

- Targets `net9.0` and `net10.0`
- Dependencies: `Microsoft.AspNetCore.Http.Abstractions`, `Microsoft.Extensions.DependencyInjection.Abstractions`, `System.Security.Cryptography.Pkcs` — all MIT, all Microsoft
- Licence: MIT

## Install

```bash
dotnet add package Theoistic.PDF
```

## What's new in 2.1

| Feature | In one line | More |
|---|---|---|
| Open existing PDFs | `PdfEditor.Open(bytes)` — tables, cross-reference streams, object streams, damaged files repaired | [Editing and signing](#editing-and-signing-existing-pdfs) |
| Draw on existing pages | `editor.DrawOn(0).Content.DrawText(...)` — in the coordinates you see, rotation and crop box handled | [Editing and signing](#editing-and-signing-existing-pdfs) |
| Add pages to a document | `editor.AppendPages(flow.ToArray())`, `editor.AddPage(PdfPaperSize.A4)` | [Editing and signing](#editing-and-signing-existing-pdfs) |
| Incremental saves | `editor.Save()` — the original bytes are kept exactly; everything new is appended | [Editing and signing](#editing-and-signing-existing-pdfs) |
| PAdES digital signatures | `await PdfSigner.SignAsync(pdf, new PdfSignatureOptions { Certificate = cert })` — RSA or ECDSA | [Signing](#signing) |
| Trusted timestamps | `TimestampAuthority = new Uri(...)` — an RFC 3161 timestamp on the signature (PAdES B-T) | [Signing](#signing) |
| Signature verification | `PdfSignatureVerifier.Verify(pdf)` — every signature, and whether each still holds | [Verifying](#verifying) |

## What's new in 2.0

| Feature | In one line | More |
|---|---|---|
| Invoice templates | `InvoiceTemplate.Modern.ToPdf(invoice, new InvoiceOptions { Logo = logo })` | [Invoices](#invoices) |
| Quotes, receipts, credit notes | `new InvoiceOptions { Kind = InvoiceDocumentKind.Receipt }` | [Invoices](#invoices) |
| Correct invoice arithmetic | `InvoiceTotals.Calculate(invoice)` — decimal, per-rate tax, currency precision | [The arithmetic](#the-arithmetic-is-done-for-you-and-done-properly) |
| Your own templates | `class MyTemplate : InvoiceTemplate` | [Your own template](#adding-to-an-invoice-or-making-your-own-template) |
| Flowing documents | `new FlowDocument().Heading(...).Table(items, ...)` — wraps, paginates, repeats headers | [Documents without coordinates](#documents-without-coordinates) |
| Embedded TrueType fonts | `PdfFontFamily.FromFiles("NotoSans-Regular.ttf", "NotoSans-Bold.ttf")` — subset, searchable | [Fonts](#fonts) |
| Text checked before rendering | `doc.FindUnsupportedText()` lists every character a font cannot draw | [Fonts](#fonts) |
| JPEG and PNG images | `PdfImage.FromFile("logo.png")` — PNG transparency kept | [Images](#images) |
| Drawing API | `page.Content.DrawText(...)`, `.Rectangle(...)`, `.DrawImage(...)` | [Drawing directly](#drawing-directly) |
| HTML without native binaries | `HtmlPdfBuilder` drives an installed Chrome, Edge or Chromium, or a remote one | [HTML to PDF](#html-to-pdf) |
| Reproducible output | The same input always produces byte-identical files | [Drawing directly](#drawing-directly) |

## Invoices

Fill in an `Invoice`, pick a template, get a PDF. No coordinates, no HTML, no browser.

```csharp
using Theoistic.PDF.Templates;
using Theoistic.PDF.Writing;

var invoice = new Invoice
{
    Number = "2026-0042",
    IssueDate = new DateOnly(2026, 10, 22),
    DueDate = new DateOnly(2026, 11, 21),
    Currency = "EUR",
    TaxRate = 0.25m,
    Seller = new Party { Name = "Theoistic ApS", Address = "Niels Finsens gøta 1\nFO-100 Tórshavn", TaxId = "FO 123456" },
    Buyer = new Party { Name = "Acme Industries Ltd", Address = "14 Harbour Road\nBelfast BT3 9DT" },
    Lines =
    {
        new InvoiceLine("PDF writer core", quantity: 1, unitPrice: 1200m),
        new InvoiceLine("Consulting", quantity: 6.5m, unitPrice: 140m) { Unit = "hrs", Detail = "On-site architecture review" },
        new InvoiceLine("Printed manual", quantity: 3, unitPrice: 18.50m) { TaxRate = 0m },
    },
    Payment = new PaymentDetails { Iban = "FO97 6460 0001 6312 34", Bic = "EIKBFOTF", Reference = "2026-0042" },
    Notes = "Thank you for your business.",
};

byte[] pdf = InvoiceTemplate.Modern.ToPdf(invoice, new InvoiceOptions
{
    Logo = PdfImage.FromFile("logo.png"),
});
```

Three designs are built in — **`Modern`** (accent header, striped table, an accent bar for the
amount due), **`Classic`** (serif, ruled, ledger-style double rule above the total) and
**`Minimal`** (hairlines, no fills, the accent kept for the one figure that matters).

The same model prints as four documents:

```csharp
new InvoiceOptions { Kind = InvoiceDocumentKind.Quote }       // "Valid until", no payment section
new InvoiceOptions { Kind = InvoiceDocumentKind.Receipt }     // marked paid
new InvoiceOptions { Kind = InvoiceDocumentKind.CreditNote }  // "Total credit"; Reference names the invoice credited
```

A part-paid invoice shows what has been paid and what is still due:

```csharp
invoice.AmountPaid = 500m;   // adds "Paid -500.00 EUR" and "Amount due" rows
```

### The arithmetic is done for you, and done properly

Totals are never supplied — they are calculated from the lines, so a printed total can never
disagree with the lines above it. The same figures are available for your own use, such as
storing them alongside the invoice:

```csharp
InvoiceTotals totals = InvoiceTotals.Calculate(invoice);

Console.WriteLine($"{totals.Subtotal} + {totals.TaxTotal} tax = {totals.Total}");

foreach (TaxGroup tax in totals.Taxes)
{
    Console.WriteLine($"{tax.Rate:P0} on {tax.Base}: {tax.Amount}");
}
```

- All money is `decimal`, rounded to the **currency's own precision**: two places for EUR, none
  for JPY or ISK, three for KWD.
- Tax is computed **once per rate**, on the sum of the lines at that rate, then rounded once —
  the way EU VAT is meant to be done, which avoids the cent-out errors of rounding every line.
- Midpoints round **away from zero**, as people do with money, not to even as .NET does by default.
- `PricesIncludeTax = true` extracts tax from gross prices instead of adding it.
- Zero-rated lines form their own 0% group, because VAT invoices have to disclose them.
- Discount and per-line tax columns appear only when they say something — a discount column when
  a line is discounted, a tax column when lines are taxed at different rates.

### Options

| Option | Default | |
|---|---|---|
| `Logo` | none | JPEG or PNG. Without one, the seller's name is set as the letterhead |
| `AccentColor` | per template | `PdfColor.FromHex("#0f766e")` |
| `Fonts` | Helvetica or Times | See [Fonts](#fonts) — needed for text outside Western European |
| `Culture` | invariant | Number and date formatting: `CultureInfo.GetCultureInfo("da-DK")` |
| `Labels` | English | Every fixed string: `InvoiceLabels.English with { TaxName = "VAT", BillTo = "Customer" }` |
| `PaperSize` | A4 | `PdfPaperSize.Letter` and others |
| `FormatAmount` | culture + currency decimals | Your own amount formatter |

### Text the font cannot show is caught up front

The default fonts are the standard PDF typefaces, which embed nothing but only cover Western
European text. Before rendering, a template checks every string against its fonts and, if any
character is missing, throws a `PdfTextCoverageException` listing **every** problem at once —
not just the first one it trips over:

```
4 character(s) cannot be shown in the document's fonts:
  U+0141 'Ł' in "Łukasz Żółć" (not in Helvetica, Helvetica-Bold)
  U+017B 'Ż' in "Łukasz Żółć" (not in Helvetica, Helvetica-Bold)
  U+0142 'ł' in "Łukasz Żółć" (not in Helvetica, Helvetica-Bold)
  U+0107 'ć' in "Łukasz Żółć" (not in Helvetica, Helvetica-Bold)
Set InvoiceOptions.Fonts to a TrueType family that covers these characters, for example PdfFontFamily.FromFiles("NotoSans-Regular.ttf", "NotoSans-Bold.ttf").
```

`ó` is not listed: WinAnsi has it. The full detail, one entry per character and font, is on
the exception's `Problems`.

### Adding to an invoice, or making your own template

`ToDocument` returns the laid-out document before it is rendered, so you can append to any
template:

```csharp
FlowDocument doc = InvoiceTemplate.Classic.ToDocument(invoice);
doc.PageBreak();
doc.Heading("Terms and conditions");
doc.Paragraph(termsText);
doc.Save("invoice.pdf");
```

To design your own, derive from `InvoiceTemplate` and arrange the sections in `Compose`. The
base class supplies the parts every invoice shares — `PartyParagraph`, `MetaTable`,
`LinesTable`, `TotalRows`, `TotalsTable`, `PaymentParagraph`, `NotesBlocks`, `Letterhead` — and
the `InvoiceContext` it hands you formats money, dates and percentages for the chosen culture.
Validation, the totals and the font coverage check all come with it.

```csharp
using Theoistic.PDF.Layout;
using Theoistic.PDF.Templates;
using Theoistic.PDF.Writing;

public sealed class LetterheadTemplate : InvoiceTemplate
{
    public override string Name => "Letterhead";

    protected override PdfColor DefaultAccent => PdfColor.FromHex("#7c2d12");

    protected override PdfFontFamily DefaultFonts => PdfFontFamily.Helvetica;

    protected override FlowDocument Compose(InvoiceContext c)
    {
        TextStyle body = c.Style(10);
        FlowDocument doc = NewDocument(c, Thickness.Millimetres(20, 18, 20, 18), body, c.Style(8, color: PdfColor.Gray(0.5)));

        doc.Add(Letterhead(c, c.Style(18, bold: true, color: c.Accent)));
        doc.Add(new Paragraph($"{c.Title} {c.Invoice.Number} — {c.Date(c.Invoice.IssueDate)}", c.Style(14, bold: true))
        {
            SpaceBefore = 12,
            SpaceAfter = 12,
        });
        doc.Add(PartyParagraph(c, c.Invoice.Buyer, body with { Bold = true }, body));
        doc.Add(LinesTable(c, new TableStyle { HeaderBackground = c.Accent, HeaderText = c.Style(9, bold: true, color: PdfColor.White) }, c.Style(8)));
        doc.Add(TotalsTable(TotalRows(c), body, body, body with { Bold = true }, body with { Bold = true }, new Thickness(2, 6)));

        return doc;
    }
}

byte[] pdf = new LetterheadTemplate().ToPdf(invoice);
```

## Documents without coordinates

`FlowDocument` is the layer the templates are built on, and it is public. Add blocks; rendering
wraps the text, sizes the tables, breaks pages where it has to, repeats table headers on
continuation pages, and draws page numbers once the page count is known.

```csharp
using Theoistic.PDF.Layout;

var doc = new FlowDocument();
doc.Footer.Center = "Page {page} of {pages}";

doc.Heading("Open orders");
doc.Paragraph("Everything not yet shipped, as of this morning.");

doc.Table(orders, t => t
    .Column("Order", o => o.Id, ColumnWidth.Auto)
    .Column("Customer", o => o.Customer)
    .Column("Total", o => o.Total.ToString("N2"), ColumnWidth.Auto, TextAlign.Right)
    .WithStyle(new TableStyle { HeaderBackground = PdfColor.FromHex("#1f3a5f"), AlternateRowBackground = PdfColor.Gray(0.96) }));

byte[] pdf = doc.ToArray();
```

| Block | |
|---|---|
| `Paragraph` | Wrapped text, mixing styles in one paragraph: `new Paragraph().Add("Due: ").Add(date, bold)`. Splits across pages line by line |
| `TableBlock` | Columns `Fixed`, `Relative` or `Auto` (sized to content — right for numbers). Rows never split; the header repeats and is never stranded at the foot of a page |
| `ColumnsBlock` | Blocks side by side — a logo beside a title, two addresses |
| `PanelBlock` | Blocks in a padded box with a fill and border |
| `KeepTogether` | Blocks that move to the next page as a group rather than split |
| `ImageBlock` | An image scaled to fit, keeping its proportions |
| `RuleBlock`, `SpacerBlock`, `PageBreakBlock` | What they say |

Every block takes `SpaceBefore`, `SpaceAfter`, and `MaxWidth` with `HorizontalAlign` — so a
totals table can be 250pt wide and sit at the right. Leading space is dropped at the top of a
page, as in a word processor.

Styles are immutable records, so variations read naturally:
`doc.DefaultStyle with { Size = 14, Bold = true, Color = PdfColor.FromHex("#1f3a5f") }`.

Putting the containers together — a logo beside a title, a paragraph mixing styles, and a boxed
section that stays with its heading:

```csharp
var doc = new FlowDocument();
doc.Header.Right = "Quarterly report";
doc.Header.RuleColor = PdfColor.Gray(0.8);
doc.Header.OnFirstPage = false;
doc.Footer.Center = "Page {page} of {pages}";

TextStyle muted = doc.DefaultStyle with { Size = 8.5, Color = PdfColor.Gray(0.45) };

doc.Add(new ColumnsBlock()
    .Add(ColumnWidth.Relative(), new ImageBlock(PdfImage.FromFile("logo.png"), maxHeight: 40))
    .Add(ColumnWidth.Relative(), new Paragraph("Q3 2026", doc.DefaultStyle with { Size = 20, Bold = true }) { Align = TextAlign.Right }));

doc.Add(new Paragraph()
    .Add("Revenue grew ")
    .Add("18%", doc.DefaultStyle with { Bold = true, Color = PdfColor.FromHex("#15803d") })
    .Add(" on the quarter, the third consecutive quarter of growth."));

doc.Add(new KeepTogether(
    new Paragraph("Outlook", doc.DefaultStyle with { Bold = true }) { SpaceBefore = 12, SpaceAfter = 4 },
    new PanelBlock(new Paragraph("Guidance for the full year is unchanged.", muted)) { Background = PdfColor.Gray(0.95) }));

// Empty when every character can be drawn in the fonts the document uses.
IReadOnlyList<UnsupportedText> problems = doc.FindUnsupportedText();

doc.Save("report.pdf");
```

## Fonts

| | Standard (`StandardFont`) | TrueType (`TrueTypeFont`) |
|---|---|---|
| Embedded | Nothing — every reader has them | A subset of the glyphs you used |
| Covers | WinAnsi: Western European | Whatever the font covers |
| Licence | None applies | The font's own; see below |

```csharp
var family = PdfFontFamily.FromFiles("NotoSans-Regular.ttf", "NotoSans-Bold.ttf");
var style = new TextStyle { Family = family, Size = 10 };
```

- **Only the glyphs you draw are embedded**, renumbered densely, so a 19 MB CJK font costs a few
  kilobytes in the document. Each document keeps its own subset, so one loaded font can serve any
  number of documents concurrently.
- **Text stays searchable and copyable**: every embedded font carries a ToUnicode map.
- **The font's embedding licence is honoured.** A font marked restricted is refused; a font
  marked no-subsetting is embedded whole.
- `.ttf` and `.ttc` collections (`TrueTypeFont.Load("msyh.ttc", collectionIndex: 0)`) are
  supported. CFF-outline `.otf` files are refused with a clear message; most families also ship a
  `.ttf`.
- Text is set **one glyph per character**, with no kerning, ligatures or complex shaping. Latin,
  Greek, Cyrillic and CJK print correctly. **Arabic, Hebrew and Indic scripts need shaping and
  bidirectional layout, and will not render correctly** — use the HTML path for those.

## Images

```csharp
PdfImage logo = PdfImage.FromFile("logo.png");
(double w, double h) = logo.FitWithin(160, 60);
```

**JPEG** is embedded byte for byte — PDF decodes JPEG itself, so there is no quality loss and no
decoder. Baseline and progressive, greyscale, RGB and CMYK, including Adobe's inverted CMYK.
**PNG** is decoded and re-compressed, every colour type and bit depth, interlaced or not, and its
**transparency is preserved** — a logo on a transparent background sits cleanly on any colour.
An image drawn on every page is stored once.

## Drawing directly

`Theoistic.PDF.Writing` is the level everything above is built on: pages and the PDF drawing
operators, for when you want exact control.

```csharp
using Theoistic.PDF.Writing;

var document = new PdfDocument();
PdfPage page = document.AddPage(PdfPaperSize.A4);

page.Content
    .SetFillColor(PdfColor.FromHex("#1f3a5f"))
    .Rectangle(0, page.Height - 96, page.Width, 96)
    .Fill();

page.Content.DrawText("STATEMENT", StandardFont.HelveticaBold, 28, 56, page.Height - 56);

// Text can be measured, so right-aligning is arithmetic rather than guesswork.
const string total = "2,580.00";
double width = StandardFont.HelveticaBold.GetTextWidth(total, 12);

page.Content.DrawText(total, StandardFont.HelveticaBold, 12, page.Width - 56 - width, 200);
page.Content.DrawImage(PdfImage.FromFile("logo.png"), 56, 120, 90, 30);

byte[] pdf = document.ToArray();
```

- **Coordinates are PDF user space**: the origin is the **bottom-left** corner and y increases
  upwards. The layout level is where top-down coordinates live.
- **Output is deterministic.** Nothing timestamped or random is written unless you set it — even
  font subset tags are derived, not random — so the same calls produce byte-identical files.

## Editing and signing existing PDFs

A form made in Word, a contract from a supplier, a government template: `PdfEditor` opens a PDF
you did not make, so you can fill it in, add pages after it, and seal it with a digital
signature. It is the same managed code as the rest of the package — no rendering engine, nothing
native.

```csharp
using Theoistic.PDF.Editing;
using Theoistic.PDF.Layout;
using Theoistic.PDF.Writing;

PdfEditor editor = PdfEditor.Open(File.ReadAllBytes("power-of-attorney.pdf"));

PdfPage page = editor.DrawOn(0);
page.Content.DrawText("Jóhanna Á. Hansen", StandardFont.Helvetica, 12, 84, 624);
page.Content.DrawImage(PdfImage.FromFile("signature.png"), 312, 110, 150, 40);

editor.AppendPages(new FlowDocument()
    .Heading("Signing record")
    .Paragraph("Filled in and signed through the HR portal.")
    .ToArray());

byte[] filledIn = editor.Save();
```

- **Nothing in the original is rewritten.** `Save` returns the original bytes, exactly as they
  were, followed by one incremental update holding everything new. That is how PDF is designed to
  be extended, and it is the only way that leaves a signature already in the file valid. With
  nothing drawn or added, `Save` returns the original unchanged.
- **Coordinates are the page as you see it.** `editor.Pages` reports each page the way a viewer
  shows it — crop box applied, `/Rotate` applied, so a portrait page rotated by 90° reports a
  landscape width and height — and `DrawOn` gives you a surface in exactly those coordinates,
  origin bottom-left. Mapping that onto the page's own coordinate space is done for you.
- **It is the ordinary drawing API.** `DrawOn` returns a normal `PdfPage`, so TrueType fonts are
  embedded as subsets and PNG transparency is kept, as in a new document. The page's existing
  content is wrapped so that whatever state it leaves behind — a transform, a clip, a colour —
  cannot leak into what you draw.
- `AddPage(width, height)` appends a blank page. `AppendPages(bytes)` appends every page of
  another PDF, such as a `FlowDocument`; the pages keep their content, fonts and images, but not
  their annotations or links.
- The reader understands classic cross-reference tables, cross-reference streams, object
  streams, the hybrid files Word writes, and chains of earlier updates, and it rebuilds a damaged
  cross-reference table by scanning the file, as viewers do. **Encrypted PDFs are refused** with a
  `PdfFormatException`, as is anything that is not a PDF.

### Signing

```csharp
using System.Security.Cryptography.X509Certificates;

using X509Certificate2 certificate = X509CertificateLoader.LoadPkcs12FromFile("seal.pfx", password);

byte[] signed = await PdfSigner.SignAsync(filledIn, new PdfSignatureOptions
{
    Certificate = certificate,
    Name = "Jóhanna Á. Hansen",
    Reason = "I grant this power of attorney",
    Location = "Tórshavn",
});
```

The signature is a **PAdES baseline signature** (`/SubFilter /ETSI.CAdES.detached`): a
detached CMS over the whole file, SHA-256, carrying the signing-certificate attribute that PAdES
requires, with the signing time in the signature dictionary rather than inside the CMS. It is
invisible — a signature field with no appearance, on the first page — and it is appended as one
more incremental update.

- RSA and ECDSA keys. Put intermediate certificates in `ExtraCertificates`, so a verifier can build
  the chain without fetching anything.
- Set `TimestampAuthority` to an RFC 3161 service — certificate providers run them — and the
  signature carries a timestamp over its own value (PAdES B-T), proving it existed at that moment
  whatever the signer's clock said. That request is the only network call the library ever makes,
  and only when you ask for it.
- With RSA and a fixed `SigningTime`, signing is deterministic too: the same input produces the
  same signed file.
- **A signed file can be extended and signed again.** Each signature covers the file as it was
  when it was made, and an incremental update leaves that prefix alone, so every earlier signature
  keeps verifying for its own revision. Verifiers do apply a modification policy on top, though:
  adding another signature after someone has signed is fine, but drawing on a page or adding
  pages after someone has signed is reported as a change to what they signed — pyHanko, for one,
  rejects the earlier signature for it. **Draw and append first, then sign.**

### Verifying

```csharp
foreach (PdfSignatureInfo signature in PdfSignatureVerifier.Verify(signed))
{
    Console.WriteLine(
        $"{signature.FieldName}: {signature.Certificate?.Subject}, intact {signature.IntegrityValid}, " +
        $"whole document {signature.CoversWholeDocument}");
}
```

`Verify` lists every signature in the file, oldest first. `IntegrityValid` means the signed bytes
are exactly what was signed and the signature checks out against the key of the certificate it
carries. `CoversWholeDocument` is false for a signature followed by later updates — normal for
every signature but the last, and worth a look when a document was meant to be final.

**Whether to trust the certificate is your decision**, not the verifier's: build its chain with
`X509Chain` against the roots you accept, or compare its thumbprint with the one you issued.

## HTML to PDF

When you already have HTML, or need what only a browser does — complex scripts, CSS grid, web
fonts — `HtmlPdfBuilder` renders it with a Chromium-family browser already on the machine.

Register the renderer once at startup. Both calls are needed: `AddTheoisticPDF` registers the
services, `UseTheoisticPDF` lets the parameterless `HtmlPdfBuilder()` constructor find them.

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTheoisticPDF();

var app = builder.Build();

app.UseTheoisticPDF();
```

Outside ASP.NET Core — a worker, a console app, a test — there is an `IServiceProvider` overload:

```csharp
var services = new ServiceCollection();
services.AddTheoisticPDF();

ServiceProvider provider = services.BuildServiceProvider();
provider.UseTheoisticPDF();
```

Then build a document:

```csharp
using var pdfBuilder = new HtmlPdfBuilder();

byte[] pdf = await pdfBuilder
    .Settings(settings =>
    {
        settings.PaperSize = PaperKind.A4;
        settings.Orientation = Orientation.Portrait;
        settings.DocumentTitle = "Invoice 2026-0042";
        settings.Margins = new MarginSettings { Unit = Unit.Millimeters, Top = 20, Bottom = 18, Left = 15, Right = 15 };
    })
    .BuildAsync("<html><body><h1>Hello</h1></body></html>");
```

A browser is launched on first use and reused. Each render gets its own tab, so concurrent
requests do not interfere, and a browser that dies is replaced on the next render.

### Check at startup, not on the first request

A missing browser is a deployment problem, and you want to find out about it when the app starts
rather than when a user asks for a document:

```csharp
var renderer = app.Services.GetRequiredService<IPdfRenderer>();
PdfRendererStatus status = await renderer.CheckAsync();

if (!status.IsAvailable)
{
    app.Logger.LogError("No PDF renderer: {Reason}", status.Description);
}
```

`Description` names the browser and version when it works (`Edg/155.0.4283.62 (C:\Program Files
(x86)\Microsoft\Edge\Application\msedge.exe)`), and says what it probed and failed to find when
it does not.

### HTML templates

A small Mustache renderer is included, so you do not need a templating dependency:

```csharp
string template = await File.ReadAllTextAsync("Templates/Invoice.html");
string html = new MustacheRenderer().Render(template, model);
```

It supports `{{value}}`, `{{#section}}` for lists and truthy checks, `{{^section}}` for inverted
sections, `{{.}}` for the current list item, and dotted paths like `{{#Order.Lines}}`. Inside a
list section each item becomes the context, so its properties are referenced unqualified.

### Styling

`InjectCSS` points at a stylesheet on disk. The path is rewritten to an absolute `file://` URL and
the link is inserted just above the closing `</head>`, which is what lets the stylesheet reach
fonts and images sitting beside it by relative URL:

```csharp
await pdfBuilder
    .InjectCSS(Path.Combine(env.WebRootPath, "PDFStyle.css"))
    .BuildAsync(html);
```

The injected link is `media="all"`, deliberately. A `media="screen"` stylesheet is ignored while a
browser renders print media, which is what printing does — so a screen-only link silently does
nothing.

By default the page is rendered as **screen** media. Set `WebSettings.PrintMediaType = true` to
render print media instead, which is what activates your `@media print` rules.

### Headers and footers

Set any of `Left`, `Center`, `Right` on `HeaderSettings` or `FooterSettings` and they are rendered
as a three-column band. The old bracket placeholders are translated for you:

| Placeholder | Becomes |
|---|---|
| `[page]`, `[sitepage]` | current page number |
| `[topage]`, `[sitepages]` | total pages |
| `[date]`, `[isodate]`, `[time]` | the date |
| `[title]`, `[doctitle]` | the document title |
| `[webpage]` | the page URL |

```csharp
.BuildAsync(html, content =>
{
    content.HeaderSettings.Right = "[title]";
    content.HeaderSettings.Line = true;
    content.FooterSettings.Right = "Page [page] of [topage]";
    content.FooterSettings.FontSize = 8;
});
```

`[frompage]`, `[section]` and `[subsection]` have no counterpart and are dropped. Literal text
around a placeholder is HTML-escaped, so an ampersand in your text is safe.

Two gotchas inherited from the browser: the header and footer area is carved out of the page
**margins**, so a zero top margin leaves no room for a header; and set `FontSize`/`FontName`
explicitly, because the band renders in a bare document with a tiny default font.

## Choosing a browser

`PdfRendererOptions` offers three arrangements. Pick **one** — a remote browser and a local
executable are alternatives, and setting both throws.

```csharp
// 1. Discover an installed browser. The default; nothing to configure.
builder.Services.AddTheoisticPDF();

// 2. A specific binary.
builder.Services.AddTheoisticPDF(options => options.ExecutablePath = "/usr/bin/chromium");

// 3. A browser running elsewhere. This is the one for a slim container.
//    RemoteAddress takes an http:// DevTools address; RemoteEndpoint takes a ws:// one.
builder.Services.AddTheoisticPDF(options => options.RemoteAddress = new Uri("http://chrome:9222"));
```

Other options worth knowing:

| Option | Default | Why you would change it |
|---|---|---|
| `DisableSandbox` | `false` | **Set this in containers.** An unprivileged container cannot create the namespaces Chromium's sandbox needs. Also adds `--disable-dev-shm-usage`, without which a large page outgrows the default 64 MB shared-memory segment and crashes the tab |
| `AllowFileAccess` | `false` | Only for a page that reads local files itself via `fetch`/XHR. Stylesheets, fonts and images load across directories without it |
| `RenderTimeout` | 60s | Long or asset-heavy documents |
| `NavigationTimeout` | 30s | How long to wait for the load event. The page is printed anyway when it elapses, so one slow third-party asset is not fatal |
| `Arguments` | — | Extra browser flags |
| `Proxy` | — | `--proxy-server` syntax |

`ChromiumLocator.Find()` returns the browser that would be discovered, and
`ChromiumLocator.ProbedLocations()` lists every path it looks at — both useful when diagnosing a
deployment.

## Deployment

The managed writer (`Theoistic.PDF.Writing`), and the editing and signing built on it
(`Theoistic.PDF.Editing`), need nothing and run anywhere. Everything below is about the **HTML
path**.

### Windows and macOS, development and servers

Nothing to do. Edge ships with Windows and updates itself; Chrome on macOS is found automatically.

### Docker

You must install a browser into the image, and — easy to miss — **fonts**. A slim base image has
almost none, and without them text renders as empty boxes rather than failing loudly.

**.NET 9** images are Debian 12 based, where `chromium` is a real package:

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base

RUN apt-get update && apt-get install -y --no-install-recommends \
        chromium \
        fontconfig \
        fonts-liberation \
        fonts-noto-core \
        fonts-noto-color-emoji \
        fonts-noto-cjk \
    && fc-cache -f \
    && rm -rf /var/lib/apt/lists/*

ENV CHROMIUM_PATH=/usr/bin/chromium
```

**.NET 10** images are Ubuntu 24.04 based, and there `chromium` is a transitional package that
pulls in snap — which does not work in a container. Install Google Chrome from Google's own
repository instead, or pin an older Debian-based tag such as `9.0-bookworm-slim`, or use the
[sidecar](#sidecar-the-option-that-avoids-all-of-this):

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base

RUN apt-get update && apt-get install -y --no-install-recommends curl gnupg ca-certificates \
    && curl -fsSL https://dl.google.com/linux/linux_signing_key.pub \
        | gpg --dearmor -o /usr/share/keyrings/google-chrome.gpg \
    && echo "deb [arch=amd64 signed-by=/usr/share/keyrings/google-chrome.gpg] http://dl.google.com/linux/chrome/deb/ stable main" \
        > /etc/apt/sources.list.d/google-chrome.list \
    && apt-get update && apt-get install -y --no-install-recommends \
        google-chrome-stable \
        fontconfig fonts-liberation fonts-noto-core fonts-noto-color-emoji fonts-noto-cjk \
    && fc-cache -f \
    && rm -rf /var/lib/apt/lists/*

ENV CHROMIUM_PATH=/usr/bin/google-chrome
```

Note that Google Chrome is proprietary, whereas Debian's `chromium` is the open-source build — if
avoiding proprietary dependencies is the point, prefer the Debian route or the sidecar.

Then point the renderer at it and disable the sandbox:

```csharp
builder.Services.AddTheoisticPDF(options =>
{
    options.ExecutablePath = Environment.GetEnvironmentVariable("CHROMIUM_PATH");
    options.DisableSandbox = true;
});
```

`DisableSandbox` weakens the isolation between the browser and your process, so treat the
container as the security boundary and only render markup you control. If you must render
untrusted HTML, use a sidecar on its own network instead.

**Alpine** (`9.0-alpine`) works too, with different package names:

```dockerfile
RUN apk add --no-cache chromium font-noto font-noto-cjk font-noto-emoji ttf-liberation fontconfig \
    && fc-cache -f
# The binary has been /usr/bin/chromium-browser and /usr/bin/chromium across Alpine releases,
# so confirm which one your image has rather than assuming:
#   docker run --rm <image> sh -c 'command -v chromium chromium-browser'
ENV CHROMIUM_PATH=/usr/bin/chromium
```

### Sidecar: the option that avoids all of this

Run the browser as its own container and keep your application image slim. Nothing to install,
nothing to keep patched in your image, and the browser can be restarted independently.

```yaml
services:
  app:
    build: .
    environment:
      - Pdf__RemoteAddress=http://chrome:9222
    depends_on: [chrome]

  chrome:
    image: chromedp/headless-shell:latest
    # Chrome binds the DevTools port to loopback by default, so a sidecar must be told
    # to listen on the container's interface or nothing can reach it.
    command:
      - --remote-debugging-address=0.0.0.0
      - --remote-debugging-port=9222
      - --disable-dev-shm-usage
      - --no-sandbox
    expose: ["9222"]
```

```csharp
builder.Services.AddTheoisticPDF(options =>
    options.RemoteAddress = new Uri(builder.Configuration["Pdf:RemoteAddress"]!));
```

**Never publish the DevTools port publicly.** It grants full control of the browser, including
reading local files. Keep it on an internal network, use `expose` rather than `ports`, and if you
must map it to a host, bind it to loopback (`127.0.0.1:9222:9222`).

A sidecar image needs fonts just as much as your own would, and they vary a lot — minimal images
such as `headless-shell` carry very few. If your documents are not purely Latin, check inside the
image before trusting it:

```bash
docker run --rm --entrypoint sh chromedp/headless-shell -c 'fc-match "sans-serif:lang=ja"'
```

If that comes back with a fallback carrying no glyphs, build your own sidecar image from the
browser image plus the font packages above.

### Azure

| Target | Works? |
|---|---|
| App Service (Linux, **Code**) | **No.** OS packages cannot be installed, and Chromium fails on missing shared libraries such as `libnspr4.so` |
| App Service (Windows, Code) | Not recommended — no reliable way to install or update a browser |
| App Service for **Containers** | **Yes** — use the Dockerfile above |
| Container Apps | **Yes** — and the natural home for the sidecar pattern |
| Container Instances | **Yes** |
| Functions (Code plans) | **No**, same reason as App Service Code |
| Functions (custom container) | **Yes** |

If you are on App Service Code today and need HTML rendering, your options are to move to a
custom container, point `RemoteAddress` at a browser running elsewhere (a Container App, say), or
use the managed writer instead.

### DigitalOcean

- **App Platform with a Dockerfile** — works. App Platform uses a `Dockerfile` in the repository
  root if one is present, so the Dockerfile above applies unchanged.
- **App Platform with a buildpack** — you cannot run `apt-get`, but an `Aptfile` declaring the
  Chromium and font packages is installed at build time. Packages land outside the default
  `PATH`, so set `ExecutablePath` explicitly rather than relying on discovery.
- **Droplet** — `apt-get install chromium` plus the font packages, same as any Debian/Ubuntu host.
- **App Platform worker + sidecar** — not supported in one component; run the browser as its own
  service and point `RemoteAddress` at it.

### Deployment checklist

1. A browser is installed, or `RemoteAddress` points at one.
2. Font packages are installed — including CJK and emoji if your documents need them.
3. `DisableSandbox = true` in containers.
4. `CheckAsync` is called at startup and its failure is logged loudly.
5. The DevTools port, if any, is not reachable from the internet.

## Settings that a browser cannot honour

The settings types were modelled on wkhtmltox, and some of them describe either that engine's
internals or a physical printer. They are still accepted so existing code compiles, and ignored:

`ColorMode` · `UseCompression` · `DPI` · `ImageDPI` · `ImageQuality` · `Copies` · `Collate` ·
`Outline` · `OutlineDepth` · `DumpOutline` · `EnableIntelligentShrinking` · `MinimumFontSize` ·
`enablePlugins` · `UseExternalLinks` · `UseLocalLinks` · `ProduceForms` · `IncludeInOutline` ·
`PagesCount` · `StopSlowScript` · `DebugJavascript` · `RepeatCustomHeaders` · `CookieJar`

`BlockLocalFileAccess` is a browser-wide launch decision rather than a per-document one — use
`PdfRendererOptions.AllowFileAccess`.

The authoritative list is `PrintSettingsMapper.UnsupportedSettings`, and a test fails if it drifts
from the implementation.

## Troubleshooting

| Symptom | Cause |
|---|---|
| `No Chromium-family browser was found` | Nothing installed. The message lists every path probed; set `ExecutablePath` or `RemoteAddress` |
| `The browser did not publish a DevTools port` | It failed to start. In a container this is almost always the sandbox — set `DisableSandbox = true` |
| Text renders as empty boxes | No fonts in the image. Install `fonts-liberation`, `fonts-noto-core`, and CJK/emoji packages if needed |
| Injected CSS seems ignored | Check the stylesheet is not `@media print` only while rendering screen media, or set `PrintMediaType = true` |
| Custom `@font-face` falls back | The stylesheet is not being applied at all — see above |
| `N resource(s) failed to load` | A referenced asset 404'd. The message names the URLs. Set `LoadSettings.LoadErrorHandling = ContentErrorHandling.Ignore` to render anyway |
| PDF title is `document.html` | The markup has no `<title>` and `DocumentTitle` was not set |
| `PdfTextCoverageException` from a template | Text outside WinAnsi in the standard fonts. Set `InvoiceOptions.Fonts` to a TrueType family that covers it |
| Arabic or Hebrew comes out disconnected or backwards | The non-browser levels do no shaping or bidirectional layout. Use `HtmlPdfBuilder` for those scripts |
| `CFF-outline OpenType font` error | The `.otf` uses CFF outlines, which are not supported. Use the family's `.ttf` build |
| Renders are slow | A browser is started on first use. Call `CheckAsync` at startup to pay that cost before the first request |
| `PdfFormatException: The PDF is encrypted` | `PdfEditor`, `PdfSigner` and `PdfSignatureVerifier` do not decrypt. Save an unencrypted copy of the document first |
| A validator rejects an earlier signature after the document was edited | Drawing on pages or appending pages after a signature changes what was signed, which modification policies do not allow. Do all editing first, then sign |

## Upgrading from 1.x

**2.0 is not backwards compatible with 1.x.** It is a fresh start: the rendering engine was
replaced, the main type was renamed with no alias left behind, and the package no longer ships
anything native. Existing 1.x code will not compile against it unchanged, and code that does
compile can render differently. Plan the upgrade as a migration, not a version bump.

| 1.x | 2.0 |
|---|---|
| `new PDFBuilder()` | `new HtmlPdfBuilder()` — renamed; `PDFBuilder` no longer exists |
| wkhtmltopdf, bundled as native libraries for each platform | A Chrome, Edge or Chromium browser on the machine, or one reached through `RemoteAddress`. Nothing native is shipped |
| Worked in any container with no setup | A container needs a browser and fonts installed, or a browser sidecar — see [Deployment](#deployment) |
| Old WebKit layout | Current Chromium layout. Flexbox, grid and web fonts now work, so pages tuned around WebKit's quirks may look different |
| `WkHtmlAttribute`, `ObjectSettings.GetContent()` | Removed; both existed only for the native interop |
| `ColorMode`, `DPI`, `Outline` and other engine settings | Still compile, but ignored — see [Settings that a browser cannot honour](#settings-that-a-browser-cannot-honour) |
| `InjectCSS` wrote `media="screen"` | Writes `media="all"`, so the stylesheet applies to the PDF |
| HTML was the only way to make a document | Templates, flowing layout and direct drawing need no HTML and no browser |

The quickest migration for code that only used `PDFBuilder` is a rename, plus making sure the
deployment target has a browser:

```csharp
// 1.x
using var builder = new PDFBuilder();

// 2.0
using var builder = new HtmlPdfBuilder();
```

If you only used HTML to produce invoices, consider moving to [the templates](#invoices)
instead: no browser to install, and the totals are calculated for you.

## Contributing

Contributions are welcome. The test suite covers both target frameworks:

```bash
dotnet test
```

Tests that need a browser report themselves inconclusive when none is installed, so the suite
passes on a machine without one.

## Licence

MIT. No native binaries are shipped and nothing is downloaded at runtime, so the only licences
involved are this one and those of the Microsoft packages it references. The browser the HTML path
drives is one you install and license yourself.
