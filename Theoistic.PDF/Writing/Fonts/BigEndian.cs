namespace Theoistic.PDF.Writing.Fonts;

/// <summary>
/// Reads and writes the big-endian integers that every OpenType table is built from.
/// <para>
/// Reads are bounds-checked and throw <see cref="InvalidDataException"/>, so that a truncated or
/// corrupt font file reports itself as a bad font rather than as an index error from deep inside
/// a table parser.
/// </para>
/// </summary>
internal static class BigEndian
{
    public static ushort U16(byte[] data, int offset)
    {
        Require(data, offset, 2);

        return (ushort)((data[offset] << 8) | data[offset + 1]);
    }

    public static short I16(byte[] data, int offset) => (short)U16(data, offset);

    public static uint U32(byte[] data, int offset)
    {
        Require(data, offset, 4);

        return ((uint)data[offset] << 24)
            | ((uint)data[offset + 1] << 16)
            | ((uint)data[offset + 2] << 8)
            | data[offset + 3];
    }

    public static int I32(byte[] data, int offset) => (int)U32(data, offset);

    public static string Tag(byte[] data, int offset)
    {
        Require(data, offset, 4);

        return string.Create(4, (data, offset), static (span, state) =>
        {
            for (int i = 0; i < 4; i++)
            {
                span[i] = (char)state.data[state.offset + i];
            }
        });
    }

    public static void Write16(byte[] data, int offset, int value)
    {
        data[offset] = (byte)(value >> 8);
        data[offset + 1] = (byte)value;
    }

    public static void Write32(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }

    public static void Require(byte[] data, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > data.Length - length)
        {
            throw new InvalidDataException(
                $"The font file is truncated or corrupt: a read of {length} byte(s) at offset {offset} runs past its {data.Length} bytes.");
        }
    }
}
