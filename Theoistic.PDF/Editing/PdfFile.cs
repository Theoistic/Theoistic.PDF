using System.Globalization;
using Theoistic.PDF.Writing;

namespace Theoistic.PDF.Editing;

/// <summary>
/// An existing PDF opened for reading: its cross-reference information, its trailer, and lazy,
/// cached access to every object by number.
/// <para>
/// Everything the format allows for locating objects is understood - classic tables, cross-
/// reference streams, hybrid files that carry both, chains of incremental updates through /Prev,
/// and objects packed into object streams. When the cross-reference information is missing or
/// wrong, the file is scanned for <c>N G obj</c> instead, which is how every viewer copes with
/// damaged files.
/// </para>
/// </summary>
internal sealed class PdfFile
{
    private readonly Dictionary<int, XrefEntry> entries = new Dictionary<int, XrefEntry>();
    private readonly Dictionary<int, PdfObject?> cache = new Dictionary<int, PdfObject?>();
    private readonly Dictionary<int, ObjectStream?> objectStreams = new Dictionary<int, ObjectStream?>();

    // Objects being loaded right now. A stream whose /Length points back at the stream itself, or
    // a pair of object streams that claim to contain each other, would otherwise recurse forever.
    private readonly HashSet<int> loading = new HashSet<int>();

    private ScanResult? scan;

    private PdfFile(byte[] data) => Data = data;

    /// <summary>The file's bytes, exactly as given.</summary>
    public byte[] Data { get; }

    /// <summary>
    /// The newest trailer: the trailer dictionary of a table, or the dictionary of a cross-reference
    /// stream, which serves the same purpose.
    /// </summary>
    public PdfDictionary Trailer { get; private set; } = new PdfDictionary();

    /// <summary>The offset the final startxref names, which an incremental update's /Prev points back to.</summary>
    public long StartXref { get; private set; } = -1;

    /// <summary>Whether the newest cross-reference section is a stream rather than a table.</summary>
    public bool LastSectionIsStream { get; private set; }

    /// <summary>
    /// True when the cross-reference information could not be used and was rebuilt by scanning.
    /// An update to such a file has to list every object itself rather than chain to the old
    /// sections with /Prev, because those sections are what was broken.
    /// </summary>
    public bool Reconstructed { get; private set; }

    /// <summary>One more than the highest object number in use: where new objects are numbered from.</summary>
    public int Size
    {
        get
        {
            int size = Trailer["Size"] is PdfInteger { Value: > 0 and < int.MaxValue } declared ? (int)declared.Value : 1;

            foreach (int number in entries.Keys)
            {
                size = Math.Max(size, number + 1);
            }

            return size;
        }
    }

    /// <summary>Every object the cross-reference information lists, for writing a complete section.</summary>
    public IReadOnlyDictionary<int, XrefEntry> Entries => entries;

    public PdfReference RootReference =>
        Trailer["Root"] as PdfReference ?? throw new PdfFormatException("The trailer has no /Root reference.");

    public PdfDictionary Catalog =>
        Resolve(Trailer["Root"]) as PdfDictionary ?? throw new PdfFormatException("The document catalog is missing.");

    /// <summary>
    /// Opens <paramref name="data"/>. Throws <see cref="PdfFormatException"/> when it is not a PDF,
    /// cannot be repaired, or is encrypted.
    /// </summary>
    public static PdfFile Open(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (data.AsSpan(0, Math.Min(data.Length, 1024)).IndexOf("%PDF-"u8) < 0)
        {
            throw new PdfFormatException("The data is not a PDF: there is no %PDF- header at its start.");
        }

        var file = new PdfFile(data);

        try
        {
            bool read = file.TryReadCrossReferences();

            // Checked before anything else is looked at: in an encrypted file every string and
            // stream is ciphertext, and reading on would only produce confusing errors.
            if (read)
            {
                file.ThrowIfEncrypted();
            }

            if (!read || !file.HasUsableCatalog())
            {
                file.Reconstruct();
                file.ThrowIfEncrypted();

                if (!file.HasUsableCatalog())
                {
                    throw new PdfFormatException(
                        "The PDF is damaged beyond repair: no document catalog with a page tree could be found.");
                }
            }
        }
        catch (Exception exception) when (exception is not PdfFormatException and not OutOfMemoryException)
        {
            throw new PdfFormatException("The PDF could not be read: " + exception.Message, exception);
        }

        return file;
    }

