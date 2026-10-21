using System.Collections;
using System.Text;

namespace Theoistic.PDF.Writing;

/// <summary>
/// Base of the object types the PDF syntax defines (ISO 32000-1 section 7.3). Every value that
/// can appear in a PDF file body is one of these.
/// </summary>
public abstract class PdfObject
{
    /// <summary>Emits this object using the PDF syntax rules for its type.</summary>
    internal abstract void Write(PdfWriter writer);
}

/// <summary>The null object. Also what a dangling reference resolves to.</summary>
public sealed class PdfNull : PdfObject
{
    public static readonly PdfNull Instance = new PdfNull();

    private PdfNull()
    {
    }

    internal override void Write(PdfWriter writer) => writer.WriteRaw("null");
}

public sealed class PdfBoolean : PdfObject
{
    public static readonly PdfBoolean True = new PdfBoolean(true);
    public static readonly PdfBoolean False = new PdfBoolean(false);

    private PdfBoolean(bool value) => Value = value;

    public bool Value { get; }

    public static PdfBoolean Of(bool value) => value ? True : False;

    internal override void Write(PdfWriter writer) => writer.WriteRaw(Value ? "true" : "false");
}

public sealed class PdfInteger : PdfObject
{
    public PdfInteger(long value) => Value = value;

    public long Value { get; }

    internal override void Write(PdfWriter writer) => writer.WriteInteger(Value);
}

public sealed class PdfReal : PdfObject
{
    public PdfReal(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                "PDF has no syntax for NaN or infinity; a real number must be finite.");
        }

        Value = value;
    }

    public double Value { get; }

    internal override void Write(PdfWriter writer) => writer.WriteReal(Value);
}

/// <summary>
/// A PDF string: a byte sequence, not text. The encoding is decided by whatever consumes it - a
/// content-stream string is interpreted through the current font's encoding, while a document
/// information string is PDFDocEncoding or UTF-16BE.
/// </summary>
public sealed class PdfString : PdfObject
{
    public PdfString(byte[] bytes, bool hexadecimal = false)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        Bytes = bytes;
        Hexadecimal = hexadecimal;
    }

    public byte[] Bytes { get; }

    /// <summary>
    /// Whether to emit the angle-bracketed hex form rather than the parenthesised literal form.
    /// Both are legal for any content; hex avoids escaping entirely and suits binary better.
    /// </summary>
    public bool Hexadecimal { get; }

    /// <summary>
    /// A string whose characters all fall inside Latin-1, encoded one byte per character.
    /// Throws for anything outside that range rather than substituting a placeholder, because
    /// silently mangled text is far harder to diagnose downstream than a failure here.
    /// </summary>
    public static PdfString Latin1(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var bytes = new byte[text.Length];

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (c > 0xFF)
            {
                throw new ArgumentException(
                    $"U+{(int)c:X4} is outside Latin-1 and cannot be written as a one-byte-per-character PDF string. Use PdfString.Utf16 for arbitrary text.",
                    nameof(text));
            }

            bytes[i] = (byte)c;
        }

        return new PdfString(bytes);
    }

    /// <summary>
    /// Text as UTF-16BE with the byte-order mark PDF requires to tell it apart from
    /// PDFDocEncoding. This is the form for document metadata such as the title.
    /// </summary>
    public static PdfString Utf16(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        byte[] encoded = Encoding.BigEndianUnicode.GetBytes(text);
        var bytes = new byte[encoded.Length + 2];

        bytes[0] = 0xFE;
        bytes[1] = 0xFF;
        encoded.CopyTo(bytes, 2);

        return new PdfString(bytes, hexadecimal: true);
    }

    internal override void Write(PdfWriter writer) => writer.WriteString(this);
}

/// <summary>
/// A name object. The stored value excludes the leading solidus and is the decoded form, so a
/// name containing a space is written back out with the escaped hex form.
/// </summary>
public sealed class PdfName : PdfObject
{
    public PdfName(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        Value = value;
    }

    public string Value { get; }

    // The handful that appear in almost every document, interned to keep the allocation count
    // down on large files where each page repeats them.
    public static readonly PdfName Type = new PdfName("Type");
    public static readonly PdfName Subtype = new PdfName("Subtype");
    public static readonly PdfName Length = new PdfName("Length");
    public static readonly PdfName Filter = new PdfName("Filter");
    public static readonly PdfName FlateDecode = new PdfName("FlateDecode");
    public static readonly PdfName Parent = new PdfName("Parent");
    public static readonly PdfName Kids = new PdfName("Kids");
    public static readonly PdfName Count = new PdfName("Count");
    public static readonly PdfName Resources = new PdfName("Resources");
    public static readonly PdfName MediaBox = new PdfName("MediaBox");
    public static readonly PdfName Contents = new PdfName("Contents");
    public static readonly PdfName Font = new PdfName("Font");
    public static readonly PdfName BaseFont = new PdfName("BaseFont");
    public static readonly PdfName Encoding = new PdfName("Encoding");
    public static readonly PdfName Catalog = new PdfName("Catalog");
    public static readonly PdfName Pages = new PdfName("Pages");
    public static readonly PdfName Type1 = new PdfName("Type1");
    public static readonly PdfName WinAnsiEncoding = new PdfName("WinAnsiEncoding");
    public static readonly PdfName XObject = new PdfName("XObject");

