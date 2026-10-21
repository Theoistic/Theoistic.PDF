namespace Theoistic.PDF.Writing;

/// <summary>
/// Builds the operator sequence for a page's content stream.
/// <para>
/// Coordinates are in PDF user space: the origin is the bottom-left corner of the page and y
/// increases upwards. Nothing here flips the axis.
/// </para>
/// </summary>
public sealed class ContentStream
{
    private readonly PdfPage page;
    private readonly MemoryStream buffer = new MemoryStream();
    private readonly PdfWriter writer;

    private int saveDepth;
    private bool inTextObject;

    internal ContentStream(PdfPage page)
    {
        this.page = page;
        writer = new PdfWriter(buffer);
    }

    /// <summary>
    /// Number of unmatched save-state operators. Checked when the stream is written, because an
    /// unbalanced stack is accepted by some readers and rejected by others.
    /// </summary>
    internal int SaveDepth => saveDepth;

    // ---------------------------------------------------------------- graphics state

    /// <summary>Pushes the graphics state.</summary>
    public ContentStream Save()
    {
        saveDepth++;

        return Operator("q");
    }

    /// <summary>Pops the graphics state.</summary>
    public ContentStream Restore()
    {
        if (saveDepth == 0)
        {
            throw new InvalidOperationException(
                "Restore without a matching Save would pop a graphics state this content stream does not own.");
        }

        saveDepth--;

        return Operator("Q");
    }

    /// <summary>Concatenates a transform onto the current transformation matrix.</summary>
    public ContentStream Transform(PdfMatrix matrix) =>
        Operands(matrix.A, matrix.B, matrix.C, matrix.D, matrix.E, matrix.F).Operator("cm");

    public ContentStream Translate(double dx, double dy) => Transform(PdfMatrix.Translation(dx, dy));

    public ContentStream Scale(double sx, double sy) => Transform(PdfMatrix.Scaling(sx, sy));

    /// <summary>Rotates about the current origin, counter-clockwise, in degrees.</summary>
    public ContentStream Rotate(double degrees) => Transform(PdfMatrix.Rotation(degrees));

    public ContentStream SetLineWidth(double width) => Operands(width).Operator("w");

    public ContentStream SetLineCap(PdfLineCap cap) => Operands((int)cap).Operator("J");

    public ContentStream SetLineJoin(PdfLineJoin join) => Operands((int)join).Operator("j");

    public ContentStream SetMiterLimit(double limit) => Operands(limit).Operator("M");

    /// <summary>
    /// Sets the dash pattern. An empty <paramref name="pattern"/> restores a solid line.
    /// </summary>
    public ContentStream SetDashPattern(double[] pattern, double phase = 0)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        writer.WriteRaw("[");

        for (int i = 0; i < pattern.Length; i++)
        {
            if (i > 0)
            {
                writer.WriteSpace();
            }

            writer.WriteReal(pattern[i]);
        }

        writer.WriteRaw("] ");