    // ---------------------------------------------------------------- objects

    /// <summary>
    /// Follows a reference to the object it points at; anything else is returned as it is. A
    /// reference to nothing, and the null object, both come back as null.
    /// </summary>
    public PdfObject? Resolve(PdfObject? value)
    {
        // A reference to a reference is legal if odd. The bound stops a loop of them.
        for (int i = 0; i < 32 && value is PdfReference reference; i++)
        {
            value = GetObject(reference.ObjectNumber);
        }

        return value is PdfReference or PdfNull ? null : value;
    }

    /// <summary>The object with this number, or null if the file has none.</summary>
    public PdfObject? GetObject(int number)
    {
        if (cache.TryGetValue(number, out PdfObject? cached))
        {
            return cached;
        }

        if (!loading.Add(number))
        {
            return null;
        }

        try
        {
            PdfObject? value = Load(number);

            cache[number] = value;

            return value;
        }
        finally
        {
            loading.Remove(number);
        }
    }

    /// <summary>The generation the cross-reference information gives an object; 0 when it is compressed or unknown.</summary>
    public int GenerationOf(int number) =>
        entries.TryGetValue(number, out XrefEntry entry) && entry.Kind == XrefKind.InUse ? entry.Generation : 0;

    private PdfObject? Load(int number)
    {
        if (entries.TryGetValue(number, out XrefEntry entry))
        {
            switch (entry.Kind)
            {
                case XrefKind.Free:
                    return null;

                case XrefKind.InUse when TryReadAt(entry.Offset, number, out PdfObject? value):
                    return value;

                case XrefKind.Compressed when TryReadCompressed(entry.Offset, number, out PdfObject? value):
                    return value;
            }
        }

        // Missing from the table, or the table pointed somewhere else: a table off by a few bytes
        // or numbered from 1 instead of 0 is a classic defect, and scanning finds the object anyway.
        if (Scan().Objects.TryGetValue(number, out (long Offset, int Generation) found) &&
            TryReadAt(found.Offset, number, out PdfObject? scanned))
        {
            return scanned;
        }

        return null;
    }

    private bool TryReadAt(long offset, int number, out PdfObject? value)
    {
        value = null;

        if (offset < 0 || offset >= Data.Length)
        {
            return false;
        }

        try
        {
            var parser = new PdfParser(Data, (int)offset, resolve: reference => Resolve(reference));

            PdfObject read = parser.ReadIndirectObject(out int foundNumber, out _);

            if (foundNumber != number)
            {
                return false;
            }

            value = read;

            return true;
        }
        catch (Exception exception) when (IsDamage(exception))
        {
            return false;
        }
    }

    private bool TryReadCompressed(long streamNumber, int number, out PdfObject? value)
    {
        value = null;

        if (streamNumber is < 1 or > int.MaxValue || streamNumber == number)
        {
            return false;
        }

        ObjectStream? objectStream = GetObjectStream((int)streamNumber);

        // The index the cross-reference gives is only a hint; the stream's own header says which
        // object is where.
        if (objectStream is null || !objectStream.Offsets.TryGetValue(number, out int offset))
        {
            return false;
        }

        try
        {
            int start = objectStream.First + offset;

            if (start < 0 || start >= objectStream.Data.Length)
            {
                return false;
            }

            value = new PdfParser(objectStream.Data, start).ReadObject();

            return true;
        }
        catch (Exception exception) when (IsDamage(exception))
        {
            return false;
        }
    }

    /// <summary>
    /// Whether an exception from reading one object means the object is damaged, as opposed to a
    /// fault in the reader. A damaged object is treated as missing - the same as a viewer does -
    /// so one bad object cannot make a whole document unreadable.
    /// </summary>
    private static bool IsDamage(Exception exception) =>
        exception is PdfFormatException or ArgumentException or IndexOutOfRangeException or OverflowException or InvalidDataException;

    private ObjectStream? GetObjectStream(int number)
    {
        if (objectStreams.TryGetValue(number, out ObjectStream? existing))
        {
            return existing;
        }

        ObjectStream? objectStream = null;

        try
        {
            if (GetObject(number) is PdfStream stream)
            {
                objectStream = ObjectStream.Read(stream, this);
            }
        }
        catch (Exception exception) when (IsDamage(exception))
        {
            objectStream = null;
        }

        objectStreams[number] = objectStream;

        return objectStream;
    }

