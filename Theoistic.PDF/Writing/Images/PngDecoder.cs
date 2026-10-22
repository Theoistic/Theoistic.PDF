using System.IO.Compression;

namespace Theoistic.PDF.Writing.Images;

/// <summary>
/// Decodes a PNG into 8-bit samples plus an optional alpha channel.
/// <para>
/// A PNG cannot be passed straight through the way a JPEG can, because PDF has no way to carry
/// an alpha channel inside an image: transparency has to become a separate soft-mask image. So
/// the file is decoded fully - every colour type, bit depth and filter, and Adam7 interlacing -
/// and then re-compressed with Flate when the document is written. The decompression itself is
/// the BCL's zlib, so no codec is shipped.
/// </para>
/// </summary>
internal static class PngDecoder
{
    // A logo has no business being larger than this, and the cap stops a small hostile file
    // that declares enormous dimensions from exhausting memory when it is expanded.
    private const long MaxPixels = 100_000_000;

    private static readonly byte[] Signature = { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>A decoded image: one or three 8-bit channels, and alpha if any pixel is not opaque.</summary>
    public sealed record Result(int Width, int Height, int Channels, byte[] Color, byte[]? Alpha);

    public static Result Decode(byte[] data)
    {
        if (data.Length < Signature.Length || !data.AsSpan(0, Signature.Length).SequenceEqual(Signature))
        {
            throw new InvalidDataException("Not a PNG file: the signature does not match.");
        }

        int width = 0;
        int height = 0;
        int bitDepth = 0;
        int colorType = -1;
        int interlace = 0;
        byte[]? palette = null;
        byte[]? transparency = null;
        using var compressed = new MemoryStream();

        int position = Signature.Length;

        while (position + 8 <= data.Length)
        {
            int length = (int)Fonts.BigEndian.U32(data, position);
            string type = Fonts.BigEndian.Tag(data, position + 4);
            int body = position + 8;

            if (length < 0 || body + length + 4 > data.Length)
            {
                throw new InvalidDataException($"Corrupt PNG: the '{type}' chunk runs past the end of the file.");
            }

            switch (type)
            {
                case "IHDR":
                    width = (int)Fonts.BigEndian.U32(data, body);
                    height = (int)Fonts.BigEndian.U32(data, body + 4);
                    bitDepth = data[body + 8];
                    colorType = data[body + 9];
                    interlace = data[body + 12];
                    break;

                case "PLTE":
                    palette = data.AsSpan(body, length).ToArray();
                    break;

                case "tRNS":
                    transparency = data.AsSpan(body, length).ToArray();
                    break;

                case "IDAT":
                    compressed.Write(data, body, length);
                    break;

                case "IEND":
                    position = data.Length;
                    continue;
            }

            position = body + length + 4;
        }

        Validate(width, height, bitDepth, colorType, interlace, palette);

        byte[] raw = Inflate(compressed.ToArray());
        int channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };

        // Samples at their native depth, one array element per sample, unfiltered and with
        // interlacing undone.
        int[] samples = interlace == 1
            ? DeinterlaceAdam7(raw, width, height, channels, bitDepth)
            : Unfilter(raw, 0, width, height, channels, bitDepth, out _);

        return ToEightBit(samples, width, height, colorType, bitDepth, palette, transparency);
    }

    private static void Validate(int width, int height, int bitDepth, int colorType, int interlace, byte[]? palette)
    {
        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException("Corrupt PNG: missing header or zero dimension.");
        }

        if ((long)width * height > MaxPixels)
        {
            throw new NotSupportedException($"The PNG is {width} x {height}, larger than the {MaxPixels:N0}-pixel limit for an embedded image.");
        }

        bool validDepth = colorType switch
        {
            0 => bitDepth is 1 or 2 or 4 or 8 or 16,
            3 => bitDepth is 1 or 2 or 4 or 8,
            2 or 4 or 6 => bitDepth is 8 or 16,
            _ => false
        };

        if (!validDepth)
        {
            throw new InvalidDataException($"Corrupt PNG: colour type {colorType} with bit depth {bitDepth} is not a valid combination.");
        }

