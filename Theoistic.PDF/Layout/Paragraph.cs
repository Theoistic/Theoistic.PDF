using System.Text;
using System.Text.RegularExpressions;

namespace Theoistic.PDF.Layout;

/// <summary>A piece of a paragraph in one style.</summary>
public readonly record struct TextRun(string Text, TextStyle? Style);

/// <summary>
/// Wrapped text, possibly mixing styles - "Invoice no. <b>2026-0042</b>" is one paragraph of
/// two runs.
/// <para>
/// Lines break at spaces, and a word too long for the line is broken between characters rather
/// than allowed to overflow. A newline in the text forces a break. The paragraph splits across
/// pages line by line.
/// </para>
/// </summary>
public sealed class Paragraph : Block
{
    private static readonly Regex Tokens = new Regex(@"(\r\n|\n|\r)|([ \t]+)|([^ \t\r\n]+)", RegexOptions.Compiled);

    private readonly List<TextRun> runs = new List<TextRun>();

    private double cachedWidth = double.NaN;
    private TextStyle? cachedDefault;
    private List<Line>? cachedLines;

    public Paragraph()
    {
    }

    public Paragraph(string text, TextStyle? style = null)
    {
        Add(text, style);
    }

    /// <summary>The style for runs that do not set their own; falls back to the document's.</summary>
    public TextStyle? Style
    {
        get => style;
        set
        {
            style = value;
            cachedLines = null;
        }
    }

    private TextStyle? style;

    public TextAlign Align { get; set; }

    public IReadOnlyList<TextRun> Runs => runs;

    public Paragraph Add(string text, TextStyle? style = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        runs.Add(new TextRun(text, style));
        cachedLines = null;

        return this;
    }

    /// <summary>All the paragraph's text, for checking coverage before laying anything out.</summary>
    public string PlainText => string.Concat(runs.Select(run => run.Text));

    internal override bool CanSplit => true;

    internal override double Measure(LayoutContext context, double width) =>
        MeasureWith(context.DefaultStyle, width);

    /// <summary>Measures against an explicit fallback style - a table cell falls back to the table's, not the document's.</summary>
    internal double MeasureWith(TextStyle defaultStyle, double width) =>
        Lines(defaultStyle, width).Sum(line => line.Height);

    /// <summary>The width of the widest line when nothing wraps - what an automatic column needs.</summary>
    internal double NaturalWidth(TextStyle defaultStyle) =>
        Lines(defaultStyle, double.PositiveInfinity).Select(line => line.Width).DefaultIfEmpty(0).Max();

    internal override void Render(LayoutContext context) => RenderWith(context, context.DefaultStyle);

    internal void RenderWith(LayoutContext context, TextStyle defaultStyle)
    {
        foreach (Line line in Lines(defaultStyle, context.Width))
        {
            context.EnsureSpace(line.Height);

            double x = context.Left + (Align switch
            {
                TextAlign.Center => (context.Width - line.Width) / 2,
                TextAlign.Right => context.Width - line.Width,
                _ => 0
            });

            double baseline = context.Cursor + line.Baseline;

            foreach (Piece piece in line.Pieces)
            {
                if (piece.Text.Length > 0)
                {
                    context.DrawText(piece.Text, piece.Style, x, baseline);
                }

                x += piece.Width;
            }

            context.Cursor += line.Height;
        }
    }

    // ---------------------------------------------------------------- line breaking

