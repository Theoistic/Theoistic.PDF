using Theoistic.PDF.Writing;

namespace Theoistic.PDF.Layout;

/// <summary>
/// Blocks that stay on one page together - a totals section, or a signature line with the text
/// above it. If the group is taller than a whole page it flows normally rather than being cut.
/// </summary>
public sealed class KeepTogether : Block
{
    public KeepTogether(params Block[] blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);

        Blocks.AddRange(blocks);
    }

    public List<Block> Blocks { get; } = new List<Block>();

    internal override IEnumerable<Block> Children => Blocks;

    internal override double Measure(LayoutContext context, double width) =>
        Blocks.Sum(block => block.MeasureWithSpacing(context, width));

    internal override void Render(LayoutContext context)
    {
        if (context.CanBreak && Measure(context, context.Width) > context.PageContentHeight)
        {
            foreach (Block block in Blocks)
            {
                block.Place(context);
            }

            return;
        }

        LayoutContext group = context.Fixed(context.Left, context.Width, context.Cursor);

        foreach (Block block in Blocks)
        {
            block.Place(group);
        }

        context.Cursor = group.Cursor;
    }
}

/// <summary>
/// Blocks side by side: a logo beside the document title, or the seller's address beside the
/// buyer's. Columns are top-aligned and the row is as tall as its tallest column. A row is kept
/// whole on one page.
/// </summary>
public sealed class ColumnsBlock : Block
{
    public List<LayoutColumn> Columns { get; } = new List<LayoutColumn>();

    /// <summary>Space between columns, in points.</summary>
    public double Gap { get; set; } = 18;

    public ColumnsBlock Add(ColumnWidth width, params Block[] blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);

        Columns.Add(new LayoutColumn(width, blocks.ToList()));

        return this;
    }

    internal override IEnumerable<Block> Children => Columns.SelectMany(column => column.Blocks);

    internal override double Measure(LayoutContext context, double width)
    {
        double[] widths = Widths(width);

        return Columns
            .Select((column, i) => column.Blocks.Sum(block => block.MeasureWithSpacing(context, widths[i])))
            .DefaultIfEmpty(0)
            .Max();
    }

    internal override void Render(LayoutContext context)
    {
        double[] widths = Widths(context.Width);
        double x = context.Left;
        double bottom = context.Cursor;

        for (int i = 0; i < Columns.Count; i++)
        {
            LayoutContext column = context.Fixed(x, widths[i], context.Cursor);

            foreach (Block block in Columns[i].Blocks)
            {
                block.Place(column);
            }

            bottom = Math.Max(bottom, column.Cursor);
            x += widths[i] + Gap;
        }

        context.Cursor = bottom;
    }

    private double[] Widths(double available)
    {
        double inner = Math.Max(0, available - (Gap * Math.Max(0, Columns.Count - 1)));
        double fixedTotal = Columns.Where(c => c.Width.Kind == ColumnWidthKind.Fixed).Sum(c => c.Width.Value);

        // Automatic sizing would mean measuring arbitrary blocks without a width to measure them
        // at, so an automatic column here takes an equal share like a relative one.
        double weights = Columns.Where(c => c.Width.Kind != ColumnWidthKind.Fixed).Sum(c => Weight(c.Width));
        double remaining = Math.Max(0, inner - fixedTotal);

        return Columns
            .Select(c => c.Width.Kind == ColumnWidthKind.Fixed
                ? Math.Min(c.Width.Value, inner)
                : weights > 0 ? remaining * Weight(c.Width) / weights : 0)
            .ToArray();

        static double Weight(ColumnWidth width) => width.Kind == ColumnWidthKind.Relative ? width.Value : 1;
    }
}

/// <summary>One column of a <see cref="ColumnsBlock"/>.</summary>
public sealed record LayoutColumn(ColumnWidth Width, List<Block> Blocks);

