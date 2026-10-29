using Theoistic.PDF.Writing;

namespace Theoistic.PDF.Editing;

/// <summary>
/// Copies objects from one file into an update to another, giving every indirect object it
/// reaches a new number there. Fonts, font programs, images and their soft masks all come across
/// with whatever they point at, each copied once however many times it is referred to.
/// </summary>
internal sealed class ObjectImporter
{
    private readonly PdfFile source;
    private readonly IncrementalUpdate target;
    private readonly Dictionary<int, PdfReference> numbers = new Dictionary<int, PdfReference>();
    private readonly Queue<(int Source, PdfReference Target)> pending = new Queue<(int Source, PdfReference Target)>();

    public ObjectImporter(PdfFile source, IncrementalUpdate target)
    {
        this.source = source;
        this.target = target;
    }

    public PdfFile Source => source;

    /// <summary>
    /// Copies <paramref name="value"/> and everything reachable from it. Indirect objects are
    /// queued and copied one at a time rather than recursively, so a long chain of references
    /// cannot exhaust the stack.
    /// </summary>
    public PdfObject Import(PdfObject value)
    {
        ArgumentNullException.ThrowIfNull(value);

        PdfObject copy = Copy(value);

        while (pending.Count > 0)
        {
            (int number, PdfReference reference) = pending.Dequeue();
            PdfObject? original = source.GetObject(number);

            target.Set(reference, original is null ? PdfNull.Instance : Copy(original));
        }

        return copy;
    }

    private PdfObject Copy(PdfObject value) => value switch
    {
        PdfReference reference => Map(reference),
        PdfStream stream => CopyStream(stream),
        PdfDictionary dictionary => CopyEntries(dictionary, new PdfDictionary()),
        PdfArray array => new PdfArray(array.Select(Copy)),

        // Numbers, names, strings, booleans and null are never modified once made, so the same
        // instance can appear in both files.
        _ => value
    };

    private PdfObject Map(PdfReference reference)
    {
        if (numbers.TryGetValue(reference.ObjectNumber, out PdfReference? mapped))
        {
            return mapped;
        }

        // A page or page-tree node is never brought along by reference. Anything that points at
        // one - an annotation's /P, a destination - would otherwise drag in its /Parent, and from
        // there every page of the source document.
        if (source.GetObject(reference.ObjectNumber) is PdfDictionary dictionary &&
            dictionary["Type"] is PdfName { Value: "Page" or "Pages" })
        {
            return PdfNull.Instance;
        }

        PdfReference reserved = target.Reserve();

        numbers[reference.ObjectNumber] = reserved;
        pending.Enqueue((reference.ObjectNumber, reserved));

        return reserved;
    }

    private PdfStream CopyStream(PdfStream stream)
    {
        // The bytes go across still encoded, with the filter entries that describe them.
        var copy = new PdfStream(stream.Data, compress: false);

        CopyEntries(stream, copy);

        return copy;
    }

    private PdfDictionary CopyEntries(PdfDictionary from, PdfDictionary to)
    {
        foreach (KeyValuePair<string, PdfObject> entry in from)
        {
            to.Set(entry.Key, Copy(entry.Value));
        }

        return to;
    }
}
