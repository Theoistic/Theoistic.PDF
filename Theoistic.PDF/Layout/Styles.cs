using Theoistic.PDF.Writing;

namespace Theoistic.PDF.Layout;

/// <summary>
/// A set of related fonts - regular, bold and optionally italic - so a style can ask for "bold"
/// without naming a file.
/// </summary>
public sealed record PdfFontFamily(PdfFont Regular, PdfFont Bold, PdfFont? Italic = null, PdfFont? BoldItalic = null)
{
    public static PdfFontFamily Helvetica { get; } = new PdfFontFamily(
        StandardFont.Helvetica, StandardFont.HelveticaBold, StandardFont.HelveticaOblique, StandardFont.HelveticaBoldOblique);

    public static PdfFontFamily Times { get; } = new PdfFontFamily(
        StandardFont.TimesRoman, StandardFont.TimesBold, StandardFont.TimesItalic, StandardFont.TimesBoldItalic);

    public static PdfFontFamily Courier { get; } = new PdfFontFamily(
        StandardFont.Courier, StandardFont.CourierBold, StandardFont.CourierOblique, StandardFont.CourierBoldOblique);

    /// <summary>
    /// Loads a family from TrueType files. This is what to use when text may contain characters
    /// outside WinAnsi - names and addresses in most of Central and Eastern Europe, Turkey,
    /// Greece and anywhere using Cyrillic or CJK.
    /// </summary>
    public static PdfFontFamily FromFiles(string regular, string bold, string? italic = null, string? boldItalic = null) =>
        new PdfFontFamily(
            TrueTypeFont.Load(regular),
            TrueTypeFont.Load(bold),
            italic is null ? null : TrueTypeFont.Load(italic),
            boldItalic is null ? null : TrueTypeFont.Load(boldItalic));

    /// <summary>
    /// Picks the face for a weight and slope. A missing italic falls back to the upright face of
    /// the same weight rather than failing, since emphasis is cosmetic and a missing file is not.
    /// </summary>
    public PdfFont Resolve(bool bold, bool italic) => (bold, italic) switch
    {
        (false, false) => Regular,
        (true, false) => Bold,
        (false, true) => Italic ?? Regular,
        (true, true) => BoldItalic ?? Bold
    };

    /// <summary>Every distinct font in the family, for checking text coverage up front.</summary>
    public IEnumerable<PdfFont> Fonts => new[] { Regular, Bold, Italic, BoldItalic }
        .Where(font => font is not null)
        .Distinct()!;
}

/// <summary>How a run of text looks. Immutable, so variations are made with <c>with</c>.</summary>
public sealed record TextStyle
{
    public PdfFontFamily Family { get; init; } = PdfFontFamily.Helvetica;

    /// <summary>Size in points.</summary>
    public double Size { get; init; } = 10;

    public bool Bold { get; init; }

    public bool Italic { get; init; }

    public PdfColor Color { get; init; } = PdfColor.Black;

    /// <summary>Baseline-to-baseline distance as a multiple of the size.</summary>
    public double LineHeight { get; init; } = 1.3;

    internal PdfFont Font => Family.Resolve(Bold, Italic);

    internal double LineHeightPoints => Size * LineHeight;
}

public enum TextAlign
{
    Left,
    Center,
    Right
}

/// <summary>Distances on four sides, in points: margins and padding.</summary>
public readonly record struct Thickness(double Top, double Right, double Bottom, double Left)
{
    public Thickness(double all)
        : this(all, all, all, all)
    {
    }

    public Thickness(double vertical, double horizontal)
        : this(vertical, horizontal, vertical, horizontal)
    {
    }

    public double Horizontal => Left + Right;

    public double Vertical => Top + Bottom;

    /// <summary>Builds margins from millimetres, the unit page margins are usually specified in.</summary>
    public static Thickness Millimetres(double top, double right, double bottom, double left) =>
        new Thickness(top * 72 / 25.4, right * 72 / 25.4, bottom * 72 / 25.4, left * 72 / 25.4);
}

/// <summary>How wide a table or layout column is.</summary>
public readonly record struct ColumnWidth
{
    private ColumnWidth(ColumnWidthKind kind, double value)
    {
        Kind = kind;
        Value = value;
    }

    internal ColumnWidthKind Kind { get; }

    internal double Value { get; }

    /// <summary>An exact width in points.</summary>
    public static ColumnWidth Fixed(double points) =>
        points > 0 ? new ColumnWidth(ColumnWidthKind.Fixed, points) : throw new ArgumentOutOfRangeException(nameof(points));

    /// <summary>A share of whatever space the fixed and automatic columns leave.</summary>
    public static ColumnWidth Relative(double weight = 1) =>
        weight > 0 ? new ColumnWidth(ColumnWidthKind.Relative, weight) : throw new ArgumentOutOfRangeException(nameof(weight));

    /// <summary>As wide as the widest content in the column without wrapping - right for numbers.</summary>
    public static ColumnWidth Auto { get; } = new ColumnWidth(ColumnWidthKind.Auto, 0);
}

internal enum ColumnWidthKind
{
    Fixed,
    Relative,
    Auto
}

/// <summary>The look of a table.</summary>
public sealed record TableStyle
{
    /// <summary>Header text style; defaults to the body style in bold.</summary>
    public TextStyle? HeaderText { get; init; }

    /// <summary>Body text style; defaults to the document's style.</summary>
    public TextStyle? BodyText { get; init; }

    public PdfColor? HeaderBackground { get; init; }

    /// <summary>Fills every other body row, which keeps a wide table readable.</summary>
    public PdfColor? AlternateRowBackground { get; init; }

    /// <summary>Colour of the rules between rows; null for none.</summary>
    public PdfColor? RuleColor { get; init; } = PdfColor.Gray(0.82);

    public double RuleWidth { get; init; } = 0.5;

    /// <summary>A heavier rule under the header row; null to use the ordinary rule.</summary>
    public PdfColor? HeaderRuleColor { get; init; }

    public Thickness CellPadding { get; init; } = new Thickness(4, 6);

    public bool ShowHeader { get; init; } = true;

    /// <summary>Whether the header is drawn again at the top of each page the table continues onto.</summary>
    public bool RepeatHeader { get; init; } = true;
}