        if (interlace is not (0 or 1))
        {
            throw new InvalidDataException($"Corrupt PNG: unknown interlace method {interlace}.");
        }

        if (colorType == 3 && palette is null)
        {
            throw new InvalidDataException("Corrupt PNG: a palette image has no palette.");
        }
    }

    private static byte[] Inflate(byte[] compressed)
    {
        using var input = new MemoryStream(compressed);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();

        zlib.CopyTo(output);

        return output.ToArray();
    }

    /// <summary>
    /// Reverses the per-row filters for one image (or one interlace pass) starting at
    /// <paramref name="offset"/>, returning its samples and how many bytes it consumed.
    /// </summary>
    private static int[] Unfilter(byte[] raw, int offset, int width, int height, int channels, int bitDepth, out int consumed)
    {
        int bitsPerPixel = channels * bitDepth;
        int stride = (int)(((long)width * bitsPerPixel + 7) / 8);

        // Filters work on whole bytes, comparing each with the byte one pixel to its left; for
        // sub-byte pixels that distance is one byte.
        int bytesPerPixel = Math.Max(1, bitsPerPixel / 8);

        consumed = (stride + 1) * height;

        if (offset + consumed > raw.Length)
        {
            throw new InvalidDataException("Corrupt PNG: the image data is shorter than its dimensions require.");
        }

        var current = new byte[stride];
        var previous = new byte[stride];
        var samples = new int[width * height * channels];
        int sample = 0;

        for (int row = 0; row < height; row++)
        {
            int start = offset + (row * (stride + 1));
            int filter = raw[start];

            for (int i = 0; i < stride; i++)
            {
                int x = raw[start + 1 + i];
                int a = i >= bytesPerPixel ? current[i - bytesPerPixel] : 0;
                int b = previous[i];
                int c = i >= bytesPerPixel ? previous[i - bytesPerPixel] : 0;

                current[i] = (byte)(filter switch
                {
                    0 => x,
                    1 => x + a,
                    2 => x + b,
                    3 => x + ((a + b) / 2),
                    4 => x + Paeth(a, b, c),
                    _ => throw new InvalidDataException($"Corrupt PNG: unknown row filter {filter}.")
                });
            }

            sample = UnpackRow(current, width * channels, bitDepth, samples, sample);

            (previous, current) = (current, previous);
        }

        return samples;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);

        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static int UnpackRow(byte[] row, int count, int bitDepth, int[] samples, int index)
    {
        switch (bitDepth)
        {
            case 8:
                for (int i = 0; i < count; i++)
                {
                    samples[index++] = row[i];
                }

                break;

            case 16:
                for (int i = 0; i < count; i++)
                {
                    samples[index++] = (row[i * 2] << 8) | row[(i * 2) + 1];
                }

                break;

            default:
                int perByte = 8 / bitDepth;
                int mask = (1 << bitDepth) - 1;

                for (int i = 0; i < count; i++)
                {
                    int shift = 8 - (((i % perByte) + 1) * bitDepth);
                    samples[index++] = (row[i / perByte] >> shift) & mask;
                }

                break;
        }

        return index;
    }

    /// <summary>
    /// Adam7 stores seven reduced images, each a sparser grid over the full one. Each pass is
    /// unfiltered on its own and its pixels scattered into place.
    /// </summary>
    private static int[] DeinterlaceAdam7(byte[] raw, int width, int height, int channels, int bitDepth)
    {
        ReadOnlySpan<int> startX = stackalloc[] { 0, 4, 0, 2, 0, 1, 0 };
        ReadOnlySpan<int> startY = stackalloc[] { 0, 0, 4, 0, 2, 0, 1 };
        ReadOnlySpan<int> stepX = stackalloc[] { 8, 8, 4, 4, 2, 2, 1 };
        ReadOnlySpan<int> stepY = stackalloc[] { 8, 8, 8, 4, 4, 2, 2 };

        var samples = new int[width * height * channels];
        int offset = 0;

        for (int pass = 0; pass < 7; pass++)
        {
            int passWidth = (width - startX[pass] + stepX[pass] - 1) / stepX[pass];
            int passHeight = (height - startY[pass] + stepY[pass] - 1) / stepY[pass];

            // A small image can leave some passes empty, and an empty pass has no rows at all,
            // not even filter bytes.
            if (passWidth <= 0 || passHeight <= 0)
            {
                continue;
            }

            int[] passSamples = Unfilter(raw, offset, passWidth, passHeight, channels, bitDepth, out int consumed);
            offset += consumed;

            for (int y = 0; y < passHeight; y++)
            {
                for (int x = 0; x < passWidth; x++)
                {
                    int target = ((((startY[pass] + (y * stepY[pass])) * width) + startX[pass] + (x * stepX[pass])) * channels);
                    int source = ((y * passWidth) + x) * channels;

                    Array.Copy(passSamples, source, samples, target, channels);
                }
            }
        }

        return samples;
    }

    /// <summary>
    /// Converts native-depth samples to 8-bit grey or RGB plus alpha. Palettes are expanded and
    /// tRNS transparency becomes alpha, so the PDF side only ever deals with two simple layouts.
    /// </summary>
    private static Result ToEightBit(int[] samples, int width, int height, int colorType, int bitDepth, byte[]? palette, byte[]? transparency)
    {
        int pixels = width * height;
        int maxValue = (1 << bitDepth) - 1;
        bool isColor = colorType is 2 or 3 or 6;
        int outChannels = isColor ? 3 : 1;

        var color = new byte[pixels * outChannels];
        var alpha = new byte[pixels];
        bool anyTransparent = false;

        int? transparentGray = colorType == 0 && transparency is { Length: >= 2 } ? (transparency[0] << 8) | transparency[1] : null;
        (int R, int G, int B)? transparentRgb = colorType == 2 && transparency is { Length: >= 6 }
            ? ((transparency[0] << 8) | transparency[1], (transparency[2] << 8) | transparency[3], (transparency[4] << 8) | transparency[5])
            : null;

        for (int p = 0; p < pixels; p++)
        {
            byte a = 255;

            switch (colorType)
            {
                case 0:
                    int gray = samples[p];
                    color[p] = Scale(gray, maxValue);

                    if (gray == transparentGray)
                    {
                        a = 0;
                    }

                    break;

                case 2:
                    int r = samples[p * 3];
                    int g = samples[(p * 3) + 1];
                    int b = samples[(p * 3) + 2];

                    color[p * 3] = Scale(r, maxValue);
                    color[(p * 3) + 1] = Scale(g, maxValue);
                    color[(p * 3) + 2] = Scale(b, maxValue);

                    if (transparentRgb is { } t && r == t.R && g == t.G && b == t.B)
                    {
                        a = 0;
                    }

                    break;

                case 3:
                    int index = samples[p];

                    if ((index * 3) + 2 >= palette!.Length)
                    {
                        throw new InvalidDataException($"Corrupt PNG: palette index {index} is past the end of the palette.");
                    }

                    color[p * 3] = palette[index * 3];
                    color[(p * 3) + 1] = palette[(index * 3) + 1];
                    color[(p * 3) + 2] = palette[(index * 3) + 2];

                    // Palette transparency lists alpha per entry; entries past its end are opaque.
                    if (transparency is not null && index < transparency.Length)
                    {
                        a = transparency[index];
                    }

                    break;

                case 4:
                    color[p] = Scale(samples[p * 2], maxValue);
                    a = Scale(samples[(p * 2) + 1], maxValue);
                    break;

                case 6:
                    color[p * 3] = Scale(samples[p * 4], maxValue);
                    color[(p * 3) + 1] = Scale(samples[(p * 4) + 1], maxValue);
                    color[(p * 3) + 2] = Scale(samples[(p * 4) + 2], maxValue);
                    a = Scale(samples[(p * 4) + 3], maxValue);
                    break;
            }

            alpha[p] = a;
            anyTransparent |= a != 255;
        }

        // An alpha channel that is opaque everywhere is dropped, so that a fully opaque PNG with
        // an alpha channel costs no more than one without.
        return new Result(width, height, outChannels, color, anyTransparent ? alpha : null);
    }

    private static byte Scale(int value, int maxValue) =>
        maxValue == 255 ? (byte)value : (byte)((value * 255 + (maxValue / 2)) / maxValue);
}
