using System.Globalization;
using System.Text;
using Theoistic.PDF.Writing;

namespace Theoistic.PDF.Editing;

/// <summary>
/// Reads PDF objects out of bytes (ISO 32000-1 sections 7.2 and 7.3) into the same object model
/// the writer emits, so anything read can be written straight back out.
/// <para>
/// Streams are kept exactly as they are stored: the bytes stay encoded and the dictionary keeps
/// its /Filter and /DecodeParms, so copying a page between files never re-encodes its content.
/// Only the reader's own structures - cross-reference and object streams - are ever decoded.
/// </para>
/// </summary>
internal sealed class PdfParser
{
    // Real documents nest a handful of levels. Anything this deep is corrupt or hostile, and
    // refusing it keeps a crafted file from exhausting the stack.
    private const int MaxDepth = 256;

    private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

    private readonly byte[] data;
    private readonly int end;
    private readonly Func<PdfReference, PdfObject?>? resolve;

    /// <param name="data">The bytes to read.</param>
    /// <param name="start">Where reading begins.</param>
    /// <param name="length">How many bytes from <paramref name="start"/> may be read; the rest of the array when null.</param>
    /// <param name="resolve">
    /// Resolves an indirect /Length, which a stream is allowed to have. Without it such a stream
    /// is delimited by searching for its endstream keyword instead.
    /// </param>
    public PdfParser(byte[] data, int start = 0, int? length = null, Func<PdfReference, PdfObject?>? resolve = null)
    {
        ArgumentNullException.ThrowIfNull(data);

        this.data = data;
        this.resolve = resolve;

        end = length is int count ? Math.Min(data.Length, start + count) : data.Length;
        Position = start;
    }

    public int Position { get; set; }

    public bool AtEnd
    {
        get
        {
            SkipWhitespace();

            return Position >= end;
        }
    }

    // ---------------------------------------------------------------- objects

    /// <summary>Reads one object of any type, following an integer pair and R into a reference.</summary>
    public PdfObject ReadObject() => ReadObject(0);

    /// <summary>
    /// Reads <c>N G obj ... endobj</c> and returns the object, reporting the number and generation
    /// the file gives it so a caller can check it found the object it was looking for.
    /// </summary>
    public PdfObject ReadIndirectObject(out int number, out int generation)
    {
        SkipWhitespace();
        number = ReadUnsignedInteger();

        SkipWhitespace();
        generation = ReadUnsignedInteger();

        SkipWhitespace();
        ExpectKeyword("obj");

        PdfObject value = ReadObject();

        // endobj is required, but a missing one costs nothing to forgive: the object is complete.
        SkipWhitespace();

        if (MatchesKeyword("endobj"))
        {
            Position += 6;
        }

        return value;
    }

    private PdfObject ReadObject(int depth)
    {
        if (depth > MaxDepth)
        {
            throw Error("Objects are nested too deeply");
        }

        SkipWhitespace();

        if (Position >= end)
        {
            throw Error("The data ends where an object was expected");
        }

        byte b = data[Position];

        switch (b)
        {
            case (byte)'/':
                return new PdfName(ReadNameValue());

            case (byte)'(':
                return ReadLiteralString();

            case (byte)'[':
                return ReadArray(depth);

            case (byte)'<':
                return Position + 1 < end && data[Position + 1] == (byte)'<'
                    ? ReadDictionary(depth)
                    : ReadHexString();
        }

        if (IsNumberStart(b))
        {
            return ReadNumberOrReference();
        }

        string keyword = ReadKeyword();

        return keyword switch
        {
            "true" => PdfBoolean.True,
            "false" => PdfBoolean.False,
            "null" => PdfNull.Instance,
            _ => throw Error($"'{keyword}' is not an object")
        };
    }

    private PdfArray ReadArray(int depth)
    {
        Position++;

        var array = new PdfArray();

        while (true)
        {
            SkipWhitespace();

            if (Position >= end)
            {
                throw Error("An array is not closed");
            }

            if (data[Position] == (byte)']')
            {
                Position++;

                return array;
            }

            array.Add(ReadObject(depth + 1));
        }
    }

    private PdfObject ReadDictionary(int depth)
    {
        Position += 2;

        var dictionary = new PdfDictionary();

        while (true)
        {
            SkipWhitespace();

            if (Position >= end)
            {
                throw Error("A dictionary is not closed");
            }

            if (data[Position] == (byte)'>' && Position + 1 < end && data[Position + 1] == (byte)'>')
            {
                Position += 2;
                break;
            }

            if (data[Position] != (byte)'/')
            {
                throw Error("A dictionary key was expected");
            }

            string key = ReadNameValue();
            PdfObject value = ReadObject(depth + 1);

            // A null value means the same as the entry being absent (7.3.7), and leaving it out
            // keeps every lookup from having to treat the two alike.
            if (value is not PdfNull)
            {
                dictionary[key] = value;
            }
        }

        return ReadStreamBody(dictionary);
    }

