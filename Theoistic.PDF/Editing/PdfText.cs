using System.Globalization;
using System.Text;
using Theoistic.PDF.Writing;

namespace Theoistic.PDF.Editing;

/// <summary>
/// Text strings and dates as PDF stores them in dictionaries (7.9.2 and 7.9.4): the encoding
/// that signature dictionaries and form fields use, as opposed to the font encoding of text drawn
/// on a page.
/// </summary>
internal static class PdfText
{
    // PDFDocEncoding agrees with Latin-1 except in these places, which hold typographic
    // characters instead of control codes.
    private static readonly Dictionary<byte, char> DocEncodingDifferences = new Dictionary<byte, char>
    {
        [0x18] = '˘', [0x19] = 'ˇ', [0x1A] = 'ˆ', [0x1B] = '˙',
        [0x1C] = '˝', [0x1D] = '˛', [0x1E] = '˚', [0x1F] = '˜',
        [0x80] = '•', [0x81] = '†', [0x82] = '‡', [0x83] = '…',
        [0x84] = '—', [0x85] = '–', [0x86] = 'ƒ', [0x87] = '⁄',
        [0x88] = '‹', [0x89] = '›', [0x8A] = '−', [0x8B] = '‰',
        [0x8C] = '„', [0x8D] = '“', [0x8E] = '”', [0x8F] = '‘',
        [0x90] = '’', [0x91] = '‚', [0x92] = '™', [0x93] = 'ﬁ',
        [0x94] = 'ﬂ', [0x95] = 'Ł', [0x96] = 'Œ', [0x97] = 'Š',
        [0x98] = 'Ÿ', [0x99] = 'Ž', [0x9A] = 'ı', [0x9B] = 'ł',
        [0x9C] = 'œ', [0x9D] = 'š', [0x9E] = 'ž', [0xA0] = '€',
    };

    /// <summary>
    /// Encodes a text string: plain ASCII as a literal, which every tool can read at a glance,
    /// and anything else as UTF-16 with a byte-order mark.
    /// </summary>
    public static PdfString Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        foreach (char c in text)
        {
            if (c < 0x20 || c > 0x7E)
            {
                return PdfString.Utf16(text);
            }
        }

        return PdfString.Latin1(text);
    }

    public static string? Decode(PdfObject? value) => value is PdfString text ? Decode(text.Bytes) : null;

    public static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        // PDF 2.0 also allows UTF-8, marked with its own byte-order mark.
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        var text = new StringBuilder(bytes.Length);

        foreach (byte b in bytes)
        {
            text.Append(DocEncodingDifferences.TryGetValue(b, out char c) ? c : (char)b);
        }

        return text.ToString();
    }

    /// <summary>
    /// Reads a PDF date, <c>D:YYYYMMDDHHmmSSOHH'mm</c>, where everything after the year is
    /// optional. Returns null for anything that is not one.
    /// </summary>
    public static DateTimeOffset? ParseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string value = text.Trim();

        if (value.StartsWith("D:", StringComparison.Ordinal))
        {
            value = value[2..];
        }

        int position = 0;

        int year = Digits(4, -1);
        int month = Digits(2, 1);
        int day = Digits(2, 1);
        int hour = Digits(2, 0);
        int minute = Digits(2, 0);
        int second = Digits(2, 0);

        if (year < 0)
        {
            return null;
        }

        TimeSpan offset = TimeSpan.Zero;

        if (position < value.Length && (value[position] == '+' || value[position] == '-'))
        {
            int sign = value[position] == '-' ? -1 : 1;

            position++;

            int offsetHours = Digits(2, 0);

            if (position < value.Length && value[position] == '\'')
            {
                position++;
            }

            int offsetMinutes = Digits(2, 0);

            offset = TimeSpan.FromMinutes(sign * ((offsetHours * 60) + offsetMinutes));
        }

        try
        {
            return new DateTimeOffset(year, month, day, hour, minute, second, offset);
        }
        catch (ArgumentException)
        {
            return null;
        }

        int Digits(int count, int fallback)
        {
            if (position + count > value.Length ||
                !int.TryParse(value.AsSpan(position, count), NumberStyles.None, CultureInfo.InvariantCulture, out int number))
            {
                return fallback;
            }

            position += count;

            return number;
        }
    }

    public static string FormatDate(DateTimeOffset value) => PdfDocumentInfo.FormatDate(value);
}
