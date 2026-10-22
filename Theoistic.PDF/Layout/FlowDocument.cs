using System.Text;
using Theoistic.PDF.Writing;

namespace Theoistic.PDF.Layout;

/// <summary>
/// A document described as blocks that flow down the page, with no coordinates anywhere.
/// <para>
/// Add headings, paragraphs, tables, images and groups; rendering wraps the text, sizes the
/// tables, breaks pages where it has to, repeats table headers on continuation pages, and draws
/// the page header and footer with page numbers once the page count is known. Nothing here
/// needs a browser.
/// </para>
/// <code>
/// var doc = new FlowDocument();
/// doc.Heading("Open orders");
/// doc.Table(orders, t => t
///     .Column("Order", o => o.Id)
///     .Column("Customer", o => o.Customer, ColumnWidth.Relative(2))
///     .Column("Total", o => o.Total.ToString("N2"), ColumnWidth.Auto, TextAlign.Right));
/// byte[] pdf = doc.ToArray();
/// </code>
/// </summary>
public sealed class FlowDocument
{
    private readonly List<Block> blocks = new List<Block>();

    public FlowDocument(PdfPaperSize? size = null, PdfOrientation orientation = PdfOrientation.Portrait)
    {
        (double width, double height) = (size ?? PdfPaperSize.A4).ToPoints();

        if (orientation == PdfOrientation.Landscape)
        {
            (width, height) = (height, width);
        }

        PageWidth = width;
        PageHeight = height;
    }

    public double PageWidth { get; }

    public double PageHeight { get; }

    /// <summary>Page margins in points. The header and footer are drawn inside the top and bottom margins.</summary>
    public Thickness Margins { get; set; } = Thickness.Millimetres(22, 18, 22, 18);

    /// <summary>The style for any text that does not set its own.</summary>
    public TextStyle DefaultStyle { get; set; } = new TextStyle();

    public PdfDocumentInfo Info { get; } = new PdfDocumentInfo();

    /// <summary>Text across the top of each page. <c>{page}</c> and <c>{pages}</c> become page numbers.</summary>
    public PageBand Header { get; } = new PageBand();

    /// <summary>Text across the bottom of each page. <c>{page}</c> and <c>{pages}</c> become page numbers.</summary>
    public PageBand Footer { get; } = new PageBand();

    public IReadOnlyList<Block> Blocks => blocks;

    /// <summary>The width blocks get between the margins.</summary>
    public double ContentWidth => PageWidth - Margins.Horizontal;

    // ---------------------------------------------------------------- building

    public FlowDocument Add(Block block)
    {
        ArgumentNullException.ThrowIfNull(block);

        blocks.Add(block);

        return this;
    }

    /// <summary>A heading, sized by level: 1 is the largest.</summary>
    public FlowDocument Heading(string text, int level = 1, TextStyle? style = null)
    {
        double size = level switch { 1 => 20, 2 => 15, 3 => 12.5, _ => 11 };

        return Add(new Paragraph(text, style ?? DefaultStyle with { Size = size, Bold = true, LineHeight = 1.2 })
        {
            SpaceBefore = size * 0.6,
            SpaceAfter = size * 0.35,
        });
    }

    public FlowDocument Paragraph(string text, TextStyle? style = null, TextAlign align = TextAlign.Left) =>
        Add(new Paragraph(text, style) { Align = align, SpaceAfter = 4 });

    public FlowDocument Table<T>(IEnumerable<T> items, Action<TableBuilder<T>> configure)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new TableBuilder<T>();
        configure(builder);

        TableBlock table = builder.Build(items);
        table.SpaceAfter = 8;