    /// <summary>
    /// Turns the dictionary just read into a stream if the stream keyword follows it, and
    /// returns it unchanged otherwise.
    /// </summary>
    private PdfObject ReadStreamBody(PdfDictionary dictionary)
    {
        int mark = Position;

        SkipWhitespace();

        if (!MatchesKeyword("stream"))
        {
            Position = mark;

            return dictionary;
        }

        Position += 6;

        // The keyword is followed by CRLF or LF. A lone CR is wrong but common enough in the wild
        // that refusing it would refuse real files.
        if (Position < end && data[Position] == (byte)'\r')
        {
            Position++;
        }

        if (Position < end && data[Position] == (byte)'\n')
        {
            Position++;
        }

        int start = Position;
        int length = DeclaredLength(dictionary, start);

        // A wrong /Length is the single most common defect in real files, so it is checked
        // against where endstream actually is rather than trusted.
        if (length < 0 || !EndstreamFollows(start + length))
        {
            length = FindEndstream(start);
        }

        var stream = new PdfStream(data.AsSpan(start, length).ToArray(), compress: false);

        foreach (KeyValuePair<string, PdfObject> entry in dictionary)
        {
            stream.Set(entry.Key, entry.Value);
        }

        Position = start + length;
        SkipWhitespace();

        if (MatchesKeyword("endstream", requireBoundary: false))
        {
            Position += 9;
        }

        return stream;
    }

    private int DeclaredLength(PdfDictionary dictionary, int start)
    {
        PdfObject? value = dictionary["Length"];

        if (value is PdfReference reference && resolve is not null)
        {
            try
            {
                value = resolve(reference);
            }
            catch (PdfFormatException)
            {
                value = null;
            }
        }

        return value is PdfInteger { Value: >= 0 } integer && integer.Value <= end - start
            ? (int)integer.Value
            : -1;
    }

    private bool EndstreamFollows(int position)
    {
        if (position > end)
        {
            return false;
        }

        int saved = Position;

        Position = position;

        while (Position < end && IsWhitespace(data[Position]))
        {
            Position++;
        }

        bool found = MatchesKeyword("endstream", requireBoundary: false);

        Position = saved;

        return found;
    }

    private int FindEndstream(int start)
    {
        int found = data.AsSpan(start, end - start).IndexOf("endstream"u8);

        if (found < 0)
        {
            throw Error("A stream has no endstream keyword");
        }

        int length = found;

        // The end-of-line marker before endstream belongs to the syntax, not the data.
        if (length > 0 && data[start + length - 1] == (byte)'\n')
        {
            length--;
        }

        if (length > 0 && data[start + length - 1] == (byte)'\r')
        {
            length--;
        }

        return length;
    }

    private PdfObject ReadNumberOrReference()
    {
        PdfObject number = ReadNumber();

        // "12 0 R" is three tokens, so an integer has to look ahead to know whether it is a
        // number or the start of a reference - and step back if it is not.
        if (number is PdfInteger { Value: >= 0 and <= int.MaxValue } first)
        {
            int mark = Position;

            SkipWhitespace();

            if (Position < end && IsDigit(data[Position]))
            {
                PdfObject second = ReadNumber();

                if (second is PdfInteger { Value: >= 0 and <= int.MaxValue } generation)
                {
                    SkipWhitespace();

                    if (Position < end && data[Position] == (byte)'R' && IsBoundary(Position + 1))
                    {
                        Position++;

                        // Object 0 is the head of the free list, so "0 0 R" points at nothing.
                        return first.Value >= 1
                            ? new PdfReference((int)first.Value, (int)generation.Value)
                            : PdfNull.Instance;
                    }
                }
            }

            Position = mark;
        }

        return number;
    }

    private PdfObject ReadNumber()
    {
        int start = Position;

        while (Position < end && IsNumberChar(data[Position]))
        {
            Position++;
        }

        string token = Encoding.ASCII.GetString(data, start, Position - start);

        if (!token.Contains('.') &&
            long.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long integer))
        {
            return new PdfInteger(integer);
        }

        if (IsWellFormedReal(token) &&
            double.TryParse(token, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double value) &&
            double.IsFinite(value))
        {
            return new PdfReal(value, token);
        }

