using System.IO.Compression;
using Theoistic.PDF.Writing;

namespace Theoistic.PDF.Editing;

/// <summary>
/// Decodes the streams the reader itself has to look inside: cross-reference streams and object
/// streams. Both are FlateDecode in every producer worth the name, usually with a PNG predictor on
/// the cross-reference stream, so that is all that is supported. Page content is never decoded;
/// it is copied with its filters intact.
/// </summary>
internal static class StreamDecoder
{
    public static byte[] Decode(PdfStream stream, PdfFile file)
    {
        ArgumentNullException.ThrowIfNull(stream);

        PdfObject? filter = file.Resolve(stream["Filter"]);
        PdfObject? parameters = file.Resolve(stream["DecodeParms"]);

        if (filter is null)
        {
            return stream.Data;
        }

        var filters = new List<(PdfObject? Name, PdfObject? Parameters)>();

        if (filter is PdfArray array)
        {
            for (int i = 0; i < array.Count; i++)
            {
                PdfObject? itemParameters = parameters is PdfArray parameterArray && i < parameterArray.Count
                    ? file.Resolve(parameterArray[i])
                    : null;

                filters.Add((file.Resolve(array[i]), itemParameters));
            }
        }
        else
        {
            filters.Add((filter, parameters));
        }

        byte[] data = stream.Data;

        foreach ((PdfObject? name, PdfObject? itemParameters) in filters)
        {
            string filterName = (name as PdfName)?.Value ?? "?";

            if (filterName is not ("FlateDecode" or "Fl"))
            {
                throw new PdfFormatException(
                    $"A cross-reference or object stream uses the /{filterName} filter; only FlateDecode is supported there.");
            }

            data = Unpredict(Inflate(data), itemParameters as PdfDictionary, file);
        }

        return data;
    }

    /// <summary>
    /// zlib inflate. A stream whose header is missing is tried as raw deflate, and one that is
    /// truncated or fails its checksum keeps whatever decoded before the damage, which is what
    /// every viewer does with such files.
    /// </summary>
    internal static byte[] Inflate(byte[] data)
    {
        bool zlib = data.Length >= 2 && (data[0] & 0x0F) == 8 && (((data[0] << 8) | data[1]) % 31) == 0;

        using var input = new MemoryStream(data, writable: false);
        using Stream inflater = zlib
            ? new ZLibStream(input, CompressionMode.Decompress)
            : new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();

        byte[] buffer = new byte[16384];

        try
        {
            int read;

            while ((read = inflater.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, read);
            }
        }
        catch (InvalidDataException exception)
        {
            if (output.Length == 0)
            {
                throw new PdfFormatException("A compressed stream could not be decoded.", exception);
            }
        }

        return output.ToArray();
    }

    /// <summary>Reverses a TIFF or PNG predictor (7.4.4.4).</summary>
    private static byte[] Unpredict(byte[] data, PdfDictionary? parameters, PdfFile file)
    {
        int predictor = Integer(parameters, "Predictor", 1, file);

        if (predictor <= 1)
        {
            return data;
        }

        int colors = Integer(parameters, "Colors", 1, file);
        int bitsPerComponent = Integer(parameters, "BitsPerComponent", 8, file);
        int columns = Integer(parameters, "Columns", 1, file);

        if (colors < 1 || columns < 1 || bitsPerComponent is not (1 or 2 or 4 or 8 or 16))
        {
            throw new PdfFormatException("A stream's predictor parameters are invalid.");
        }

        int bytesPerPixel = Math.Max(1, ((colors * bitsPerComponent) + 7) / 8);
        int rowLength = ((colors * bitsPerComponent * columns) + 7) / 8;

        return predictor == 2
            ? UndoTiff(data, rowLength, bytesPerPixel, bitsPerComponent)
            : UndoPng(data, rowLength, bytesPerPixel);
    }

    private static byte[] UndoPng(byte[] data, int rowLength, int bytesPerPixel)
    {
        using var output = new MemoryStream(data.Length);

        byte[] previous = new byte[rowLength];
        byte[] current = new byte[rowLength];

        // Every row carries its own filter type in its first byte, whichever PNG predictor value
        // the dictionary names, so 10 through 15 all decode the same way.
        for (int offset = 0; offset < data.Length; offset += rowLength + 1)
        {
            int type = data[offset];
            int available = Math.Min(rowLength, data.Length - offset - 1);

            if (available <= 0)
            {
                break;
            }

            Array.Clear(current);
            Array.Copy(data, offset + 1, current, 0, available);

            for (int i = 0; i < rowLength; i++)
            {
                int left = i >= bytesPerPixel ? current[i - bytesPerPixel] : 0;
                int up = previous[i];
                int upLeft = i >= bytesPerPixel ? previous[i - bytesPerPixel] : 0;

                current[i] = (byte)(type switch
                {
                    0 => current[i],
                    1 => current[i] + left,
                    2 => current[i] + up,
                    3 => current[i] + ((left + up) >> 1),
                    4 => current[i] + Paeth(left, up, upLeft),
                    _ => throw new PdfFormatException($"A stream uses PNG filter type {type}, which does not exist.")
                });
            }

            output.Write(current, 0, available);

            (previous, current) = (current, previous);
        }

        return output.ToArray();
    }

    private static byte[] UndoTiff(byte[] data, int rowLength, int bytesPerPixel, int bitsPerComponent)
    {
        if (bitsPerComponent != 8)
        {
            throw new PdfFormatException("Only 8-bit TIFF predictors are supported in cross-reference and object streams.");
        }

        byte[] output = (byte[])data.Clone();

        for (int row = 0; row < output.Length; row += rowLength)
        {
            int rowEnd = Math.Min(output.Length, row + rowLength);

            for (int i = row + bytesPerPixel; i < rowEnd; i++)
            {
                output[i] = (byte)(output[i] + output[i - bytesPerPixel]);
            }
        }

        return output;
    }

    private static int Paeth(int left, int up, int upLeft)
    {
        int estimate = left + up - upLeft;
        int toLeft = Math.Abs(estimate - left);
        int toUp = Math.Abs(estimate - up);
        int toUpLeft = Math.Abs(estimate - upLeft);

        if (toLeft <= toUp && toLeft <= toUpLeft)
        {
            return left;
        }

        return toUp <= toUpLeft ? up : upLeft;
    }

    private static int Integer(PdfDictionary? dictionary, string key, int fallback, PdfFile file) =>
        file.Resolve(dictionary?[key]) is PdfInteger { Value: >= int.MinValue and <= int.MaxValue } value
            ? (int)value.Value
            : fallback;
}