    private List<Line> Lines(TextStyle defaultStyle, double width)
    {
        if (cachedLines is not null && cachedWidth.Equals(width) && ReferenceEquals(cachedDefault, defaultStyle))
        {
            return cachedLines;
        }

        TextStyle paragraphStyle = Style ?? defaultStyle;
        var lines = new List<Line>();
        var current = new List<Piece>();
        double currentWidth = 0;

        void Finish()
        {
            // Spaces a break landed after are not part of the line: counting them would push
            // right-aligned text left by a space.
            while (current.Count > 0 && current[^1].IsSpace)
            {
                currentWidth -= current[^1].Width;
                current.RemoveAt(current.Count - 1);
            }

            lines.Add(Line.From(current, currentWidth, paragraphStyle));
            current = new List<Piece>();
            currentWidth = 0;
        }

        foreach (TextRun run in runs)
        {
            TextStyle style = run.Style ?? paragraphStyle;

            foreach (Match token in Tokens.Matches(run.Text))
            {
                if (token.Groups[1].Success)
                {
                    Finish();
                    continue;
                }

                if (token.Groups[2].Success)
                {
                    // A line never starts with the space it wrapped at.
                    if (current.Count > 0)
                    {
                        var space = Piece.Measure(" ", style, isSpace: true);
                        current.Add(space);
                        currentWidth += space.Width;
                    }

                    continue;
                }

                var word = Piece.Measure(token.Value, style, isSpace: false);

                if (currentWidth + word.Width <= width + 0.001)
                {
                    current.Add(word);
                    currentWidth += word.Width;
                    continue;
                }

                if (current.Any(piece => !piece.IsSpace))
                {
                    Finish();
                }
                else
                {
                    current.Clear();
                    currentWidth = 0;
                }

                if (word.Width <= width)
                {
                    current.Add(word);
                    currentWidth += word.Width;
                    continue;
                }

                // Wider than a whole line: break it between characters, as a browser does
                // with an unbroken URL or account number.
                foreach (Piece chunk in BreakWord(token.Value, style, width))
                {
                    if (current.Count > 0)
                    {
                        Finish();
                    }

                    current.Add(chunk);
                    currentWidth = chunk.Width;
                }
            }
        }

        if (current.Count > 0 || lines.Count == 0)
        {
            Finish();
        }

        cachedWidth = width;
        cachedDefault = defaultStyle;
        cachedLines = lines;

        return lines;
    }

    private static IEnumerable<Piece> BreakWord(string word, TextStyle style, double width)
    {
        var chunk = new StringBuilder();

        foreach (Rune rune in word.EnumerateRunes())
        {
            string candidate = chunk.ToString() + rune;

            if (chunk.Length > 0 && style.Font.GetTextWidth(candidate, style.Size) > width)
            {
                yield return Piece.Measure(chunk.ToString(), style, isSpace: false);
                chunk.Clear();
            }

            chunk.Append(rune.ToString());
        }

        if (chunk.Length > 0)
        {
            yield return Piece.Measure(chunk.ToString(), style, isSpace: false);
        }
    }

    private readonly record struct Piece(string Text, TextStyle Style, double Width, bool IsSpace)
    {
        public static Piece Measure(string text, TextStyle style, bool isSpace) =>
            new Piece(text, style, style.Font.GetTextWidth(text, style.Size), isSpace);
    }

    private sealed record Line(IReadOnlyList<Piece> Pieces, double Width, double Height, double Baseline)
    {
        public static Line From(List<Piece> pieces, double width, TextStyle fallback)
        {
            // Adjacent pieces in one style are drawn as one string, which keeps the content
            // stream small and lets the reader see whole words when text is extracted.
            var merged = new List<Piece>();

            foreach (Piece piece in pieces)
            {
                if (merged.Count > 0 && ReferenceEquals(merged[^1].Style, piece.Style))
                {
                    Piece previous = merged[^1];
                    merged[^1] = previous with { Text = previous.Text + piece.Text, Width = previous.Width + piece.Width };
                }
                else
                {
                    merged.Add(piece);
                }
            }

            IEnumerable<TextStyle> styles = merged.Count > 0 ? merged.Select(p => p.Style) : new[] { fallback };

            double height = styles.Max(style => style.LineHeightPoints);
            double ascent = styles.Max(style => style.Font.Ascent * style.Size / 1000);
            double descent = styles.Max(style => -style.Font.Descent * style.Size / 1000);

            // The leading is split evenly above and below the glyphs, as CSS does.
            double baseline = ((height - (ascent + descent)) / 2) + ascent;

            return new Line(merged, width, height, baseline);
        }
    }
}
