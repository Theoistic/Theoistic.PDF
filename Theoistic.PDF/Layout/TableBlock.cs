namespace Theoistic.PDF.Layout;

/// <summary>One column of a <see cref="TableBlock"/>.</summary>
public sealed record TableColumn
{
    public string Header { get; init; } = string.Empty;

    public ColumnWidth Width { get; init; } = ColumnWidth.Relative();

    /// <summary>Alignment of cells created from plain strings.</summary>
    public TextAlign Align { get; init; }

    /// <summary>Alignment of the header; follows <see cref="Align"/> when unset, so numbers line up under their heading.</summary>
    public TextAlign? HeaderAlign { get; init; }

    /// <summary>Style of cells created from plain strings; follows the table's body style when unset.</summary>
    public TextStyle? Style { get; init; }
}

/// <summary>
/// Rows and columns of wrapped text.
/// <para>
/// Columns can be fixed, a share of the remaining width, or sized to their content. Rows are
/// never split: a row that will not fit moves to the next page, and the header is drawn again
/// there. A header is never left alone at the foot of a page, separated from its first row.
/// </para>
/// </summary>
public sealed class TableBlock : Block
{
    private readonly List<Paragraph[]> rows = new List<Paragraph[]>();

    public TableStyle Style { get; set; } = new TableStyle();

    public List<TableColumn> Columns { get; } = new List<TableColumn>();

    public int RowCount => rows.Count;

    public IReadOnlyList<IReadOnlyList<Paragraph>> Rows => rows;

    public TableBlock AddColumn(TableColumn column)
    {
        ArgumentNullException.ThrowIfNull(column);

        Columns.Add(column);

        return this;
    }

    /// <summary>Adds a row of plain text, styled and aligned by each column's settings.</summary>
    public TableBlock AddRow(params string?[] cells)
    {
        ArgumentNullException.ThrowIfNull(cells);

        RequireColumnCount(cells.Length);

        return AddRow(cells
            .Select((text, i) => new Paragraph(text ?? string.Empty) { Align = Columns[i].Align, Style = Columns[i].Style })
            .ToArray());
    }

    /// <summary>Adds a row of rich cells, for content such as a description with a smaller second line.</summary>
    public TableBlock AddRow(params Paragraph[] cells)
    {
        ArgumentNullException.ThrowIfNull(cells);

        RequireColumnCount(cells.Length);
        rows.Add(cells);

        return this;
    }

    private void RequireColumnCount(int count)
    {
        if (count != Columns.Count)
        {
            throw new ArgumentException($"The row has {count} cell(s) but the table has {Columns.Count} column(s).");
        }
    }

    internal override bool CanSplit => true;

    internal override double Measure(LayoutContext context, double width)
    {
        Geometry geometry = Layout(context, width);

        return geometry.HeaderHeight + rows.Sum(row => RowHeight(row, geometry));
    }

    internal override void Render(LayoutContext context)
    {
        if (Columns.Count == 0)
        {
            return;
        }

        Geometry geometry = Layout(context, context.Width);
        double firstRow = rows.Count > 0 ? RowHeight(rows[0], geometry) : 0;

        // The header and the first row move together, so a page never ends on a lone header.
        context.EnsureSpace(geometry.HeaderHeight + firstRow);

        if (Style.ShowHeader)
        {
            DrawHeader(context, geometry);
        }

        using IDisposable? repeat = Style.ShowHeader && Style.RepeatHeader
            ? context.OnNewPage(page => DrawHeader(page, geometry))
            : null;

        for (int r = 0; r < rows.Count; r++)
        {
            Paragraph[] row = rows[r];
            double height = RowHeight(row, geometry);

            context.EnsureSpace(height);

            if (Style.AlternateRowBackground is { } stripe && r % 2 == 1)
            {
                context.FillRectangle(context.Left, context.Cursor, geometry.TotalWidth, height, stripe);
            }

            DrawCells(context, geometry, row, geometry.BodyStyle);

            context.Cursor += height;

            if (Style.RuleColor is { } rule)
            {
                context.HorizontalLine(context.Left, context.Left + geometry.TotalWidth, context.Cursor, rule, Style.RuleWidth);
            }
        }
    }