        return Add(table);
    }

    public FlowDocument Spacer(double height) => Add(new SpacerBlock(height));

    public FlowDocument Rule(PdfColor? color = null, double thickness = 0.75) =>
        Add(new RuleBlock { Color = color ?? PdfColor.Gray(0.75), Thickness = thickness, SpaceBefore = 6, SpaceAfter = 6 });

    public FlowDocument Image(PdfImage image, double maxWidth, double maxHeight, TextAlign align = TextAlign.Left) =>
        Add(new ImageBlock(image, maxHeight) { MaxWidth = maxWidth, HorizontalAlign = align, SpaceAfter = 6 });

    public FlowDocument PageBreak() => Add(new PageBreakBlock());

    // ---------------------------------------------------------------- checking

    /// <summary>
    /// Every character that some font in use cannot show, with the text it appears in.
    /// <para>
    /// Worth calling before rendering text from a database or a form: it reports every problem
    /// in one go, where rendering stops at the first. Text is checked against every font of
    /// every family in use, which errs towards reporting a character that would in fact have
    /// been drawn in a face that has it.
    /// </para>
    /// </summary>
    public IReadOnlyList<UnsupportedText> FindUnsupportedText()
    {
        var texts = new List<string>();
        var families = new HashSet<PdfFontFamily> { DefaultStyle.Family };

        void AddStyle(TextStyle? style)
        {
            if (style is not null)
            {
                families.Add(style.Family);
            }
        }

        void Walk(Block block)
        {
            switch (block)
            {
                case Paragraph paragraph:
                    texts.Add(paragraph.PlainText);
                    AddStyle(paragraph.Style);

                    foreach (TextRun run in paragraph.Runs)
                    {
                        AddStyle(run.Style);
                    }

                    break;

                case TableBlock table:
                    AddStyle(table.Style.BodyText);
                    AddStyle(table.Style.HeaderText);

                    foreach (TableColumn column in table.Columns)
                    {
                        texts.Add(column.Header);
                        AddStyle(column.Style);
                    }

                    foreach (Block cell in table.Rows.SelectMany(row => row))
                    {
                        Walk(cell);
                    }

                    break;
            }

            foreach (Block child in block.Children)
            {
                Walk(child);
            }
        }

        foreach (Block block in blocks)
        {
            Walk(block);
        }

        foreach (PageBand band in new[] { Header, Footer })
        {
            texts.AddRange(new[] { band.Left, band.Center, band.Right }.OfType<string>());
            AddStyle(band.Style);
        }

        var problems = new List<UnsupportedText>();
        var seen = new HashSet<(string, string)>();

        foreach (string text in texts.Where(t => t.Length > 0))
        {
            foreach (PdfFont font in families.SelectMany(family => new[] { family.Regular, family.Bold }).Distinct())
            {
                // Each character is reported once per font, however many strings contain it.
                foreach (Rune rune in text.EnumerateRunes())
                {
                    // Newlines and tabs are consumed by line breaking and never drawn, so a font
                    // having no glyph for them is not a problem.
                    if (Rune.IsControl(rune))
                    {
                        continue;
                    }

                    string character = rune.ToString();

                    if (font.FindUnsupported(character) is not null && seen.Add((character, font.Name)))
                    {
                        problems.Add(new UnsupportedText(character, text, font.Name));
                    }
                }
            }
        }

        return problems;
    }

    // ---------------------------------------------------------------- rendering

    /// <summary>Lays the document out into a new <see cref="PdfDocument"/>.</summary>
    public PdfDocument Render()
    {
        if (ContentWidth <= 0 || PageHeight - Margins.Vertical <= 0)
        {
            throw new InvalidOperationException("The margins leave no room on the page for content.");
        }

        var document = new PdfDocument();

        CopyInfo(document.Info);

        LayoutContext context = LayoutContext.Start(document, this);

        foreach (Block block in blocks)
        {
            block.Place(context);
        }

        int pages = document.Pages.Count;

        for (int i = 0; i < pages; i++)
        {
            DrawBand(document.Pages[i], Header, i + 1, pages, isHeader: true);
            DrawBand(document.Pages[i], Footer, i + 1, pages, isHeader: false);
        }

        return document;
    }

    public byte[] ToArray() => Render().ToArray();

    public void Save(string path) => Render().Save(path);

    public void Save(Stream destination) => Render().Save(destination);

    private void CopyInfo(PdfDocumentInfo target)
    {
        target.Title = Info.Title;
        target.Author = Info.Author;
        target.Subject = Info.Subject;
        target.Keywords = Info.Keywords;
        target.Creator = Info.Creator;
        target.Producer = Info.Producer;
        target.CreationDate = Info.CreationDate;
    }

    private void DrawBand(PdfPage page, PageBand band, int number, int total, bool isHeader)
    {
        if (band.IsEmpty || (number == 1 && !band.OnFirstPage))
        {
            return;
        }

        TextStyle style = band.Style ?? DefaultStyle with { Size = 8, Color = PdfColor.Gray(0.4) };
        PdfFont font = style.Font;
        double cap = font.Ascent * style.Size / 1000;

        // Centred in its margin, so it clears the content whatever the margin size is.
        double baselineFromTop = isHeader
            ? (Margins.Top / 2) + (cap / 2)
            : PageHeight - (Margins.Bottom / 2) + (cap / 2);

        double baseline = page.Height - baselineFromTop;
        double left = Margins.Left;
        double right = PageWidth - Margins.Right;

        void Draw(string? template, TextAlign align)
        {
            if (template is not { Length: > 0 })
            {
                return;
            }

            string text = template
                .Replace("{page}", number.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{pages}", total.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

            double width = font.GetTextWidth(text, style.Size);
            double x = align switch
            {
                TextAlign.Center => left + ((right - left - width) / 2),
                TextAlign.Right => right - width,
                _ => left
            };

            page.Content.SetFillColor(style.Color).DrawText(text, font, style.Size, x, baseline);
        }

        Draw(band.Left, TextAlign.Left);
        Draw(band.Center, TextAlign.Center);
        Draw(band.Right, TextAlign.Right);

        if (band.RuleColor is { } rule)
        {
            double y = isHeader ? baseline - (style.Size * 0.6) : baseline + (style.Size * 1.1);

            page.Content
                .Save()
                .SetStrokeColor(rule)
                .SetLineWidth(0.5)
                .MoveTo(left, y)
                .LineTo(right, y)
                .Stroke()
                .Restore();
        }
    }
}

/// <summary>The three slots of a page header or footer.</summary>
public sealed class PageBand
{
    public string? Left { get; set; }

    public string? Center { get; set; }

    public string? Right { get; set; }

    public TextStyle? Style { get; set; }

    /// <summary>A line separating the band from the content; null for none.</summary>
    public PdfColor? RuleColor { get; set; }

    /// <summary>Whether the band appears on the first page. A letterhead usually makes it redundant there.</summary>
    public bool OnFirstPage { get; set; } = true;

    internal bool IsEmpty => Left is not { Length: > 0 } && Center is not { Length: > 0 } && Right is not { Length: > 0 };
}

/// <summary>A character a font cannot show, and where it was found.</summary>
public sealed record UnsupportedText(string Character, string InText, string FontName)
{
    public override string ToString() =>
        $"U+{char.ConvertToUtf32(Character, 0):X4} '{Character}' in \"{InText}\" is not in {FontName}";
}
