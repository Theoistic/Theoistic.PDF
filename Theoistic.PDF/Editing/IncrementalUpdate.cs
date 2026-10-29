using System.Globalization;
using System.Security.Cryptography;
using Theoistic.PDF.Writing;

namespace Theoistic.PDF.Editing;

/// <summary>
/// One incremental update (ISO 32000-1 section 7.5.6): new and replaced objects, a cross-reference
/// section for them, and a trailer pointing back at the file's previous section, all appended
/// after the original bytes, which are left exactly as they were.
/// <para>
/// That is what lets a signature made earlier keep verifying: its byte range still describes a
/// prefix of the file, and that prefix has not changed.
/// </para>
/// </summary>
internal sealed class IncrementalUpdate
{
    private readonly PdfFile file;

    // Sorted so that objects are written in number order, which keeps the cross-reference
    // section to as few subsections as possible and the output reproducible.
    private readonly SortedDictionary<int, (int Generation, PdfObject? Value)> objects =
        new SortedDictionary<int, (int Generation, PdfObject? Value)>();

    private int nextNumber;

    public IncrementalUpdate(PdfFile file)
    {
        this.file = file;
        nextNumber = file.Size;
    }

    public PdfFile File => file;

    public bool IsEmpty => objects.Count == 0;

    /// <summary>Adds a new object, numbered after everything already in the file.</summary>
    public PdfReference Add(PdfObject value)
    {
        ArgumentNullException.ThrowIfNull(value);

        PdfReference reference = Reserve();

        objects[reference.ObjectNumber] = (0, value);

        return reference;
    }

    /// <summary>
    /// Takes a number for an object that will be supplied later with <see cref="Set"/>, which an
    /// object graph with cycles needs: a font can only be written once its descendants have numbers.
    /// </summary>
    public PdfReference Reserve()
    {
        var reference = new PdfReference(nextNumber++);

        objects[reference.ObjectNumber] = (0, null);

        return reference;
    }

    public void Set(PdfReference reference, PdfObject value)
    {
        ArgumentNullException.ThrowIfNull(value);

        objects[reference.ObjectNumber] = (reference.Generation, value);
    }

    /// <summary>
    /// Writes a new version of an existing object. It keeps its number and generation, so every
    /// reference to it anywhere in the file now reaches the new version; the old one stays in the
    /// bytes, untouched, for anything that needs the earlier revision.
    /// </summary>
    public void Replace(PdfReference reference, PdfObject value)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(value);

