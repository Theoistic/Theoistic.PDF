using Theoistic.PDF.Writing;

namespace Theoistic.PDF.Layout;

/// <summary>
/// Something that occupies vertical space in a flowing document: a paragraph, a table, an image.
/// <para>
/// Blocks are laid out top to bottom. Each is measured at the width available to it and placed
/// where the previous one ended; when one does not fit, it moves to the next page. Paragraphs
/// and tables can split across pages; everything else is kept whole.
/// </para>
/// </summary>
public abstract class Block
{
    /// <summary>Space above the block, in points. Dropped at the top of a page.</summary>
    public double SpaceBefore { get; set; }

    /// <summary>Space below the block, in points.</summary>
    public double SpaceAfter { get; set; }

    /// <summary>
    /// Caps the block's width, in points. A totals table, for instance, is narrower than the
    /// page and sits at the right.
    /// </summary>
    public double? MaxWidth { get; set; }

    /// <summary>Where a block narrower than its space sits horizontally.</summary>
    public TextAlign HorizontalAlign { get; set; } = TextAlign.Left;

    /// <summary>The height of the block's content laid out at <paramref name="width"/>, excluding spacing.</summary>
    internal abstract double Measure(LayoutContext context, double width);

    /// <summary>
    /// Draws the content at the context's cursor and advances it. Splittable blocks may start
    /// new pages through the context; whole blocks are given room before this is called.
    /// </summary>
    internal abstract void Render(LayoutContext context);

    /// <summary>Whether <see cref="Render"/> manages page breaks itself, line by line or row by row.</summary>
    internal virtual bool CanSplit => false;

    /// <summary>Blocks nested inside this one, for walking a document's text and styles.</summary>
    internal virtual IEnumerable<Block> Children => Array.Empty<Block>();

    /// <summary>The width this block gets inside a region of <paramref name="available"/> points.</summary>
    internal double WidthWithin(double available) => Math.Min(MaxWidth ?? available, available);

    /// <summary>Total height including spacing, as a container stacking this block needs it.</summary>
    internal double MeasureWithSpacing(LayoutContext context, double available) =>
        SpaceBefore + Measure(context, WidthWithin(available)) + SpaceAfter;

    /// <summary>
    /// Places the block within the context: applies spacing, width and alignment, makes room,
    /// and renders it. Every container lays out its children through this one path.
    /// </summary>
    internal void Place(LayoutContext context)
    {
        if (!context.AtTop)
        {
            context.Cursor += SpaceBefore;
        }

        double width = Math.Min(MaxWidth ?? context.Width, context.Width);
        double offset = HorizontalAlign switch
        {
            TextAlign.Center => (context.Width - width) / 2,
            TextAlign.Right => context.Width - width,
            _ => 0
        };

        LayoutContext region = context.Narrowed(context.Left + offset, width);

        if (!CanSplit)
        {
            double height = Measure(region, width);

            // A whole block taller than a page could never fit; it is drawn from the top of a
            // fresh page and allowed to run past the bottom, rather than looping forever.
            region.EnsureSpace(height);
        }

        Render(region);

        context.Cursor = region.Cursor + SpaceAfter;
    }
}

/// <summary>
/// The page being drawn on, the region within it, and the cursor - in top-down coordinates,
/// measured in points from the top of the page, so that layout reads the way pages are read.
/// Conversion to PDF's bottom-up space happens only at the moment of drawing.
/// </summary>
internal sealed class LayoutContext
{
    private readonly LayoutRoot root;

    private LayoutContext(LayoutRoot root, double left, double width, double cursor, bool canBreak)
    {
        this.root = root;
        Left = left;
        Width = width;
        Cursor = cursor;
        CanBreak = canBreak;
    }

    public static LayoutContext Start(PdfDocument document, FlowDocument flow)
    {
        var root = new LayoutRoot(document, flow);
        var context = new LayoutContext(root, flow.Margins.Left, root.ContentWidth, flow.Margins.Top, canBreak: true);

        root.NewPage();

        return context;
    }

    public double Left { get; }

    public double Width { get; }

    /// <summary>Distance from the top of the page to where the next thing is drawn.</summary>
    public double Cursor { get; set; }

    /// <summary>False inside a block that must stay whole, where a page break is not allowed.</summary>
    public bool CanBreak { get; }

