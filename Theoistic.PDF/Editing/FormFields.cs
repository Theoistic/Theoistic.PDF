using Theoistic.PDF.Writing;

namespace Theoistic.PDF.Editing;

/// <summary>
/// The interactive form's fields (12.7.3): every node of the field tree with its fully qualified
/// name and its field type, which a kid inherits from its parent when it has none of its own.
/// </summary>
internal static class FormFields
{
    public static List<FormField> Read(PdfFile file)
    {
        var result = new List<FormField>();

        if (file.Resolve(file.Catalog["AcroForm"]) is not PdfDictionary form ||
            file.Resolve(form["Fields"]) is not PdfArray fields)
        {
            return result;
        }

        var visited = new HashSet<int>();
        var stack = new Stack<(PdfObject Node, string Parent, string? Type)>();

        for (int i = fields.Count - 1; i >= 0; i--)
        {
            stack.Push((fields[i], string.Empty, null));
        }

        while (stack.Count > 0)
        {
            (PdfObject node, string parent, string? inheritedType) = stack.Pop();

            if (node is PdfReference reference && !visited.Add(reference.ObjectNumber))
            {
                continue;
            }

            if (file.Resolve(node) is not PdfDictionary dictionary)
            {
                continue;
            }

            // A widget annotation merged into its field has no /T of its own and shares the
            // field's name; a field with /T adds a part to the qualified name.
            string? partial = PdfText.Decode(file.Resolve(dictionary["T"]));
            string name = partial is null ? parent : parent.Length == 0 ? partial : parent + "." + partial;
            string? type = (file.Resolve(dictionary["FT"]) as PdfName)?.Value ?? inheritedType;

            result.Add(new FormField(name, dictionary, type, partial is not null));

            if (file.Resolve(dictionary["Kids"]) is PdfArray kids)
            {
                for (int i = kids.Count - 1; i >= 0; i--)
                {
                    stack.Push((kids[i], name, type));
                }
            }
        }

        return result;
    }
}

/// <param name="FullName">The fully qualified name, parts joined by periods.</param>
/// <param name="HasOwnName">Whether this node has a /T, as opposed to being a widget of its parent.</param>
internal sealed record FormField(string FullName, PdfDictionary Dictionary, string? Type, bool HasOwnName);