    // ---------------------------------------------------------------- cross-reference sections

    private bool TryReadCrossReferences()
    {
        long startxref = FindStartXref();

        if (startxref < 0 || startxref >= Data.Length)
        {
            return false;
        }

        StartXref = startxref;

        var visited = new HashSet<long>();
        long? offset = startxref;
        bool newest = true;

        while (offset is long at && visited.Add(at))
        {
            PdfDictionary sectionTrailer;
            bool isStream;

            try
            {
                sectionTrailer = ReadSection(at, out isStream);
            }
            catch (PdfFormatException) when (!newest)
            {
                // An older section that cannot be read loses only the objects nothing newer
                // superseded, and those are still found by scanning when asked for.
                break;
            }
            catch (PdfFormatException)
            {
                return false;
            }

            if (newest)
            {
                Trailer = sectionTrailer;
                LastSectionIsStream = isStream;
                newest = false;
            }

            offset = sectionTrailer["Prev"] is PdfInteger { Value: >= 0 } previous ? previous.Value : null;
        }

        return true;
    }

    private PdfDictionary ReadSection(long at, out bool isStream)
    {
        var probe = new PdfParser(Data, (int)at);

        probe.SkipWhitespace();
        isStream = !probe.MatchesKeyword("xref");

        return isStream ? ReadStreamSection(at) : ReadTableSection(probe.Position);
    }

    private PdfDictionary ReadTableSection(int at)
    {
        var parser = new PdfParser(Data, at);

        parser.ExpectKeyword("xref");

        var section = new Dictionary<int, XrefEntry>();

        while (true)
        {
            parser.SkipWhitespace();

            if (parser.MatchesKeyword("trailer"))
            {
                break;
            }

            int first = parser.ReadUnsignedInteger();
            int count = parser.ReadUnsignedInteger();

            // Entries are read as tokens rather than as fixed 20-byte records, so the common
            // variations in their line endings do not matter.
            for (int i = 0; i < count; i++)
            {
                long offset = parser.ReadUnsignedLong();
                int generation = parser.ReadUnsignedInteger();
                string type = parser.ReadKeyword();

                section.TryAdd(first + i, type == "n"
                    ? new XrefEntry(XrefKind.InUse, offset, generation)
                    : new XrefEntry(XrefKind.Free, 0, generation));
            }
        }

        parser.ExpectKeyword("trailer");

        if (parser.ReadObject() is not PdfDictionary trailer)
        {
            throw new PdfFormatException("A trailer is not a dictionary.");
        }

        // A hybrid file keeps the objects that live in object streams out of its table, or lists
        // them as free, so that readers which predate object streams still work; the real entries
        // are in the cross-reference stream /XRefStm points at, and they win over those.
        if (trailer["XRefStm"] is PdfInteger { Value: >= 0 } streamOffset && streamOffset.Value < Data.Length)
        {
            try
            {
                foreach (KeyValuePair<int, XrefEntry> entry in ReadXrefStream(streamOffset.Value, out _))
                {
                    if (!section.TryGetValue(entry.Key, out XrefEntry existing) || existing.Kind == XrefKind.Free)
                    {
                        section[entry.Key] = entry.Value;
                    }
                }
            }
            catch (PdfFormatException)
            {
                // The table alone is still a usable, if older-reader, view of the file.
            }
        }

        Merge(section);

        return trailer;
    }

    private PdfDictionary ReadStreamSection(long at)
    {
        Dictionary<int, XrefEntry> section = ReadXrefStream(at, out PdfDictionary dictionary);

        Merge(section);

        return dictionary;
    }