    public TextStyle DefaultStyle => root.Flow.DefaultStyle;

    public PdfPage Page => root.Page;

    public double ContentTop => root.Flow.Margins.Top;

    public double ContentBottom => root.PageHeight - root.Flow.Margins.Bottom;

    public double Remaining => ContentBottom - Cursor;

    /// <summary>
    /// At the top of a page, where leading space is dropped. Never true inside a whole block:
    /// spacing there is part of the block's measured height and has to be honoured to match it.
    /// </summary>
    public bool AtTop => CanBreak && Cursor <= ContentTop + 0.01;

    /// <summary>The height a whole block can have and still fit on one page.</summary>
    public double PageContentHeight => ContentBottom - ContentTop;

    /// <summary>The same page and cursor, with a different horizontal region.</summary>
    public LayoutContext Narrowed(double left, double width) =>
        new LayoutContext(root, left, width, Cursor, CanBreak);

    /// <summary>A region that cannot break pages, for laying out the inside of a whole block.</summary>
    public LayoutContext Fixed(double left, double width, double top) =>
        new LayoutContext(root, left, width, top, canBreak: false);

    /// <summary>Starts a new page if <paramref name="height"/> does not fit in what is left of this one.</summary>
    public void EnsureSpace(double height)
    {
        if (CanBreak && height > Remaining + 0.01 && !AtTop)
        {
            NewPage();
        }
    }

    public void NewPage()
    {
        if (!CanBreak)
        {
            return;
        }

        root.NewPage();
        Cursor = ContentTop;

        // Whatever is mid-flow - a table repeating its header - redraws onto the new page.
        foreach (Action<LayoutContext> continuation in root.Continuations.ToArray())
        {
            continuation(this);
        }
    }

    /// <summary>Registers something to draw at the top of every page started until it is disposed.</summary>
    public IDisposable OnNewPage(Action<LayoutContext> continuation)
    {
        root.Continuations.Add(continuation);

        return new Removal(() => root.Continuations.Remove(continuation));
    }

    // ---------------------------------------------------------------- drawing, in top-down space

    private double Flip(double fromTop) => root.PageHeight - fromTop;

    public void DrawText(string text, TextStyle style, double x, double baselineFromTop)
    {
        Page.Content
            .SetFillColor(style.Color)
            .DrawText(text, style.Font, style.Size, x, Flip(baselineFromTop));
    }

    public void FillRectangle(double x, double top, double width, double height, PdfColor color)
    {
        Page.Content
            .SetFillColor(color)
            .Rectangle(x, Flip(top + height), width, height)
            .Fill();
    }

    public void StrokeRectangle(double x, double top, double width, double height, PdfColor color, double lineWidth)
    {
        Page.Content
            .Save()
            .SetStrokeColor(color)
            .SetLineWidth(lineWidth)
            .Rectangle(x, Flip(top + height), width, height)
            .Stroke()
            .Restore();
    }

    public void HorizontalLine(double x1, double x2, double top, PdfColor color, double lineWidth)
    {
        Page.Content
            .Save()
            .SetStrokeColor(color)
            .SetLineWidth(lineWidth)
            .MoveTo(x1, Flip(top))
            .LineTo(x2, Flip(top))
            .Stroke()
            .Restore();
    }

    public void DrawImage(PdfImage image, double x, double top, double width, double height) =>
        Page.Content.DrawImage(image, x, Flip(top + height), width, height);

    private sealed class Removal(Action remove) : IDisposable
    {
        private Action? remove = remove;

        public void Dispose() => Interlocked.Exchange(ref remove, null)?.Invoke();
    }

    /// <summary>State shared by every region of one layout pass.</summary>
    private sealed class LayoutRoot(PdfDocument document, FlowDocument flow)
    {
        public PdfDocument Document { get; } = document;

        public FlowDocument Flow { get; } = flow;

        public PdfPage Page { get; private set; } = null!;

        public double PageHeight => Page.Height;

        public double ContentWidth => Flow.PageWidth - Flow.Margins.Horizontal;

        public List<Action<LayoutContext>> Continuations { get; } = new List<Action<LayoutContext>>();

        public void NewPage() => Page = Document.AddPage(Flow.PageWidth, Flow.PageHeight);
    }
}
