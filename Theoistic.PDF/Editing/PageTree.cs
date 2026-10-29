using Theoistic.PDF.Writing;

namespace Theoistic.PDF.Editing;

/// <summary>
/// A leaf of the page tree with the attributes it inherits already applied, which is what a
/// viewer works from. <see cref="Resources"/> is kept as found - a reference or a dictionary - so
/// it can be copied without being resolved.
/// </summary>
internal sealed record PdfPageNode(
    PdfReference? Reference,
    PdfDictionary Dictionary,
    PdfRectangle MediaBox,
    PdfRectangle CropBox,
    int Rotate,
    PdfObject? Resources)
{
    /// <summary>Width as displayed: the crop box, turned by the rotation.</summary>
    public double DisplayWidth => Rotate is 90 or 270 ? CropBox.Height : CropBox.Width;

    public double DisplayHeight => Rotate is 90 or 270 ? CropBox.Width : CropBox.Height;

    /// <summary>
    /// The transform from displayed coordinates - origin at the bottom left of the page as the
    /// viewer shows it - to the page's own user space.
    /// <para>
    /// /Rotate turns the page clockwise for display, so the displayed origin is the corner of the
    /// crop box that the rotation brings to the bottom left: the bottom-right corner for 90, the
    /// top-right for 180, the top-left for 270.
    /// </para>
    /// </summary>
    public PdfMatrix DisplayToUser => Rotate switch
    {
        90 => new PdfMatrix(0, 1, -1, 0, CropBox.Right, CropBox.Bottom),
        180 => new PdfMatrix(-1, 0, 0, -1, CropBox.Right, CropBox.Top),
        270 => new PdfMatrix(0, -1, 1, 0, CropBox.Left, CropBox.Top),
        _ => new PdfMatrix(1, 0, 0, 1, CropBox.Left, CropBox.Bottom)
    };
}

internal static class PageTree
{
    // What a viewer assumes for a page with no /MediaBox anywhere above it: US Letter.
    private static readonly PdfRectangle DefaultMediaBox = PdfRectangle.Page(612, 792);

    /// <summary>Every page in document order.</summary>
    public static List<PdfPageNode> Read(PdfFile file)
    {
        PdfDictionary catalog = file.Catalog;
        var pages = new List<PdfPageNode>();
        var visited = new HashSet<int>();

        // An explicit stack rather than recursion: the depth of the tree is the file's choice, and
        // a crafted one should not be able to overflow the stack.
        var stack = new Stack<(PdfObject Node, Inherited State)>();

        if (catalog["Pages"] is PdfObject root)
        {
            stack.Push((root, default));
        }

        while (stack.Count > 0)
        {
            (PdfObject node, Inherited inherited) = stack.Pop();
            PdfReference? reference = node as PdfReference;

            // A node seen twice is a cycle or a page listed twice; either way it is only taken once.
            if (reference is not null && !visited.Add(reference.ObjectNumber))
            {
                continue;
            }

            if (file.Resolve(node) is not PdfDictionary dictionary)
            {
                continue;
            }

            Inherited state = inherited.Apply(dictionary, file);
            bool isLeaf = dictionary["Type"] is PdfName { Value: "Page" } || dictionary["Kids"] is null;

            if (!isLeaf)
            {
                if (file.Resolve(dictionary["Kids"]) is PdfArray kids)
                {
                    for (int i = kids.Count - 1; i >= 0; i--)
                    {
                        stack.Push((kids[i], state));
                    }
                }

                continue;
            }

            PdfRectangle media = state.MediaBox ?? DefaultMediaBox;
            PdfRectangle crop = Intersect(state.CropBox ?? media, media) ?? media;

            pages.Add(new PdfPageNode(reference, dictionary, media, crop, state.Rotate, state.Resources));
        }

        return pages;
    }

    /// <summary>Reads a rectangle, resolving the array and each number in it, or null if it is not one.</summary>
    public static PdfRectangle? ReadRectangle(PdfObject? value, PdfFile file)
    {
        if (file.Resolve(value) is not PdfArray array || array.Count < 4)
        {
            return null;
        }

        var numbers = new double[4];

        for (int i = 0; i < 4; i++)
        {
            if (Number(file.Resolve(array[i])) is not double number)
            {
                return null;
            }

            numbers[i] = number;
        }

        var rectangle = new PdfRectangle(numbers[0], numbers[1], numbers[2], numbers[3]);

        return rectangle.Width > 0 && rectangle.Height > 0 ? rectangle : null;
    }

    public static double? Number(PdfObject? value) => value switch
    {
        PdfInteger integer => integer.Value,
        PdfReal real => real.Value,
        _ => null
    };

    /// <summary>
    /// /Rotate as one of 0, 90, 180 or 270. The spec only allows multiples of 90, but negative and
    /// over-360 values are common, and a stray value is rounded to the nearest quarter turn.
    /// </summary>
    public static int NormaliseRotation(double degrees)
    {
        double quarterTurns = Math.Round(degrees / 90.0);
        int normalised = (int)(((quarterTurns % 4) + 4) % 4);

        return normalised * 90;
    }

    /// <summary>The crop box is clipped to the media box (14.11.2); null when they do not overlap.</summary>
    private static PdfRectangle? Intersect(PdfRectangle a, PdfRectangle b)
    {
        double left = Math.Max(a.Left, b.Left);
        double bottom = Math.Max(a.Bottom, b.Bottom);
        double right = Math.Min(a.Right, b.Right);
        double top = Math.Min(a.Top, b.Top);

        return right > left && top > bottom ? new PdfRectangle(left, bottom, right, top) : null;
    }

    /// <summary>The attributes a page inherits from its ancestors in the tree (table 30).</summary>
    private readonly record struct Inherited(PdfRectangle? MediaBox, PdfRectangle? CropBox, int Rotate, PdfObject? Resources)
    {
        public Inherited Apply(PdfDictionary node, PdfFile file)
        {
            Inherited result = this;

            if (ReadRectangle(node["MediaBox"], file) is PdfRectangle media)
            {
                result = result with { MediaBox = media };
            }

            if (ReadRectangle(node["CropBox"], file) is PdfRectangle crop)
            {
                result = result with { CropBox = crop };
            }

            if (Number(file.Resolve(node["Rotate"])) is double rotate)
            {
                result = result with { Rotate = NormaliseRotation(rotate) };
            }

            if (node["Resources"] is PdfObject resources && file.Resolve(resources) is PdfDictionary)
            {
                result = result with { Resources = resources };
            }

            return result;
        }
    }
}