        // Malformed numbers such as "--5" or "0.5.1" turn up in old files. Viewers read as much of
        // the token as makes a number, and so does this; the literal is not kept, so what is
        // written back is at least valid.
        return new PdfReal(LenientNumber(token));
    }

    private static double LenientNumber(string token)
    {
        var text = new StringBuilder();
        bool negative = false;
        bool point = false;

        foreach (char c in token)
        {
            if (c == '-' && text.Length == 0)
            {
                negative = true;
            }
            else if (c == '.' && !point)
            {
                point = true;
                text.Append(c);
            }
            else if (char.IsAsciiDigit(c))
            {
                text.Append(c);
            }
            else if (text.Length > 0)
            {
                break;
            }
        }

        if (!double.TryParse(text.ToString(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double value) ||
            !double.IsFinite(value))
        {
            return 0;
        }

        return negative ? -value : value;
    }

    private static bool IsWellFormedReal(string token)
    {
        int i = 0;

        if (i < token.Length && (token[i] == '+' || token[i] == '-'))
        {
            i++;
        }

        bool digits = false;
        bool point = false;

        for (; i < token.Length; i++)
        {
            if (char.IsAsciiDigit(token[i]))
            {
                digits = true;
            }
            else if (token[i] == '.' && !point)
            {
                point = true;
            }
            else
            {
                return false;
            }
        }

        return digits;
    }

    private PdfString ReadLiteralString()
    {
        Position++;

        var bytes = new List<byte>();
        int depth = 1;

        while (Position < end)
        {
            byte c = data[Position++];

            switch (c)
            {
                case (byte)'\\':
                    ReadEscape(bytes);
                    break;

                case (byte)'(':
                    // Balanced parentheses need no escape, so they are part of the string.
                    depth++;
                    bytes.Add(c);
                    break;

                case (byte)')':
                    if (--depth == 0)
                    {
                        return new PdfString(bytes.ToArray());
                    }

                    bytes.Add(c);
                    break;

                case (byte)'\r':
                    // An unescaped end-of-line of any kind reads as a single LF (7.3.4.2).
                    if (Position < end && data[Position] == (byte)'\n')
                    {
                        Position++;
                    }

                    bytes.Add((byte)'\n');
                    break;

                default:
                    bytes.Add(c);
                    break;
            }
        }

        throw Error("A string is not closed");
    }

    private void ReadEscape(List<byte> bytes)
    {
        if (Position >= end)
        {
            return;
        }

        byte e = data[Position++];

        switch (e)
        {
            case (byte)'n': bytes.Add((byte)'\n'); break;
            case (byte)'r': bytes.Add((byte)'\r'); break;
            case (byte)'t': bytes.Add((byte)'\t'); break;
            case (byte)'b': bytes.Add((byte)'\b'); break;
            case (byte)'f': bytes.Add((byte)'\f'); break;

            case (byte)'\r':
                // A backslash before an end-of-line continues the string on the next line.
                if (Position < end && data[Position] == (byte)'\n')
                {
                    Position++;
                }

                break;

            case (byte)'\n':
                break;

            case >= (byte)'0' and <= (byte)'7':
            {
                int value = e - '0';

                for (int i = 0; i < 2 && Position < end && data[Position] >= (byte)'0' && data[Position] <= (byte)'7'; i++)
                {
                    value = (value * 8) + (data[Position++] - '0');
                }

                // Three octal digits can reach 511; the high bit is dropped, as the spec says.
                bytes.Add((byte)value);
                break;
            }

            default:
                // \( \) \\ stand for themselves, and an unknown escape is the character alone.
                bytes.Add(e);
                break;
        }
    }

    private PdfString ReadHexString()
    {
        Position++;

        var bytes = new List<byte>();
        int high = -1;

        while (Position < end)
        {
            byte c = data[Position++];

            if (c == (byte)'>')
            {
                break;
            }

            int nibble = HexValue(c);

            if (nibble < 0)
            {
                continue;
            }

            if (high < 0)
            {
                high = nibble;
            }
            else
            {
                bytes.Add((byte)((high << 4) | nibble));
                high = -1;
            }
        }

        // An odd final digit is followed by an implied zero (7.3.4.3).
        if (high >= 0)
        {
            bytes.Add((byte)(high << 4));
        }

        return new PdfString(bytes.ToArray(), hexadecimal: true);
    }

    /// <summary>Reads a name token, starting at its solidus, and returns its decoded value.</summary>
    private string ReadNameValue()
    {
        Position++;

        var bytes = new List<byte>();

        while (Position < end && IsRegular(data[Position]))
        {
            byte c = data[Position];

            if (c == (byte)'#' && Position + 2 < end && HexValue(data[Position + 1]) >= 0 && HexValue(data[Position + 2]) >= 0)
            {
                bytes.Add((byte)((HexValue(data[Position + 1]) << 4) | HexValue(data[Position + 2])));
                Position += 3;

                continue;
            }

            bytes.Add(c);
            Position++;
        }

        return DecodeName(bytes.ToArray());
    }

    /// <summary>
    /// Turns the bytes of a name into the string the object model keys on. Valid UTF-8 is
    /// decoded; anything else has its high bytes carried as lone surrogates, which
    /// <see cref="PdfWriter.EncodeName"/> turns back into the same bytes.
    /// </summary>
    internal static string DecodeName(byte[] bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            var chars = new char[bytes.Length];

            for (int i = 0; i < bytes.Length; i++)
            {
                chars[i] = bytes[i] < 0x80 ? (char)bytes[i] : (char)(0xDC00 + bytes[i]);
            }

            return new string(chars);
        }
    }

    // ---------------------------------------------------------------- tokens

    /// <summary>Skips whitespace and comments.</summary>
    public void SkipWhitespace()
    {
        while (Position < end)
        {
            byte c = data[Position];

            if (IsWhitespace(c))
            {
                Position++;
            }
            else if (c == (byte)'%')
            {
                while (Position < end && data[Position] != (byte)'\n' && data[Position] != (byte)'\r')
                {
                    Position++;
                }
            }
            else
            {
                break;
            }
        }
    }

    /// <summary>Reads a run of regular characters - an operator or keyword.</summary>
    public string ReadKeyword()
    {
        SkipWhitespace();

        int start = Position;

        while (Position < end && IsRegular(data[Position]))
        {
            Position++;
        }

        // A stray delimiter is consumed as a one-character token, so a caller that loops over
        // tokens always makes progress.
        if (Position == start && Position < end)
        {
            Position++;
        }

        return Encoding.ASCII.GetString(data, start, Position - start);
    }

    public int ReadUnsignedInteger()
    {
        long value = ReadUnsignedLong();

        if (value > int.MaxValue)
        {
            throw Error("A number is out of range");
        }

        return (int)value;
    }

    public long ReadUnsignedLong()
    {
        SkipWhitespace();

        int start = Position;
        long value = 0;

        while (Position < end && IsDigit(data[Position]))
        {
            if (Position - start >= 18)
            {
                throw Error("A number is out of range");
            }

            value = (value * 10) + (data[Position++] - '0');
        }

        if (Position == start)
        {
            throw Error("A number was expected");
        }

        return value;
    }

    /// <summary>Whether the keyword starts at the current position and ends at a token boundary.</summary>
    public bool MatchesKeyword(string keyword, bool requireBoundary = true)
    {
        if (Position + keyword.Length > end)
        {
            return false;
        }

        for (int i = 0; i < keyword.Length; i++)
        {
            if (data[Position + i] != keyword[i])
            {
                return false;
            }
        }

        return !requireBoundary || IsBoundary(Position + keyword.Length);
    }

    public void ExpectKeyword(string keyword)
    {
        SkipWhitespace();

        if (!MatchesKeyword(keyword))
        {
            throw Error($"'{keyword}' was expected");
        }

        Position += keyword.Length;
    }

    private PdfFormatException Error(string message) =>
        new PdfFormatException($"{message} (at byte {Position.ToString(CultureInfo.InvariantCulture)}).");

    // ---------------------------------------------------------------- character classes

    public static bool IsWhitespace(byte c) =>
        c is 0x00 or 0x09 or 0x0A or 0x0C or 0x0D or 0x20;

    public static bool IsDelimiter(byte c) =>
        c is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    public static bool IsRegular(byte c) => !IsWhitespace(c) && !IsDelimiter(c);

    public static bool IsDigit(byte c) => c >= (byte)'0' && c <= (byte)'9';

    private bool IsBoundary(int position) =>
        position >= end || !IsRegular(data[position]);

    private static bool IsNumberStart(byte c) =>
        IsDigit(c) || c == (byte)'+' || c == (byte)'-' || c == (byte)'.';

    private static bool IsNumberChar(byte c) =>
        IsDigit(c) || c == (byte)'+' || c == (byte)'-' || c == (byte)'.';

    private static int HexValue(byte c) => c switch
    {
        >= (byte)'0' and <= (byte)'9' => c - '0',
        >= (byte)'a' and <= (byte)'f' => c - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => c - 'A' + 10,
        _ => -1
    };
}