    /// <summary>
    /// The value of a page's type entry. Named differently from <see cref="Pages"/> because the
    /// tree node and the leaf differ by only that one letter, which is easy to mistype.
    /// </summary>
    public static readonly PdfName PageType = new PdfName("Page");

    public override string ToString() => "/" + Value;

    internal override void Write(PdfWriter writer) => writer.WriteName(Value);
}

public sealed class PdfArray : PdfObject, IEnumerable<PdfObject>
{
    private readonly List<PdfObject> items;

    public PdfArray() => items = new List<PdfObject>();

    public PdfArray(IEnumerable<PdfObject> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        items = new List<PdfObject>(values);
    }

    public int Count => items.Count;

    public PdfObject this[int index] => items[index];

    public PdfArray Add(PdfObject value)
    {
        ArgumentNullException.ThrowIfNull(value);

        items.Add(value);

        return this;
    }

    /// <summary>Convenience for the many numeric arrays in PDF, such as the page boxes.</summary>
    public static PdfArray Numbers(params double[] values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var array = new PdfArray();

        foreach (double value in values)
        {
            array.Add(new PdfReal(value));
        }

        return array;
    }

    public static PdfArray Of(PdfRectangle rectangle) =>
        Numbers(rectangle.Left, rectangle.Bottom, rectangle.Right, rectangle.Top);

    public IEnumerator<PdfObject> GetEnumerator() => items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal override void Write(PdfWriter writer) => writer.WriteArray(this);
}

/// <summary>
/// A dictionary keyed by name. Insertion order is preserved so that output is byte-for-byte
/// reproducible, which makes diffing generated files and testing against fixtures practical.
/// </summary>
public class PdfDictionary : PdfObject, IEnumerable<KeyValuePair<string, PdfObject>>
{
    private readonly Dictionary<string, PdfObject> entries;
    private readonly List<string> order;

    public PdfDictionary()
    {
        entries = new Dictionary<string, PdfObject>(StringComparer.Ordinal);
        order = new List<string>();
    }

    public int Count => entries.Count;

    public IReadOnlyList<string> Keys => order;

    public PdfObject? this[string key]
    {
        get => entries.TryGetValue(key, out PdfObject? value) ? value : null;
        set
        {
            ArgumentException.ThrowIfNullOrEmpty(key);

            if (value is null)
            {
                if (entries.Remove(key))
                {
                    order.Remove(key);
                }

                return;
            }

            if (!entries.ContainsKey(key))
            {
                order.Add(key);
            }

            entries[key] = value;
        }
    }

    public PdfDictionary Set(string key, PdfObject value)
    {
        this[key] = value;

        return this;
    }

    public PdfDictionary SetName(string key, string name) => Set(key, new PdfName(name));

    public PdfDictionary Set(string key, long value) => Set(key, new PdfInteger(value));

    public PdfDictionary Set(string key, double value) => Set(key, new PdfReal(value));

    public PdfDictionary Set(string key, bool value) => Set(key, PdfBoolean.Of(value));

    public bool ContainsKey(string key) => entries.ContainsKey(key);

    public IEnumerator<KeyValuePair<string, PdfObject>> GetEnumerator()
    {
        foreach (string key in order)
        {
            yield return new KeyValuePair<string, PdfObject>(key, entries[key]);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal override void Write(PdfWriter writer) => writer.WriteDictionary(this);
}

/// <summary>
/// A stream: a dictionary followed by raw bytes. This type owns the decoded bytes and applies
/// the filter when written, so callers never maintain the length or filter entries by hand.
/// </summary>
public sealed class PdfStream : PdfDictionary
{
    private byte[] data;

    public PdfStream(byte[]? data = null, bool compress = true)
    {
        this.data = data ?? Array.Empty<byte>();
        Compress = compress;
    }

    /// <summary>
    /// Whether to apply FlateDecode when writing. Deflate comes from the BCL's ZLibStream, so
    /// compression costs no dependency.
    /// </summary>
    public bool Compress { get; set; }

    /// <summary>The decoded stream contents, before any filter is applied.</summary>
    public byte[] Data
    {
        get => data;
        set
        {
            ArgumentNullException.ThrowIfNull(value);

            data = value;
        }
    }

    internal override void Write(PdfWriter writer) => writer.WriteStream(this);
}

/// <summary>
/// An indirect reference. References are what keep the writer from recursing: a page can point
/// at its parent and the parent back at the page without either being written twice.
/// </summary>
public sealed class PdfReference : PdfObject
{
    internal PdfReference(int objectNumber, int generation = 0)
    {
        if (objectNumber < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(objectNumber),
                objectNumber,
                "Object numbers start at 1; 0 is reserved for the head of the free list.");
        }

        ObjectNumber = objectNumber;
        Generation = generation;
    }

    public int ObjectNumber { get; }

    public int Generation { get; }

    public override string ToString() => $"{ObjectNumber} {Generation} R";

    internal override void Write(PdfWriter writer) => writer.WriteReference(this);
}
