using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Theoistic.PDF.Tests;

/// <summary>
/// Builds small PDFs byte by byte for the editing tests, so that the structures the library
/// itself never writes - rotated and cropped pages, inherited resources, cross-reference streams,
/// object streams, hybrid files - can be read without shipping third-party files.
/// </summary>
internal static class PdfFixtures
{
    /// <summary>
    /// A file with a classic cross-reference table, its objects numbered from 1 in the order
    /// given. Object 1 must be the catalog.
    /// </summary>
    public static byte[] Classic(IReadOnlyList<string> objects, string trailerExtra = "")
    {
        using var output = new MemoryStream();

        Write(output, "%PDF-1.7\n%âãÏÓ\n");

        var offsets = new long[objects.Count + 1];

        for (int i = 0; i < objects.Count; i++)
        {
            offsets[i + 1] = output.Position;
            Write(output, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        long xref = output.Position;
        var table = new StringBuilder($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");

        for (int i = 1; i <= objects.Count; i++)
        {
            table.Append(offsets[i].ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }

        Write(output, table.ToString());
        Write(output, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R {trailerExtra}>>\nstartxref\n{xref}\n%%EOF\n");

        return output.ToArray();
    }

    /// <summary>An uncompressed stream object body.</summary>
    public static string Stream(string content, string extra = "") =>
        $"<< /Length {content.Length} {extra}>>\nstream\n{content}\nendstream";

    /// <summary>A one-page document whose page tree root carries /Rotate 90 and the shared resources.</summary>
    public static byte[] RotatedPage() => Classic(new[]
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 /Rotate 90 /MediaBox [0 0 595 842] /Resources 5 0 R >>",
        "<< /Type /Page /Parent 2 0 R /Contents 4 0 R >>",
        Stream("BT /F1 12 Tf 72 720 Td (Hello) Tj ET"),
        "<< /Font << /F1 6 0 R >> /XObject << /TheoisticOverlay1 7 0 R >> >>",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        Stream("0 0 m 1 1 l S", "/Type /XObject /Subtype /Form /BBox [0 0 1 1] "),
    });

    /// <summary>A one-page document with a crop box offset from the media box's origin.</summary>
    public static byte[] CroppedPage() => Classic(new[]
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 800] /CropBox [50 60 550 760] /Contents [4 0 R 5 0 R] /Resources << >> >>",
        Stream("0 0 1 rg"),
        Stream("100 100 50 50 re f"),
    });

    /// <summary>
    /// A PDF 1.5 file whose catalog, page tree and page live in a compressed object stream, located
    /// through a cross-reference stream with a PNG Up predictor. The page's content stream has an
    /// indirect /Length.
    /// <para>
    /// With <paramref name="hybrid"/>, a classic table follows instead, listing the compressed
    /// objects as free and pointing at the stream with /XRefStm, the way Microsoft Word writes.
    /// </para>
    /// </summary>
    public static byte[] ObjectStreams(bool hybrid = false)
    {
        string[] packed =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 300] /Contents 4 0 R /Resources << >> >>",
        };

        var header = new StringBuilder();
        var body = new StringBuilder();

        for (int i = 0; i < packed.Length; i++)
        {
            header.Append(CultureInfo.InvariantCulture, $"{i + 1} {body.Length} ");
            body.Append(packed[i]).Append('\n');
        }

        byte[] objectStream = Deflate(Encoding.Latin1.GetBytes(header.ToString() + body));
        const string content = "0 0 1 rg 10 10 100 50 re f";

        using var output = new MemoryStream();

        Write(output, "%PDF-1.5\n%âãÏÓ\n");

        long object4 = output.Position;
        Write(output, $"4 0 obj\n<< /Length 6 0 R >>\nstream\n{content}\nendstream\nendobj\n");

        long object5 = output.Position;
        Write(output, $"5 0 obj\n<< /Type /ObjStm /N 3 /First {header.Length} /Filter /FlateDecode /Length {objectStream.Length} >>\nstream\n");
        output.Write(objectStream);
        Write(output, "\nendstream\nendobj\n");

        long object6 = output.Position;
        Write(output, $"6 0 obj\n{content.Length}\nendobj\n");

        long object7 = output.Position;

        // Type, a two-byte field and a two-byte field, per entry.
        var rows = new List<int[]>();

        if (!hybrid)
        {
            rows.Add(new[] { 0, 0, 65535 });
        }

        rows.Add(new[] { 2, 5, 0 });
        rows.Add(new[] { 2, 5, 1 });
        rows.Add(new[] { 2, 5, 2 });

        if (!hybrid)
        {
            rows.Add(new[] { 1, (int)object4, 0 });
            rows.Add(new[] { 1, (int)object5, 0 });
            rows.Add(new[] { 1, (int)object6, 0 });
            rows.Add(new[] { 1, (int)object7, 0 });
        }

        byte[] xrefData = Deflate(PngUp(rows.Select(row => new byte[]
        {
            (byte)row[0], (byte)(row[1] >> 8), (byte)row[1], (byte)(row[2] >> 8), (byte)row[2],
        }).ToList()));

        string index = hybrid ? "/Index [1 3] " : string.Empty;

        Write(output, $"7 0 obj\n<< /Type /XRef /Size 8 {index}/W [1 2 2] /Root 1 0 R /Filter /FlateDecode /DecodeParms << /Predictor 12 /Columns 5 >> /Length {xrefData.Length} >>\nstream\n");
        output.Write(xrefData);
        Write(output, "\nendstream\nendobj\n");

        if (!hybrid)
        {
            Write(output, $"startxref\n{object7}\n%%EOF\n");

            return output.ToArray();
        }

        long table = output.Position;

        Write(output,
            "xref\n0 8\n" +
            "0000000000 65535 f \n" +
            "0000000000 65535 f \n" +
            "0000000000 65535 f \n" +
            "0000000000 65535 f \n" +
            $"{object4:D10} 00000 n \n" +
            $"{object5:D10} 00000 n \n" +
            $"{object6:D10} 00000 n \n" +
            $"{object7:D10} 00000 n \n" +
            $"trailer\n<< /Size 8 /Root 1 0 R /XRefStm {object7} >>\nstartxref\n{table}\n%%EOF\n");

        return output.ToArray();
    }

    /// <summary>Applies the PNG Up filter to each row, prefixing each with its filter type.</summary>
    private static byte[] PngUp(List<byte[]> rows)
    {
        var output = new List<byte>();
        byte[] previous = new byte[rows[0].Length];

        foreach (byte[] row in rows)
        {
            output.Add(2);

            for (int i = 0; i < row.Length; i++)
            {
                output.Add((byte)(row[i] - previous[i]));
            }

            previous = row;
        }

        return output.ToArray();
    }

    public static byte[] Deflate(byte[] data)
    {
        using var buffer = new MemoryStream();

        using (var deflate = new ZLibStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(data);
        }

        return buffer.ToArray();
    }

    /// <summary>Replaces the only occurrence of <paramref name="find"/>, failing if it is not exactly one.</summary>
    public static byte[] ReplaceOnce(byte[] pdf, string find, string replacement)
    {
        string text = Encoding.Latin1.GetString(pdf);
        int at = text.IndexOf(find, StringComparison.Ordinal);

        if (at < 0 || text.IndexOf(find, at + 1, StringComparison.Ordinal) >= 0)
        {
            throw new InvalidOperationException($"'{find}' does not occur exactly once.");
        }

        return Encoding.Latin1.GetBytes(text[..at] + replacement + text[(at + find.Length)..]);
    }

    private static void Write(Stream output, string text) => output.Write(Encoding.Latin1.GetBytes(text));
}