    private Dictionary<int, XrefEntry> ReadXrefStream(long at, out PdfDictionary dictionary)
    {
        var parser = new PdfParser(Data, (int)at, resolve: reference => Resolve(reference));

        if (parser.ReadIndirectObject(out _, out _) is not PdfStream stream)
        {
            throw new PdfFormatException("startxref does not point at a cross-reference table or stream.");
        }

        if (stream["Type"] is PdfName type && type.Value != "XRef")
        {
            throw new PdfFormatException("startxref points at a stream that is not a cross-reference stream.");
        }

        dictionary = stream;

        int[] widths = (Resolve(stream["W"]) as PdfArray)?
            .Select(item => Resolve(item) is PdfInteger { Value: >= 0 and <= 8 } width ? (int)width.Value : -1)
            .ToArray() ?? Array.Empty<int>();

        if (widths.Length < 3 || widths.Any(width => width < 0))
        {
            throw new PdfFormatException("A cross-reference stream has an invalid /W.");
        }

        long size = Resolve(stream["Size"]) is PdfInteger declared ? declared.Value : 0;
        var ranges = new List<(long First, long Count)>();

        if (Resolve(stream["Index"]) is PdfArray index)
        {
            for (int i = 0; i + 1 < index.Count; i += 2)
            {
                if (Resolve(index[i]) is PdfInteger first && Resolve(index[i + 1]) is PdfInteger count)
                {
                    ranges.Add((first.Value, count.Value));
                }
            }
        }
        else
        {
            ranges.Add((0, size));
        }

        byte[] data = StreamDecoder.Decode(stream, this);
        int rowLength = widths[0] + widths[1] + widths[2];
        int position = 0;
        var section = new Dictionary<int, XrefEntry>();

        if (rowLength == 0)
        {
            return section;
        }

        foreach ((long first, long count) in ranges)
        {
            for (long i = 0; i < count && position + rowLength <= data.Length; i++)
            {
                // A zero-width type field means every entry is type 1 (table 17).
                long kind = widths[0] == 0 ? 1 : Field(data, position, widths[0]);
                long field2 = Field(data, position + widths[0], widths[1]);
                long field3 = Field(data, position + widths[0] + widths[1], widths[2]);

                position += rowLength;

                long number = first + i;

                if (number < 0 || number > int.MaxValue)
                {
                    continue;
                }

                // Unknown types are to be read as references to the null object, which leaving
                // them out achieves.
                XrefEntry? entry = kind switch
                {
                    0 => new XrefEntry(XrefKind.Free, 0, (int)Math.Min(field3, 65535)),
                    1 => new XrefEntry(XrefKind.InUse, field2, (int)Math.Min(field3, 65535)),
                    2 => new XrefEntry(XrefKind.Compressed, field2, (int)Math.Min(field3, int.MaxValue)),
                    _ => null
                };

                if (entry is XrefEntry value)
                {
                    section.TryAdd((int)number, value);
                }
            }
        }

        return section;

        static long Field(byte[] bytes, int offset, int width)
        {
            long value = 0;

            for (int i = 0; i < width; i++)
            {
                value = (value << 8) | bytes[offset + i];
            }

            return value;
        }
    }

    /// <summary>
    /// Adds a section's entries under those already known. Sections are read newest first, so an
    /// entry already present - including one marking the object free - wins.
    /// </summary>
    private void Merge(Dictionary<int, XrefEntry> section)
    {
        foreach (KeyValuePair<int, XrefEntry> entry in section)
        {
            entries.TryAdd(entry.Key, entry.Value);
        }
    }

    private long FindStartXref()
    {
        // The spec puts startxref in the last kilobyte, but trailing junk after %%EOF is common
        // enough that the whole file is searched backwards if it is not there.
        int found = Data.AsSpan().LastIndexOf("startxref"u8);

        if (found < 0)
        {
            return -1;
        }

        try
        {
            var parser = new PdfParser(Data, found + 9);

            return parser.ReadUnsignedLong();
        }
        catch (PdfFormatException)
        {
            return -1;
        }
    }

    // ---------------------------------------------------------------- repair

    private bool HasUsableCatalog()
    {
        try
        {
            return Resolve(Trailer["Root"]) is PdfDictionary catalog && Resolve(catalog["Pages"]) is PdfDictionary;
        }
        catch (PdfFormatException)
        {
            return false;
        }
    }

    private void ThrowIfEncrypted()
    {
        if (Trailer.ContainsKey("Encrypt"))
        {
            throw new PdfFormatException(
                "The PDF is encrypted. Encrypted documents cannot be edited, signed or verified; save an unencrypted copy (remove the password or restrictions) and use that.");
        }
    }