/// <summary>
/// Blocks inside a padded box with an optional fill and border - an "amount due" callout, or a
/// notes section set apart from the rest. Kept whole on one page.
/// </summary>
public sealed class PanelBlock : Block
{
    public PanelBlock(params Block[] blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);

        Blocks.AddRange(blocks);
    }

    public List<Block> Blocks { get; } = new List<Block>();

    public Thickness Padding { get; set; } = new Thickness(10);

    public PdfColor? Background { get; set; }

    public PdfColor? BorderColor { get; set; }

    public double BorderWidth { get; set; } = 0.75;

    internal override IEnumerable<Block> Children => Blocks;

    internal override double Measure(LayoutContext context, double width)
    {
        double inner = Math.Max(1, width - Padding.Horizontal);

        return Padding.Vertical + Blocks.Sum(block => block.MeasureWithSpacing(context, inner));
    }

    internal override void Render(LayoutContext context)
    {
        double height = Measure(context, context.Width);

        if (Background is { } fill)
        {
            context.FillRectangle(context.Left, context.Cursor, context.Width, height, fill);
        }

        if (BorderColor is { } border)
        {
            context.StrokeRectangle(context.Left, context.Cursor, context.Width, height, border, BorderWidth);
        }

        LayoutContext inside = context.Fixed(
            context.Left + Padding.Left,
            Math.Max(1, context.Width - Padding.Horizontal),
            context.Cursor + Padding.Top);

        foreach (Block block in Blocks)
        {
            block.Place(inside);
        }

        context.Cursor += height;
    }
}

/// <summary>Empty vertical space.</summary>
public sealed class SpacerBlock : Block
{
    public SpacerBlock(double height)
    {
        Height = height >= 0 ? height : throw new ArgumentOutOfRangeException(nameof(height));
    }

    public double Height { get; }

    internal override double Measure(LayoutContext context, double width) => Height;

    internal override void Render(LayoutContext context) => context.Cursor += Height;
}

/// <summary>A horizontal line across the available width.</summary>
public sealed class RuleBlock : Block
{
    public PdfColor Color { get; set; } = PdfColor.Gray(0.75);

    public double Thickness { get; set; } = 0.75;

    internal override double Measure(LayoutContext context, double width) => Thickness;

    internal override void Render(LayoutContext context)
    {
        context.HorizontalLine(context.Left, context.Left + context.Width, context.Cursor + (Thickness / 2), Color, Thickness);
        context.Cursor += Thickness;
    }
}

/// <summary>An image scaled to fit a box without distortion - a logo, typically.</summary>
public sealed class ImageBlock : Block
{
    public ImageBlock(PdfImage image, double maxHeight)
    {
        ArgumentNullException.ThrowIfNull(image);

        Image = image;
        MaxHeight = maxHeight > 0 ? maxHeight : throw new ArgumentOutOfRangeException(nameof(maxHeight));
    }

    public PdfImage Image { get; }

    /// <summary>The tallest the image may be drawn; its width is limited by the available space and <see cref="Block.MaxWidth"/>.</summary>
    public double MaxHeight { get; }

    internal override double Measure(LayoutContext context, double width) => Image.FitWithin(width, MaxHeight).Height;

    internal override void Render(LayoutContext context)
    {
        (double width, double height) = Image.FitWithin(context.Width, MaxHeight);

        double x = context.Left + (HorizontalAlign switch
        {
            TextAlign.Center => (context.Width - width) / 2,
            TextAlign.Right => context.Width - width,
            _ => 0
        });

        context.DrawImage(Image, x, context.Cursor, width, height);
        context.Cursor += height;
    }
}

/// <summary>Starts a new page, unless the current one is still empty.</summary>
public sealed class PageBreakBlock : Block
{
    internal override bool CanSplit => true;

    internal override double Measure(LayoutContext context, double width) => 0;

    internal override void Render(LayoutContext context)
    {
        if (!context.AtTop)
        {
            context.NewPage();
        }
    }
}
