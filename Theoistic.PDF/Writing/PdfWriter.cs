using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Theoistic.PDF.Writing;

/// <summary>
/// Serialises <see cref="PdfObject"/> values as PDF syntax, tracking the byte offset of
/// everything it writes so that the cross-reference table can be built.
/// <para>
/// Byte offsets are the reason this type exists rather than a plain <see cref="StreamWriter"/>:
/// the trailer has to record where each indirect object starts, counted in bytes from the
/// beginning of the file, and a text writer with its own buffering cannot answer that.
/// </para>
/// </summary>
internal sealed class PdfWriter
{
    private readonly Stream output;

    public PdfWriter(Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);

        this.output = output;
    }

    /// <summary>Byte offset of the next write, counted from the start of the file.</summary>
    public long Position { get; private set; }

    public void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        output.Write(bytes);
        Position += bytes.Length;
    }

    /// <summary>
    /// Writes characters as one byte each. Every caller passes ASCII that is part of the PDF
    /// syntax itself - keywords, operators, digits - so there is nothing to encode.
    /// </summary>
    public void WriteRaw(string ascii)
    {
        ArgumentNullException.ThrowIfNull(ascii);

        byte[] buffer = new byte[Encoding.ASCII.GetByteCount(ascii)];

        Encoding.ASCII.GetBytes(ascii, buffer);
        WriteBytes(buffer);
    }

    public void WriteByte(byte value) => WriteBytes(stackalloc byte[] { value });

    public void WriteSpace() => WriteByte((byte)' ');

    /// <summary>
    /// Ends a line with a bare LF. The spec permits CR, LF or CRLF as an end-of-line marker
    /// everywhere except inside the fixed-width cross-reference entries, so using LF uniformly
    /// keeps offsets easy to reason about.
    /// </summary>
    public void WriteLine() => WriteByte((byte)'\n');

    public void WriteLine(string ascii)
    {
        WriteRaw(ascii);
        WriteLine();
    }

    public void WriteInteger(long value) =>
        WriteRaw(value.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Writes a real number in the only form PDF accepts: optional sign, digits, optional
    /// decimal point. Exponent notation is not valid PDF, which rules out the default
    /// round-trip formatting for very small and very large magnitudes.
    /// </summary>
    public void WriteReal(double value) => WriteRaw(FormatReal(value));

    /// <summary>
    /// Formats a real number for either the file body or a content stream, which share the same
    /// numeric syntax.
    /// </summary>
    internal static string FormatReal(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                "PDF has no syntax for NaN or infinity; a real number must be finite.");
        }

        // Five decimals is what Acrobat itself emits and is well inside the precision a PDF
        // consumer is required to honour. The fixed-point format also guarantees no 'E' appears.
        string text = value.ToString("0.#####", CultureInfo.InvariantCulture);

        // Rounding a tiny negative towards zero yields "-0", which is legal but pointless noise
        // in the output and breaks byte-for-byte comparison against a fixture.
        return text == "-0" ? "0" : text;
    }

    /// <summary>
    /// Writes a name object. Anything outside the printable ASCII range, and every character
    /// the lexer treats as a delimiter, has to be hex-escaped or the name would end early.
    /// </summary>
    public void WriteName(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        WriteByte((byte)'/');

        foreach (byte b in EncodeName(value))
        {
            if (IsRegularNameByte(b))
            {
                WriteByte(b);
            }
            else
            {
                WriteRaw("#");
                WriteRaw(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }
    }

    /// <summary>
    /// The bytes of a name. Names are UTF-8 by convention, but one read from an existing file can
    /// be any byte sequence - a font name in a legacy code page, say. The reader carries each byte
    /// that is not valid UTF-8 as a lone surrogate from U+DC80 to U+DCFF, and those are turned back
    /// into the original bytes here, so a name is written exactly as it was read. Rewriting it any
    /// other way would break every content stream that selects a resource by that name.
    /// </summary>
    internal static byte[] EncodeName(string value)
    {
        bool carriesRawBytes = false;

        for (int i = 0; i < value.Length && !carriesRawBytes; i++)
        {
            carriesRawBytes = IsRawByte(value, i);
        }

        if (!carriesRawBytes)
        {
            return Encoding.UTF8.GetBytes(value);
        }

        var bytes = new List<byte>(value.Length);
        int runStart = 0;

        for (int i = 0; i <= value.Length; i++)
        {
            if (i < value.Length && !IsRawByte(value, i))
            {
                continue;
            }

            bytes.AddRange(Encoding.UTF8.GetBytes(value[runStart..i]));

            if (i < value.Length)
            {
                bytes.Add((byte)(value[i] - 0xDC00));
            }

            runStart = i + 1;
        }

        return bytes.ToArray();

        // A low surrogate in that range is a carried byte only when it stands alone; preceded by
        // a high surrogate it is half of an ordinary supplementary-plane character.
        static bool IsRawByte(string text, int index) =>
            text[index] >= '\uDC80' && text[index] <= '\uDCFF' &&
            (index == 0 || !char.IsHighSurrogate(text[index - 1]));
    }

    public void WriteString(PdfString value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Hexadecimal)
        {
            WriteByte((byte)'<');

            foreach (byte b in value.Bytes)
            {
                WriteRaw(b.ToString("X2", CultureInfo.InvariantCulture));
            }

            WriteByte((byte)'>');

            return;
        }

        WriteByte((byte)'(');

        foreach (byte b in value.Bytes)
        {
            switch (b)
            {
                case (byte)'(':
                case (byte)')':
                case (byte)'\\':
                    // Unbalanced parentheses would terminate the string early, and a literal
                    // backslash would be read as the start of an escape.
                    WriteByte((byte)'\\');
                    WriteByte(b);
                    break;

                case (byte)'\n':
                    WriteRaw("\\n");
                    break;

                case (byte)'\r':
                    // A raw CR inside a literal string is normalised to LF by conforming
                    // readers, which would silently corrupt the value.
                    WriteRaw("\\r");
                    break;

                case (byte)'\t':
                    WriteRaw("\\t");
                    break;

                case (byte)'\b':
                    WriteRaw("\\b");
                    break;

                case (byte)'\f':
                    WriteRaw("\\f");
                    break;

                default:
                    if (b < 32 || b > 126)
                    {
                        // Octal escape keeps the file ASCII-safe, which matters for the bytes
                        // that some transports and text editors would otherwise rewrite.
                        WriteByte((byte)'\\');
                        WriteRaw(Convert.ToString(b, 8).PadLeft(3, '0'));
                    }
                    else
                    {
                        WriteByte(b);
                    }

                    break;
            }
        }

        WriteByte((byte)')');
    }

    public void WriteArray(PdfArray array)
    {
        ArgumentNullException.ThrowIfNull(array);

        WriteByte((byte)'[');

        bool first = true;

        foreach (PdfObject item in array)
        {
            // Names and strings are self-delimiting, but two numbers in a row would merge into
            // one token, so a separator goes between every pair of elements.
            if (!first)
            {
                WriteSpace();
            }

            WriteObject(item);
            first = false;
        }

        WriteByte((byte)']');
    }

    public void WriteDictionary(PdfDictionary dictionary)
    {
        ArgumentNullException.ThrowIfNull(dictionary);

        WriteRaw("<<");

        foreach (KeyValuePair<string, PdfObject> entry in dictionary)
        {
            WriteName(entry.Key);
            WriteSpace();
            WriteObject(entry.Value);
        }

        WriteRaw(">>");
    }

    /// <summary>
    /// Writes a stream object: the dictionary, then the encoded bytes between the stream and
    /// endstream keywords.
    /// <para>
    /// The length and filter entries are maintained here rather than by the caller. If the
    /// dictionary already carries a filter, the data is taken to be encoded already and is
    /// passed through untouched - that is what lets a JPEG be embedded without re-encoding it.
    /// </para>
    /// </summary>
    public void WriteStream(PdfStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        byte[] payload = stream.Data;
        bool alreadyFiltered = stream.ContainsKey("Filter");
        bool filterHere = stream.Compress && !alreadyFiltered;

        if (filterHere)
        {
            payload = Deflate(payload);
        }

        // The length and filter go onto a copy rather than onto the stream itself. Writing must
        // not mutate the document: a stream that recorded its own filter would, on a second
        // save, see that filter already present, skip compression, and declare FlateDecode over
        // bytes that were never deflated.
        var header = new PdfDictionary();

        foreach (KeyValuePair<string, PdfObject> entry in stream)
        {
            header.Set(entry.Key, entry.Value);
        }

        if (filterHere)
        {
            header.Set("Filter", PdfName.FlateDecode);
        }

        header.Set("Length", (long)payload.Length);

        WriteDictionary(header);
        WriteLine();
        WriteLine("stream");
        WriteBytes(payload);
        WriteLine();
        WriteRaw("endstream");
    }

    public void WriteReference(PdfReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);

        WriteInteger(reference.ObjectNumber);
        WriteSpace();
        WriteInteger(reference.Generation);
        WriteRaw(" R");
    }

    public void WriteObject(PdfObject value)
    {
        ArgumentNullException.ThrowIfNull(value);

        value.Write(this);
    }

    /// <summary>
    /// zlib-wrapped deflate, which is what FlateDecode means. ZLibStream emits the two-byte
    /// header and Adler-32 trailer that a bare DeflateStream omits and that readers require.
    /// </summary>
    internal static byte[] Deflate(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        using var buffer = new MemoryStream();

        using (var deflate = new ZLibStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            deflate.Write(data, 0, data.Length);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Whether a byte may appear unescaped in a name. The spec's regular characters are the
    /// printable ASCII range minus the delimiters, and the number sign is excluded because it
    /// introduces an escape.
    /// </summary>
    private static bool IsRegularNameByte(byte b)
    {
        if (b < 0x21 || b > 0x7E)
        {
            return false;
        }

        return b switch
        {
            (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' => false,
            (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' => false,
            (byte)'/' or (byte)'%' or (byte)'#' => false,
            _ => true
        };
    }
}