    private void DrawHeader(LayoutContext context, Geometry geometry)
    {
        if (Style.HeaderBackground is { } background)
        {
            context.FillRectangle(context.Left, context.Cursor, geometry.TotalWidth, geometry.HeaderHeight, background);
        }

        DrawCells(context, geometry, geometry.HeaderCells, geometry.HeaderStyle);

        context.Cursor += geometry.HeaderHeight;

        if ((Style.HeaderRuleColor ?? Style.RuleColor) is { } rule)
        {
            context.HorizontalLine(
                context.Left,
                context.Left + geometry.TotalWidth,
                context.Cursor,
                rule,
                Style.HeaderRuleColor is null ? Style.RuleWidth : Style.RuleWidth * 2);
        }
    }

    private void DrawCells(LayoutContext context, Geometry geometry, Paragraph[] cells, TextStyle fallback)
    {
        Thickness padding = Style.CellPadding;
        double x = context.Left;

        for (int c = 0; c < cells.Length; c++)
        {
            double inner = Math.Max(1, geometry.Widths[c] - padding.Horizontal);

            // Each cell is a small fixed region: a cell never breaks across a page on its own.
            LayoutContext cell = context.Fixed(x + padding.Left, inner, context.Cursor + padding.Top);

            cells[c].RenderWith(cell, fallback);

            x += geometry.Widths[c];
        }
    }

    private double RowHeight(Paragraph[] row, Geometry geometry)
    {
        double tallest = 0;

        for (int c = 0; c < row.Length; c++)
        {
            double inner = Math.Max(1, geometry.Widths[c] - Style.CellPadding.Horizontal);

            tallest = Math.Max(tallest, row[c].MeasureWith(geometry.BodyStyle, inner));
        }

        return tallest + Style.CellPadding.Vertical;
    }

    // ---------------------------------------------------------------- column widths

    private Geometry Layout(LayoutContext context, double width)
    {
        TextStyle body = Style.BodyText ?? context.DefaultStyle;
        TextStyle header = Style.HeaderText ?? body with { Bold = true };

        Paragraph[] headerCells = Columns
            .Select(column => new Paragraph(column.Header) { Align = column.HeaderAlign ?? column.Align, Style = header })
            .ToArray();

        double[] widths = ColumnWidths(width, body, headerCells);

        double headerHeight = 0;

        if (Style.ShowHeader)
        {
            for (int c = 0; c < headerCells.Length; c++)
            {
                double inner = Math.Max(1, widths[c] - Style.CellPadding.Horizontal);
                headerHeight = Math.Max(headerHeight, headerCells[c].MeasureWith(header, inner));
            }

            headerHeight += Style.CellPadding.Vertical;
        }

        return new Geometry(widths, widths.Sum(), headerCells, headerHeight, body, header);
    }

