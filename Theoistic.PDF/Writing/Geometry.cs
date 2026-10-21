using System.Globalization;

namespace Theoistic.PDF.Writing;

/// <summary>
/// A point in PDF user space, measured in points (1/72 inch).
/// <para>
/// PDF user space puts the origin at the <em>bottom left</em> of the page with y increasing
/// upwards, which is the opposite of nearly every screen coordinate system. This writer stays
/// faithful to the format and does not flip anything; a layout layer built on top is the right
/// place to expose top-left coordinates.
/// </para>
/// </summary>
public readonly record struct PdfPoint(double X, double Y)
{
    public static PdfPoint Origin => new PdfPoint(0, 0);

    public PdfPoint Translate(double dx, double dy) => new PdfPoint(X + dx, Y + dy);
}

/// <summary>
/// An axis-aligned rectangle in PDF user space, stored as the pair of corners that the format
/// itself uses for boxes such as /MediaBox.
/// </summary>
public readonly record struct PdfRectangle
{
    public PdfRectangle(double left, double bottom, double right, double top)
    {
        // PDF consumers are entitled to assume a normalised box, and Acrobat silently repairs
        // inverted ones rather than reporting them, so normalising here keeps the output
        // predictable instead of viewer-dependent.
        Left = Math.Min(left, right);
        Bottom = Math.Min(bottom, top);
        Right = Math.Max(left, right);
        Top = Math.Max(bottom, top);
    }

    public double Left { get; }

    public double Bottom { get; }

    public double Right { get; }

    public double Top { get; }

    public double Width => Right - Left;

    public double Height => Top - Bottom;

    /// <summary>Builds a rectangle from its lower-left corner and a size.</summary>
    public static PdfRectangle FromSize(double left, double bottom, double width, double height) =>
        new PdfRectangle(left, bottom, left + width, bottom + height);

    /// <summary>A page-sized box anchored at the origin, which is what /MediaBox normally is.</summary>
    public static PdfRectangle Page(double width, double height) =>
        new PdfRectangle(0, 0, width, height);
}

/// <summary>
/// A 2D affine transform in the six-element form PDF uses for the <c>cm</c> and <c>Tm</c>
/// operators, representing the matrix
/// <c>[a b 0; c d 0; e f 1]</c>.
/// </summary>
public readonly record struct PdfMatrix(double A, double B, double C, double D, double E, double F)
{
    public static PdfMatrix Identity => new PdfMatrix(1, 0, 0, 1, 0, 0);

    public static PdfMatrix Translation(double dx, double dy) => new PdfMatrix(1, 0, 0, 1, dx, dy);

    public static PdfMatrix Scaling(double sx, double sy) => new PdfMatrix(sx, 0, 0, sy, 0, 0);

    /// <summary>Rotation about the origin, counter-clockwise, in degrees.</summary>
    public static PdfMatrix Rotation(double degrees)
    {
        double radians = degrees * Math.PI / 180.0;
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);

        return new PdfMatrix(cos, sin, -sin, cos, 0, 0);
    }

    /// <summary>
    /// Returns the product of this matrix with <paramref name="other"/>.
    /// <para>
    /// PDF uses row vectors, so a point is mapped through <c>this</c> first and through
    /// <paramref name="other"/> second. That is the opposite order to the column-vector
    /// convention most maths references use.
    /// </para>
    /// </summary>
    public PdfMatrix Multiply(PdfMatrix other) =>
        new PdfMatrix(
            (A * other.A) + (B * other.C),
            (A * other.B) + (B * other.D),
            (C * other.A) + (D * other.C),
            (C * other.B) + (D * other.D),
            (E * other.A) + (F * other.C) + other.E,
            (E * other.B) + (F * other.D) + other.F);

    public PdfPoint Transform(PdfPoint point) =>
        new PdfPoint(
            (A * point.X) + (C * point.Y) + E,
            (B * point.X) + (D * point.Y) + F);

    public bool IsIdentity => this == Identity;
}

/// <summary>
/// A device-independent colour. PDF has no single colour type: the operator that carries it
/// depends on the colour space, so the space is part of the value here.
/// </summary>
public readonly record struct PdfColor
{
    private PdfColor(PdfColorSpace space, double c0, double c1, double c2, double c3)
    {
        Space = space;
        Component0 = c0;
        Component1 = c1;
        Component2 = c2;
        Component3 = c3;
    }

    public PdfColorSpace Space { get; }

    public double Component0 { get; }

    public double Component1 { get; }

    public double Component2 { get; }

    public double Component3 { get; }

    public static PdfColor Gray(double level) =>
        new PdfColor(PdfColorSpace.DeviceGray, Clamp(level), 0, 0, 0);

    public static PdfColor Rgb(double red, double green, double blue) =>
        new PdfColor(PdfColorSpace.DeviceRgb, Clamp(red), Clamp(green), Clamp(blue), 0);

    public static PdfColor Cmyk(double cyan, double magenta, double yellow, double black) =>
        new PdfColor(PdfColorSpace.DeviceCmyk, Clamp(cyan), Clamp(magenta), Clamp(yellow), Clamp(black));

    /// <summary>
    /// Parses the CSS-style hex forms <c>#rgb</c>, <c>#rrggbb</c> and their unprefixed
    /// equivalents into a DeviceRGB colour.
    /// </summary>
    public static PdfColor FromHex(string hex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hex);

        ReadOnlySpan<char> digits = hex.AsSpan().Trim();

        if (digits.Length > 0 && digits[0] == '#')
        {
            digits = digits[1..];
        }

        if (digits.Length == 3)
        {
            // #abc is shorthand for #aabbcc: each digit is duplicated rather than shifted, so
            // that 'f' maps to 255 and not 240.
            return Rgb(
                Nibble(digits[0]) * 17 / 255.0,
                Nibble(digits[1]) * 17 / 255.0,
                Nibble(digits[2]) * 17 / 255.0);
        }

        if (digits.Length == 6)
        {
            return Rgb(
                Byte(digits[0], digits[1]) / 255.0,
                Byte(digits[2], digits[3]) / 255.0,
                Byte(digits[4], digits[5]) / 255.0);
        }

        throw new FormatException(
            $"'{hex}' is not a recognised colour. Expected #rgb or #rrggbb.");

        static int Nibble(char c) => c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => throw new FormatException($"'{c}' is not a hexadecimal digit.")
        };

        static int Byte(char high, char low) => (Nibble(high) << 4) | Nibble(low);
    }

    public static PdfColor Black => Gray(0);

    public static PdfColor White => Gray(1);

    private static double Clamp(double value) => Math.Clamp(value, 0.0, 1.0);

    public override string ToString() => Space switch
    {
        PdfColorSpace.DeviceGray => string.Create(CultureInfo.InvariantCulture, $"gray({Component0:0.###})"),
        PdfColorSpace.DeviceRgb => string.Create(CultureInfo.InvariantCulture, $"rgb({Component0:0.###}, {Component1:0.###}, {Component2:0.###})"),
        _ => string.Create(CultureInfo.InvariantCulture, $"cmyk({Component0:0.###}, {Component1:0.###}, {Component2:0.###}, {Component3:0.###})")
    };
}

public enum PdfColorSpace
{
    DeviceGray,
    DeviceRgb,
    DeviceCmyk
}