    /// <summary>
    /// Rebuilds the cross-reference information from a scan of the whole file: every
    /// <c>N G obj</c> found, the contents of every object stream, and the newest trailer.
    /// </summary>
    private void Reconstruct()
    {
        Reconstructed = true;
        entries.Clear();
        cache.Clear();
        objectStreams.Clear();

        ScanResult result = Scan();

        foreach (KeyValuePair<int, (long Offset, int Generation)> found in result.Objects)
        {
            entries[found.Key] = new XrefEntry(XrefKind.InUse, found.Value.Offset, found.Value.Generation);
        }

        // Objects inside object streams cannot be seen by a scan, so every object stream found is
        // opened and what it holds is listed - unless a plain object of the same number exists,
        // which is the newer of the two in every file that has both.
        var candidates = new List<(long Position, PdfDictionary Trailer)>();

        foreach (KeyValuePair<int, (long Offset, int Generation)> found in result.Objects.OrderBy(pair => pair.Value.Offset))
        {
            if (GetObject(found.Key) is not PdfStream stream || stream["Type"] is not PdfName type)
            {
                continue;
            }

            if (type.Value == "ObjStm" && GetObjectStream(found.Key) is ObjectStream objectStream)
            {
                int index = 0;

                foreach (int number in objectStream.Offsets.Keys)
                {
                    entries.TryAdd(number, new XrefEntry(XrefKind.Compressed, found.Key, index++));
                }
            }
            else if (type.Value == "XRef")
            {
                candidates.Add((found.Value.Offset, stream));
            }
        }

        foreach (long position in result.Trailers)
        {
            try
            {
                var parser = new PdfParser(Data, (int)position + 7);

                if (parser.ReadObject() is PdfDictionary trailer)
                {
                    candidates.Add((position, trailer));
                }
            }
            catch (PdfFormatException)
            {
                // A damaged trailer is simply not a candidate.
            }
        }

        PdfDictionary? chosen = candidates
            .Where(candidate => candidate.Trailer["Root"] is PdfReference)
            .OrderBy(candidate => candidate.Position)
            .Select(candidate => candidate.Trailer)
            .LastOrDefault();

        if (chosen is null)
        {
            // No trailer survived, so the catalog is found by its type. The newest one wins.
            chosen = new PdfDictionary();

            foreach (KeyValuePair<int, XrefEntry> entry in entries.OrderBy(pair => pair.Value.Kind == XrefKind.InUse ? pair.Value.Offset : long.MaxValue))
            {
                if (GetObject(entry.Key) is PdfDictionary dictionary &&
                    dictionary["Type"] is PdfName { Value: "Catalog" })
                {
                    chosen.Set("Root", new PdfReference(entry.Key, GenerationOf(entry.Key)));
                }
            }

            PdfDictionary? withInfo = candidates.Select(candidate => candidate.Trailer).LastOrDefault(t => t["Info"] is PdfReference);

            if (withInfo?["Info"] is PdfObject info)
            {
                chosen.Set("Info", info);
            }
        }

        Trailer = chosen;
        LastSectionIsStream = entries.Values.Any(entry => entry.Kind == XrefKind.Compressed);
        StartXref = -1;
        cache.Clear();
    }

    /// <summary>
    /// Finds every <c>N G obj</c> header and trailer keyword in the file, skipping over stream data
    /// so that bytes inside an image cannot pass for an object. Later headers win, because an
    /// incremental update appends the newer version of an object after the older one.
    /// </summary>
    private ScanResult Scan()
    {
        if (scan is not null)
        {
            return scan;
        }

        var objects = new Dictionary<int, (long Offset, int Generation)>();
        var trailers = new List<long>();
        byte[] data = Data;

        for (int i = 0; i < data.Length; i++)
        {
            byte c = data[i];

            if (c == (byte)'s' && Matches(data, i, "stream"u8) &&
                (i == 0 || PdfParser.IsWhitespace(data[i - 1]) || data[i - 1] == (byte)'>') &&
                i + 6 < data.Length && (data[i + 6] == (byte)'\r' || data[i + 6] == (byte)'\n'))
            {
                int close = data.AsSpan(i + 6).IndexOf("endstream"u8);

                if (close < 0)
                {
                    break;
                }

                i += 6 + close + 8;

                continue;
            }

            if (c == (byte)'t' && Matches(data, i, "trailer"u8) && IsBoundary(data, i - 1) && IsBoundary(data, i + 7))
            {
                trailers.Add(i);

                continue;
            }

            if (c == (byte)'o' && Matches(data, i, "obj"u8) && IsBoundary(data, i + 3) &&
                TryReadHeaderBefore(data, i, out int number, out int generation, out int headerStart))
            {
                objects[number] = (headerStart, generation);
                i += 2;
            }
        }

        scan = new ScanResult(objects, trailers);

        return scan;

        static bool Matches(byte[] bytes, int at, ReadOnlySpan<byte> word) =>
            at + word.Length <= bytes.Length && bytes.AsSpan(at, word.Length).SequenceEqual(word);

        static bool IsBoundary(byte[] bytes, int at) =>
            at < 0 || at >= bytes.Length || !PdfParser.IsRegular(bytes[at]);
    }