    private double[] ColumnWidths(double available, TextStyle body, Paragraph[] headerCells)
    {
        int count = Columns.Count;
        var widths = new double[count];
        double padding = Style.CellPadding.Horizontal;

        double used = 0;
        double weights = 0;

        for (int c = 0; c < count; c++)
        {
            ColumnWidth spec = Columns[c].Width;

            switch (spec.Kind)
            {
                case ColumnWidthKind.Fixed:
                    widths[c] = spec.Value;
                    used += widths[c];
                    break;

                case ColumnWidthKind.Auto:
                    double natural = Style.ShowHeader ? headerCells[c].NaturalWidth(body) : 0;

                    foreach (Paragraph[] row in rows)
                    {
                        natural = Math.Max(natural, row[c].NaturalWidth(body));
                    }

                    // A hair over the measured width, so rounding can never wrap a number that
                    // was measured to fit exactly.
                    widths[c] = natural + padding + 0.5;
                    used += widths[c];
                    break;

                default:
                    weights += spec.Value;
                    break;
            }
        }

        int relativeCount = Columns.Count(column => column.Width.Kind == ColumnWidthKind.Relative);
        double minimumRelative = relativeCount * 40;

        // Content-sized columns give way first when everything will not fit, so that a long
        // description column is not squeezed to nothing by its neighbours' numbers.
        if (used + minimumRelative > available)
        {
            double autoTotal = Enumerable.Range(0, count)
                .Where(c => Columns[c].Width.Kind == ColumnWidthKind.Auto)
                .Sum(c => widths[c]);

            double excess = used + minimumRelative - available;

            if (autoTotal > 0)
            {
                double shrink = Math.Min(1, excess / autoTotal);

                for (int c = 0; c < count; c++)
                {
                    if (Columns[c].Width.Kind == ColumnWidthKind.Auto)
                    {
                        double reduced = widths[c] * shrink;
                        used -= reduced;
                        widths[c] -= reduced;
                    }
                }
            }
        }

        double remaining = Math.Max(0, available - used);

        if (relativeCount > 0)
        {
            for (int c = 0; c < count; c++)
            {
                if (Columns[c].Width.Kind == ColumnWidthKind.Relative)
                {
                    widths[c] = remaining * Columns[c].Width.Value / weights;
                }
            }
        }
        else if (count > 0)
        {
            // With nothing to absorb the slack, the first column takes it, which is what a
            // label-and-value table wants: labels stretch, values stay hugged to the right.
            widths[0] += remaining;
        }

        // Fixed columns wider than the page are scaled down together rather than running off it.
        double total = widths.Sum();

        if (total > available + 0.01)
        {
            for (int c = 0; c < count; c++)
            {
                widths[c] *= available / total;
            }
        }

        return widths;
    }

    private sealed record Geometry(
        double[] Widths,
        double TotalWidth,
        Paragraph[] HeaderCells,
        double HeaderHeight,
        TextStyle BodyStyle,
        TextStyle HeaderStyle);
}

/// <summary>Builds a <see cref="TableBlock"/> from a list of objects, one row per item.</summary>
public sealed class TableBuilder<T>
{
    private readonly List<(TableColumn Column, Func<T, Paragraph> Cell)> columns = new();

    internal TableStyle Style { get; private set; } = new TableStyle();

    /// <summary>A column of plain text taken from each item.</summary>
    public TableBuilder<T> Column(
        string header,
        Func<T, string?> value,
        ColumnWidth? width = null,
        TextAlign align = TextAlign.Left,
        TextStyle? style = null)
    {
        ArgumentNullException.ThrowIfNull(value);

        var column = new TableColumn { Header = header, Width = width ?? ColumnWidth.Relative(), Align = align, Style = style };

        columns.Add((column, item => new Paragraph(value(item) ?? string.Empty) { Align = align, Style = style }));

        return this;
    }

    /// <summary>A column of rich content built for each item.</summary>
    public TableBuilder<T> Column(
        string header,
        Func<T, Paragraph> content,
        ColumnWidth? width = null,
        TextAlign headerAlign = TextAlign.Left)
    {
        ArgumentNullException.ThrowIfNull(content);

        columns.Add((new TableColumn { Header = header, Width = width ?? ColumnWidth.Relative(), Align = headerAlign }, content));

        return this;
    }

    public TableBuilder<T> WithStyle(TableStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);

        Style = style;

        return this;
    }

    internal TableBlock Build(IEnumerable<T> items)
    {
        var table = new TableBlock { Style = Style };

        foreach ((TableColumn column, _) in columns)
        {
            table.AddColumn(column);
        }

        foreach (T item in items)
        {
            table.AddRow(columns.Select(c => c.Cell(item)).ToArray());
        }

        return table;
    }
}