        objects[reference.ObjectNumber] = (file.GenerationOf(reference.ObjectNumber), value);
    }

    /// <summary>The original bytes followed by this update.</summary>
    public byte[] Write()
    {
        byte[] original = file.Data;

        using var output = new MemoryStream(original.Length + 4096);

        // The writer counts from its own start, and the original goes through it first, so every
        // position it reports is an offset in the finished file.
        var writer = new PdfWriter(output);

        writer.WriteBytes(original);

        // The update has to start on a line of its own. Most files already end with an EOL after
        // %%EOF; one that does not gets one added, after its last byte.
        if (original.Length == 0 || (original[^1] != (byte)'\n' && original[^1] != (byte)'\r'))
        {
            writer.WriteLine();
        }

        long bodyStart = writer.Position;
        var offsets = new SortedDictionary<int, (long Offset, int Generation)>();

        foreach (KeyValuePair<int, (int Generation, PdfObject? Value)> entry in objects)
        {
            PdfObject value = entry.Value.Value ?? throw new InvalidOperationException(
                $"Object {entry.Key} was reserved but never written, which should be impossible.");

            offsets[entry.Key] = (writer.Position, entry.Value.Generation);

            writer.WriteRaw(entry.Key.ToString(CultureInfo.InvariantCulture));
            writer.WriteSpace();
            writer.WriteInteger(entry.Value.Generation);
            writer.WriteRaw(" obj");
            writer.WriteLine();
            writer.WriteObject(value);
            writer.WriteLine();
            writer.WriteLine("endobj");
        }

        PdfArray identifier = NewIdentifier(output.GetBuffer().AsSpan((int)bodyStart, (int)(writer.Position - bodyStart)));

        // The update's cross-reference section has to be the same kind as the one before it: a
        // reader that understands only tables cannot follow /Prev into a stream, and a file that
        // already uses streams may hold objects a table cannot describe.
        if (file.LastSectionIsStream)
        {
            WriteCrossReferenceStream(writer, offsets, identifier);
        }
        else
        {
            WriteCrossReferenceTable(writer, offsets, identifier);
        }

        return output.ToArray();
    }

    private void WriteCrossReferenceTable(
        PdfWriter writer,
        SortedDictionary<int, (long Offset, int Generation)> offsets,
        PdfArray identifier)
    {
        long sectionOffset = writer.Position;
        SortedDictionary<int, XrefEntry> section = SectionEntries(offsets);

        writer.WriteLine("xref");

        foreach ((int first, List<XrefEntry> run) in Runs(section))
        {
            writer.WriteLine($"{first.ToString(CultureInfo.InvariantCulture)} {run.Count.ToString(CultureInfo.InvariantCulture)}");

            foreach (XrefEntry entry in run)
            {
                // Twenty bytes exactly, with a space and LF as the two-byte terminator.
                string offset = entry.Offset.ToString(CultureInfo.InvariantCulture).PadLeft(10, '0');
                string generation = entry.Generation.ToString(CultureInfo.InvariantCulture).PadLeft(5, '0');

                writer.WriteRaw($"{offset} {generation} {(entry.Kind == XrefKind.InUse ? 'n' : 'f')} \n");
            }
        }

        writer.WriteLine("trailer");
        writer.WriteObject(TrailerDictionary(new PdfDictionary(), nextNumber, identifier));
        writer.WriteLine();
        WriteStartXref(writer, sectionOffset);
    }

    private void WriteCrossReferenceStream(
        PdfWriter writer,
        SortedDictionary<int, (long Offset, int Generation)> offsets,
        PdfArray identifier)
    {
        // The stream lists itself, so it takes the next number and its own offset is simply the
        // position it is about to be written at.
        int streamNumber = nextNumber;
        long sectionOffset = writer.Position;

        var withSelf = new SortedDictionary<int, (long Offset, int Generation)>(offsets)
        {
            [streamNumber] = (sectionOffset, 0)
        };

        SortedDictionary<int, XrefEntry> section = SectionEntries(withSelf);

        long largest = section.Values.Max(entry => entry.Offset);
        int offsetWidth = Math.Max(1, ByteWidth(largest));
        int generationWidth = Math.Max(1, ByteWidth(section.Values.Max(entry => (long)entry.Generation)));
        int rowLength = 1 + offsetWidth + generationWidth;

        var index = new PdfArray();
        var rows = new List<byte>();

        foreach ((int first, List<XrefEntry> run) in Runs(section))
        {
            index.Add(new PdfInteger(first)).Add(new PdfInteger(run.Count));

            foreach (XrefEntry entry in run)
            {
                byte[] row = new byte[rowLength];

                row[0] = entry.Kind switch
                {
                    XrefKind.Free => 0,
                    XrefKind.InUse => 1,
                    _ => 2
                };

                PutBigEndian(row, 1, offsetWidth, entry.Offset);
                PutBigEndian(row, 1 + offsetWidth, generationWidth, entry.Generation);
                rows.AddRange(row);
            }
        }

        var stream = new PdfStream(rows.ToArray());

        stream.Set("Type", new PdfName("XRef"));
        TrailerDictionary(stream, streamNumber + 1, identifier);
        stream.Set("Index", index);
        stream.Set("W", new PdfArray(new PdfObject[] { new PdfInteger(1), new PdfInteger(offsetWidth), new PdfInteger(generationWidth) }));

        writer.WriteRaw(streamNumber.ToString(CultureInfo.InvariantCulture));
        writer.WriteRaw(" 0 obj");
        writer.WriteLine();
        writer.WriteObject(stream);
        writer.WriteLine();
        writer.WriteLine("endobj");

        WriteStartXref(writer, sectionOffset);
    }

    /// <summary>
    /// The entries this section lists: the update's own objects, and - when the file's cross-
    /// reference information had to be rebuilt by scanning - every other object as well, because
    /// chaining back to sections that were broken would leave the result just as broken.
    /// </summary>
    private SortedDictionary<int, XrefEntry> SectionEntries(SortedDictionary<int, (long Offset, int Generation)> offsets)
    {
        var section = new SortedDictionary<int, XrefEntry>();

        if (file.Reconstructed)
        {
            section[0] = new XrefEntry(XrefKind.Free, 0, 65535);

            foreach (KeyValuePair<int, XrefEntry> entry in file.Entries)
            {
                if (entry.Key > 0 && entry.Value.Kind != XrefKind.Free)
                {
                    section[entry.Key] = entry.Value;
                }
            }
        }

        foreach (KeyValuePair<int, (long Offset, int Generation)> entry in offsets)
        {
            section[entry.Key] = new XrefEntry(XrefKind.InUse, entry.Value.Offset, entry.Value.Generation);
        }

        return section;
    }

    private PdfDictionary TrailerDictionary(PdfDictionary trailer, int size, PdfArray identifier)
    {
        trailer.Set("Size", size);
        trailer.Set("Root", file.RootReference);

        if (file.Trailer["Info"] is PdfReference info)
        {
            trailer.Set("Info", info);
        }

        trailer.Set("ID", identifier);

        if (!file.Reconstructed && file.StartXref >= 0)
        {
            trailer.Set("Prev", file.StartXref);
        }

        return trailer;
    }

    /// <summary>
    /// The file identifier: the first element is the document's permanent identity and is kept;
    /// the second identifies this revision and is replaced (14.4). It is derived from the update's
    /// own bytes rather than drawn at random, so the same update always produces the same file.
    /// </summary>
    private PdfArray NewIdentifier(ReadOnlySpan<byte> body)
    {
        Span<byte> hash = stackalloc byte[32];

        SHA256.HashData(body, hash);

        var revision = new PdfString(hash[..16].ToArray(), hexadecimal: true);
        PdfString permanent = file.Resolve(file.Trailer["ID"]) is PdfArray { Count: > 0 } existing && file.Resolve(existing[0]) is PdfString first
            ? new PdfString(first.Bytes, hexadecimal: true)
            : revision;

        return new PdfArray(new PdfObject[] { permanent, revision });
    }

    private static void WriteStartXref(PdfWriter writer, long sectionOffset)
    {
        writer.WriteLine("startxref");
        writer.WriteLine(sectionOffset.ToString(CultureInfo.InvariantCulture));
        writer.WriteRaw("%%EOF");
        writer.WriteLine();
    }

    /// <summary>Groups consecutive object numbers into the subsections a section is made of.</summary>
    private static IEnumerable<(int First, List<XrefEntry> Run)> Runs(SortedDictionary<int, XrefEntry> section)
    {
        int first = -1;
        int previous = -2;
        var run = new List<XrefEntry>();

        foreach (KeyValuePair<int, XrefEntry> entry in section)
        {
            if (entry.Key != previous + 1 && run.Count > 0)
            {
                yield return (first, run);
                run = new List<XrefEntry>();
            }

            if (run.Count == 0)
            {
                first = entry.Key;
            }

            run.Add(entry.Value);
            previous = entry.Key;
        }

        if (run.Count > 0)
        {
            yield return (first, run);
        }
    }

    private static int ByteWidth(long value)
    {
        int width = 0;

        while (value > 0)
        {
            width++;
            value >>= 8;
        }

        return width;
    }

    private static void PutBigEndian(byte[] target, int offset, int width, long value)
    {
        for (int i = width - 1; i >= 0; i--)
        {
            target[offset + i] = (byte)(value & 0xFF);
            value >>= 8;
        }
    }
}

/// <summary>
/// A fixed number of bytes written verbatim, whose position in the output is recorded so they
/// can be overwritten once the file is complete. A signature needs this twice: the space its
/// value will fill, and the byte range that describes everything around that space.
/// </summary>
internal sealed class PdfPlaceholder : PdfObject
{
    private readonly byte[] bytes;

    public PdfPlaceholder(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        this.bytes = bytes;
    }

    /// <summary>Where the placeholder starts in the output; -1 until it has been written.</summary>
    public long Offset { get; private set; } = -1;

    public int Length => bytes.Length;

    internal override void Write(PdfWriter writer)
    {
        Offset = writer.Position;
        writer.WriteBytes(bytes);
    }
}