    /// <summary>Walks back from an <c>obj</c> keyword over the generation and object number.</summary>
    private static bool TryReadHeaderBefore(byte[] data, int keyword, out int number, out int generation, out int start)
    {
        number = 0;
        generation = 0;
        start = 0;

        int p = keyword - 1;

        if (p < 0 || !PdfParser.IsWhitespace(data[p]))
        {
            return false;
        }

        while (p >= 0 && PdfParser.IsWhitespace(data[p]))
        {
            p--;
        }

        int generationEnd = p + 1;

        while (p >= 0 && PdfParser.IsDigit(data[p]))
        {
            p--;
        }

        int generationStart = p + 1;

        if (generationStart == generationEnd || generationEnd - generationStart > 5 || p < 0 || !PdfParser.IsWhitespace(data[p]))
        {
            return false;
        }

        while (p >= 0 && PdfParser.IsWhitespace(data[p]))
        {
            p--;
        }

        int numberEnd = p + 1;

        while (p >= 0 && PdfParser.IsDigit(data[p]))
        {
            p--;
        }

        int numberStart = p + 1;

        if (numberStart == numberEnd || numberEnd - numberStart > 9 || (p >= 0 && PdfParser.IsRegular(data[p])))
        {
            return false;
        }

        number = int.Parse(data.AsSpan(numberStart, numberEnd - numberStart), NumberStyles.None, CultureInfo.InvariantCulture);
        generation = int.Parse(data.AsSpan(generationStart, generationEnd - generationStart), NumberStyles.None, CultureInfo.InvariantCulture);
        start = numberStart;

        return number > 0;
    }

    private sealed record ScanResult(Dictionary<int, (long Offset, int Generation)> Objects, List<long> Trailers);

    /// <summary>The decoded contents of an object stream and where each object in it starts.</summary>
    private sealed class ObjectStream
    {
        private ObjectStream(byte[] data, int first, Dictionary<int, int> offsets)
        {
            Data = data;
            First = first;
            Offsets = offsets;
        }

        public byte[] Data { get; }

        public int First { get; }

        public Dictionary<int, int> Offsets { get; }

        public static ObjectStream Read(PdfStream stream, PdfFile file)
        {
            if (file.Resolve(stream["N"]) is not PdfInteger { Value: >= 0 } count ||
                file.Resolve(stream["First"]) is not PdfInteger { Value: >= 0 } first)
            {
                throw new PdfFormatException("An object stream is missing /N or /First.");
            }

            byte[] data = StreamDecoder.Decode(stream, file);
            var header = new PdfParser(data, 0, (int)Math.Min(first.Value, data.Length));
            var offsets = new Dictionary<int, int>();

            for (long i = 0; i < count.Value && !header.AtEnd; i++)
            {
                int number = header.ReadUnsignedInteger();
                int offset = header.ReadUnsignedInteger();

                offsets.TryAdd(number, offset);
            }

            return new ObjectStream(data, (int)first.Value, offsets);
        }
    }
}

internal enum XrefKind
{
    Free,
    InUse,
    Compressed
}

/// <summary>
/// One cross-reference entry. For an object in use, <see cref="Offset"/> is its byte offset; for a
/// compressed one it is the number of the object stream holding it and <see cref="Generation"/> is
/// its index there.
/// </summary>
internal readonly record struct XrefEntry(XrefKind Kind, long Offset, int Generation);
