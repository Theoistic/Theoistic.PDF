namespace Theoistic.PDF.Writing.Images;

/// <summary>
/// What a PDF needs to know about a JPEG to embed it unchanged: its size, its colour layout, and
/// whether Adobe's inverted-CMYK convention applies.
/// <para>
/// Nothing is decoded. PDF's DCTDecode filter is a JPEG decoder, so the file's bytes go into the
/// document as they are - no quality loss and no decoder to maintain. Only the header markers
/// are read.
/// </para>
/// </summary>
internal sealed record JpegInfo(int Width, int Height, int Components, int BitsPerComponent, bool AdobeInverted)
{
    public static JpegInfo Read(byte[] data)
    {
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
        {
            throw new InvalidDataException("Not a JPEG file: it does not start with the SOI marker.");
        }

        bool adobe = false;
        int position = 2;

        while (position + 4 <= data.Length)
        {
            if (data[position] != 0xFF)
            {
                throw new InvalidDataException($"Corrupt JPEG: expected a marker at offset {position}.");
            }

            byte marker = data[position + 1];

            // Fill bytes may pad between markers.
            if (marker == 0xFF)
            {
                position++;
                continue;
            }

            // Markers that stand alone, with no length field.
            if (marker is 0x01 or (>= 0xD0 and <= 0xD7))
            {
                position += 2;
                continue;
            }

            int length = (data[position + 2] << 8) | data[position + 3];
            int segment = position + 4;

            if (length < 2 || position + 2 + length > data.Length)
            {
                throw new InvalidDataException($"Corrupt JPEG: a segment at offset {position} runs past the end of the file.");
            }

            switch (marker)
            {
                case 0xEE when length >= 14 && data[segment] == (byte)'A' && data[segment + 1] == (byte)'d'
                    && data[segment + 2] == (byte)'o' && data[segment + 3] == (byte)'b' && data[segment + 4] == (byte)'e':
                    adobe = true;
                    break;

                // Baseline, extended sequential and progressive: the processes PDF readers are
                // required to support.
                case 0xC0 or 0xC1 or 0xC2:
                    int precision = data[segment];
                    int height = (data[segment + 1] << 8) | data[segment + 2];
                    int width = (data[segment + 3] << 8) | data[segment + 4];
                    int components = data[segment + 5];

                    if (width == 0 || height == 0)
                    {
                        throw new InvalidDataException("The JPEG declares a zero dimension (a deferred height is not supported).");
                    }

                    if (components is not (1 or 3 or 4))
                    {
                        throw new NotSupportedException($"A JPEG with {components} colour components cannot be embedded; 1, 3 or 4 are supported.");
                    }

                    // Photoshop writes CMYK JPEGs with inverted samples and marks them with an
                    // Adobe segment. Every reader that matters follows the same convention of
                    // undoing it with a Decode array, so that is what is done here too.
                    return new JpegInfo(width, height, components, precision, adobe && components == 4);

                // Lossless, hierarchical and arithmetic-coded variants exist but PDF readers are
                // not required to decode them, so embedding one would produce a blank image in
                // some viewers.
                case 0xC3 or 0xC5 or 0xC6 or 0xC7 or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF:
                    throw new NotSupportedException(
                        "This JPEG uses a lossless, hierarchical or arithmetic-coded process that PDF readers are not required to support. Re-save it as a baseline or progressive JPEG, or as PNG.");

                case 0xDA:
                    throw new InvalidDataException("Corrupt JPEG: image data begins before any frame header.");
            }

            position += 2 + length;
        }

        throw new InvalidDataException("Corrupt JPEG: no frame header was found.");
    }
}