        return Operands(phase).Operator("d");
    }

    // ---------------------------------------------------------------- colour

    public ContentStream SetFillColor(PdfColor color) => WriteColor(color, stroking: false);

    public ContentStream SetStrokeColor(PdfColor color) => WriteColor(color, stroking: true);

    private ContentStream WriteColor(PdfColor color, bool stroking)
    {
        // Each colour space has its own operator rather than a shared one with a space argument,
        // and the stroking variant is the same operator in upper case.
        return color.Space switch
        {
            PdfColorSpace.DeviceGray =>
                Operands(color.Component0).Operator(stroking ? "G" : "g"),

            PdfColorSpace.DeviceRgb =>
                Operands(color.Component0, color.Component1, color.Component2)
                    .Operator(stroking ? "RG" : "rg"),

            _ =>
                Operands(color.Component0, color.Component1, color.Component2, color.Component3)
                    .Operator(stroking ? "K" : "k")
        };
    }

    // ---------------------------------------------------------------- path construction

    public ContentStream MoveTo(double x, double y) => Operands(x, y).Operator("m");

    public ContentStream LineTo(double x, double y) => Operands(x, y).Operator("l");

    /// <summary>Appends a cubic Bezier curve with both control points given.</summary>
    public ContentStream CurveTo(
        double control1X,
        double control1Y,
        double control2X,
        double control2Y,
        double endX,
        double endY) =>
        Operands(control1X, control1Y, control2X, control2Y, endX, endY).Operator("c");

    public ContentStream ClosePath() => Operator("h");

    /// <summary>Appends a complete rectangular subpath.</summary>
    public ContentStream Rectangle(double x, double y, double width, double height) =>
        Operands(x, y, width, height).Operator("re");

    public ContentStream Rectangle(PdfRectangle rectangle) =>
        Rectangle(rectangle.Left, rectangle.Bottom, rectangle.Width, rectangle.Height);

    // ---------------------------------------------------------------- path painting

    public ContentStream Stroke() => Operator("S");

    public ContentStream CloseAndStroke() => Operator("s");

    public ContentStream Fill(PdfFillRule rule = PdfFillRule.NonZero) =>
        Operator(rule == PdfFillRule.NonZero ? "f" : "f*");

    public ContentStream FillAndStroke(PdfFillRule rule = PdfFillRule.NonZero) =>
        Operator(rule == PdfFillRule.NonZero ? "B" : "B*");

    public ContentStream CloseFillAndStroke(PdfFillRule rule = PdfFillRule.NonZero) =>
        Operator(rule == PdfFillRule.NonZero ? "b" : "b*");

    /// <summary>Ends the path without painting it, which is how a clip-only path is finished.</summary>
    public ContentStream EndPath() => Operator("n");

    /// <summary>
    /// Intersects the clipping region with the current path. Takes effect when the path is
    /// painted or ended, so this is normally followed by <see cref="EndPath"/>.
    /// </summary>
    public ContentStream Clip(PdfFillRule rule = PdfFillRule.NonZero) =>
        Operator(rule == PdfFillRule.NonZero ? "W" : "W*");

    // ---------------------------------------------------------------- text

    public ContentStream BeginText()
    {
        if (inTextObject)
        {
            throw new InvalidOperationException("Text objects cannot be nested.");
        }

        inTextObject = true;

        return Operator("BT");
    }

    public ContentStream EndText()
    {
        if (!inTextObject)
        {
            throw new InvalidOperationException("EndText without a matching BeginText.");
        }

        inTextObject = false;

        return Operator("ET");
    }

    /// <summary>Selects a font and size for subsequent text.</summary>
    public ContentStream SetFont(StandardFont font, double size)
    {
        ArgumentNullException.ThrowIfNull(font);

        if (size <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "Font size must be positive.");
        }

        writer.WriteName(page.GetFontResourceName(font));
        writer.WriteSpace();

        return Operands(size).Operator("Tf");
    }

    /// <summary>Moves to the start of the next line, offset from the current line start.</summary>
    public ContentStream SetTextPosition(double x, double y) => Operands(x, y).Operator("Td");

    /// <summary>Replaces the text matrix outright, which also resets the line start.</summary>
    public ContentStream SetTextMatrix(PdfMatrix matrix) =>
        Operands(matrix.A, matrix.B, matrix.C, matrix.D, matrix.E, matrix.F).Operator("Tm");

    /// <summary>Sets the leading, the baseline-to-baseline distance used by NextLine.</summary>
    public ContentStream SetLeading(double leading) => Operands(leading).Operator("TL");

    public ContentStream SetCharacterSpacing(double spacing) => Operands(spacing).Operator("Tc");

    public ContentStream SetWordSpacing(double spacing) => Operands(spacing).Operator("Tw");

    /// <summary>Horizontal scaling, as a percentage; 100 is unscaled.</summary>
    public ContentStream SetHorizontalScaling(double percent) => Operands(percent).Operator("Tz");

    public ContentStream SetTextRise(double rise) => Operands(rise).Operator("Ts");

    public ContentStream SetTextRenderMode(PdfTextRenderMode mode) =>
        Operands((int)mode).Operator("Tr");

    /// <summary>Moves down by the current leading to the next line.</summary>
    public ContentStream NextLine() => Operator("T*");

    /// <summary>
    /// Shows a string. The text is encoded as WinAnsi, matching the encoding declared on the
    /// standard fonts, so characters outside Latin-1 are rejected rather than silently dropped.
    /// </summary>
    public ContentStream ShowText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (!inTextObject)
        {
            throw new InvalidOperationException(
                "ShowText is only valid inside a text object; call BeginText first.");
        }

        writer.WriteString(StandardFont.Encode(text));

        // A string is self-delimiting, so the separator is not strictly required, but every
        // other operand here is followed by one and the emitted stream is far easier to read.
        writer.WriteSpace();

        return Operator("Tj");
    }

    /// <summary>
    /// Shows text with explicit inter-element adjustments, which is what justified and kerned
    /// output needs. A numeric element moves the pen left by that many thousandths of the font
    /// size, so a positive value tightens the spacing.
    /// </summary>
    public ContentStream ShowText(IEnumerable<object> elements)
    {
        ArgumentNullException.ThrowIfNull(elements);

        if (!inTextObject)
        {
            throw new InvalidOperationException(
                "ShowText is only valid inside a text object; call BeginText first.");
        }

        writer.WriteRaw("[");

        foreach (object element in elements)
        {
            switch (element)
            {
                case string text:
                    writer.WriteString(StandardFont.Encode(text));
                    break;

                case double adjustment:
                    writer.WriteReal(adjustment);
                    break;

                case int adjustment:
                    writer.WriteInteger(adjustment);
                    break;

                default:
                    throw new ArgumentException(
                        $"A text array element must be a string or a number; got {element?.GetType().Name ?? "null"}.",
                        nameof(elements));
            }
        }

        writer.WriteRaw("] ");

        return Operator("TJ");
    }

    /// <summary>
    /// Draws a single run of text with its baseline at the given point, wrapping the font
    /// selection and text object so the caller does not have to.
    /// </summary>
    public ContentStream DrawText(string text, StandardFont font, double size, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(font);

        return BeginText()
            .SetFont(font, size)
            .SetTextPosition(x, y)
            .ShowText(text)
            .EndText();
    }

    // ---------------------------------------------------------------- primitives

    /// <summary>Appends a raw operator with no operands.</summary>
    private ContentStream Operator(string name)
    {
        writer.WriteRaw(name);
        writer.WriteLine();

        return this;
    }

    /// <summary>Appends operands, space separated, ready for the operator that consumes them.</summary>
    private ContentStream Operands(params double[] values)
    {
        foreach (double value in values)
        {
            writer.WriteReal(value);
            writer.WriteSpace();
        }

        return this;
    }

    private ContentStream Operands(int value)
    {
        writer.WriteInteger(value);
        writer.WriteSpace();

        return this;
    }

    /// <summary>
    /// The accumulated operators. Checks the state stack first: an unbalanced save would leave
    /// the next page's state polluted in readers that share one stack across a document.
    /// </summary>
    internal byte[] ToArray()
    {
        if (saveDepth != 0)
        {
            throw new InvalidOperationException(
                $"This content stream has {saveDepth} unmatched Save call(s); every Save needs a Restore.");
        }

        if (inTextObject)
        {
            throw new InvalidOperationException(
                "This content stream ends inside a text object; call EndText before saving.");
        }

        return buffer.ToArray();
    }
}

public enum PdfFillRule
{
    /// <summary>The default winding rule.</summary>
    NonZero,

    /// <summary>The even-odd rule, which leaves overlapping subpaths as holes.</summary>
    EvenOdd
}

public enum PdfLineCap
{
    Butt = 0,
    Round = 1,
    Square = 2
}

public enum PdfLineJoin
{
    Miter = 0,
    Round = 1,
    Bevel = 2
}

public enum PdfTextRenderMode
{
    Fill = 0,
    Stroke = 1,
    FillThenStroke = 2,
    Invisible = 3,
    FillAndClip = 4,
    StrokeAndClip = 5,
    FillStrokeAndClip = 6,
    Clip = 7
}
