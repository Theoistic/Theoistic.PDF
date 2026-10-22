using System.Text;
using Theoistic.PDF.Writing;
using Theoistic.PDF.Writing.Images;

namespace Theoistic.PDF.Tests;

/// <summary>
/// Covers image loading and embedding. Every PNG fixture is decoded and compared pixel for pixel
/// with the .rgba file generate.py wrote beside it, which was computed from the source values
/// rather than by any decoder.
/// </summary>
[TestClass]
public sealed class PdfImageTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Images", name);

    private static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    /// <summary>Every PNG with an oracle beside it, one test case each.</summary>
    public static IEnumerable<object[]> PngFixtures =>
        Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Images"), "*.rgba")
            .Select(path => new object[] { Path.GetFileNameWithoutExtension(path) })
            .OrderBy(row => (string)row[0], StringComparer.Ordinal);

    [TestMethod]
    [DynamicData(nameof(PngFixtures))]
    public void Png_DecodesToTheExpectedPixels(string name)
    {
        PngDecoder.Result image = PngDecoder.Decode(File.ReadAllBytes(Fixture(name + ".png")));
        byte[] expected = File.ReadAllBytes(Fixture(name + ".rgba"));
        byte[] actual = ToRgba(image);

        Assert.AreEqual(expected.Length, actual.Length, $"{name}: wrong pixel count");

        for (int i = 0; i < expected.Length; i++)
        {
            if (expected[i] != actual[i])
            {
                int pixel = i / 4;

                Assert.Fail(
                    $"{name}: pixel ({pixel % image.Width}, {pixel / image.Width}) channel {"RGBA"[i % 4]} is {actual[i]}, expected {expected[i]}");
            }
        }
    }

    [TestMethod]
    public void Png_OpaqueAlphaChannelIsDropped()
    {
        // An alpha channel that is 255 everywhere would only add a soft mask that changes nothing.
        PdfImage image = PdfImage.FromFile(Fixture("rgba8_opaque.png"));

        Assert.IsFalse(image.HasTransparency);
    }

    [TestMethod]
    [DataRow("rgba8.png")]
    [DataRow("palette8_trns.png")]
    [DataRow("rgb8_trns.png")]
    [DataRow("gray4_trns.png")]
    [DataRow("grayalpha8.png")]
    public void Png_TransparencyIsDetected(string file)
    {
        Assert.IsTrue(PdfImage.FromFile(Fixture(file)).HasTransparency);
    }

    [TestMethod]
    public void Png_ReportsItsSize()
    {
        PdfImage image = PdfImage.FromFile(Fixture("rgb8.png"));

        Assert.AreEqual(13, image.Width);
        Assert.AreEqual(7, image.Height);
    }

    [TestMethod]
    public void Png_RejectsTruncatedData()
    {
        byte[] data = File.ReadAllBytes(Fixture("rgb8.png"));

        Assert.ThrowsException<InvalidDataException>(() => PdfImage.FromBytes(data.AsSpan(0, 40).ToArray()));
    }

    // ---------------------------------------------------------------- JPEG

    [TestMethod]
    [DataRow("baseline.jpg", 3)]
    [DataRow("progressive.jpg", 3)]
    [DataRow("gray.jpg", 1)]
    [DataRow("cmyk.jpg", 4)]
    public void Jpeg_HeaderIsRead(string file, int components)
    {
        JpegInfo info = JpegInfo.Read(File.ReadAllBytes(Fixture(file)));

        Assert.AreEqual(64, info.Width);
        Assert.AreEqual(48, info.Height);
        Assert.AreEqual(components, info.Components);
        Assert.AreEqual(8, info.BitsPerComponent);
    }

    [TestMethod]
    public void Jpeg_AdobeCmykIsMarkedForInversion()
    {
        Assert.IsTrue(JpegInfo.Read(File.ReadAllBytes(Fixture("cmyk.jpg"))).AdobeInverted);
        Assert.IsFalse(JpegInfo.Read(File.ReadAllBytes(Fixture("baseline.jpg"))).AdobeInverted);
    }

    [TestMethod]
    public void Jpeg_ArithmeticCodingIsRefused()
    {
        NotSupportedException error = Assert.ThrowsException<NotSupportedException>(
            () => PdfImage.FromFile(Fixture("arithmetic.jpg")));

        Assert.IsTrue(error.Message.Contains("baseline", StringComparison.Ordinal), error.Message);
    }

    [TestMethod]
    public void Jpeg_IsEmbeddedByteForByte()
    {
        byte[] jpeg = File.ReadAllBytes(Fixture("baseline.jpg"));
        var document = new PdfDocument();

        document.AddPage(PdfPaperSize.A4).Content.DrawImage(PdfImage.FromBytes(jpeg), 50, 50, 64, 48);

        byte[] pdf = document.ToArray();

        Assert.IsTrue(pdf.AsSpan().IndexOf(jpeg) >= 0, "the JPEG was altered on the way in");
        Assert.IsTrue(Latin1(pdf).Contains("/Filter /DCTDecode", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Jpeg_CmykCarriesTheDecodeArray()
    {
        var document = new PdfDocument();

        document.AddPage(PdfPaperSize.A4).Content.DrawImage(PdfImage.FromFile(Fixture("cmyk.jpg")), 50, 50, 64, 48);

        string pdf = Latin1(document.ToArray());

        Assert.IsTrue(pdf.Contains("/ColorSpace /DeviceCMYK", StringComparison.Ordinal));
        Assert.IsTrue(pdf.Contains("/Decode [1 0 1 0 1 0 1 0]", StringComparison.Ordinal), "inverted CMYK would print as a negative");
    }

    // ---------------------------------------------------------------- formats and embedding

    [TestMethod]
    public void FromBytes_RejectsOtherFormatsClearly()
    {
        NotSupportedException error = Assert.ThrowsException<NotSupportedException>(
            () => PdfImage.FromBytes(Encoding.ASCII.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'/>")));

        Assert.IsTrue(error.Message.Contains("JPEG", StringComparison.Ordinal), error.Message);
    }

    [TestMethod]
    public void Png_WithAlphaGetsASoftMask()
    {
        var document = new PdfDocument();

        document.AddPage(PdfPaperSize.A4).Content.DrawImage(PdfImage.FromFile(Fixture("logo.png")), 50, 700, 120, 40);

        Assert.IsTrue(Latin1(document.ToArray()).Contains("/SMask", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Png_WithoutAlphaHasNoSoftMask()
    {
        var document = new PdfDocument();

        document.AddPage(PdfPaperSize.A4).Content.DrawImage(PdfImage.FromFile(Fixture("rgb8.png")), 50, 700, 13, 7);

        Assert.IsFalse(Latin1(document.ToArray()).Contains("/SMask", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Image_DrawnOnEveryPageIsStoredOnce()
    {
        PdfImage logo = PdfImage.FromFile(Fixture("rgb8.png"));
        var document = new PdfDocument();

        for (int i = 0; i < 3; i++)
        {
            document.AddPage(PdfPaperSize.A4).Content.DrawImage(logo, 50, 700, 13, 7);
        }

        int images = System.Text.RegularExpressions.Regex.Matches(Latin1(document.ToArray()), "/Subtype /Image").Count;

        Assert.AreEqual(1, images);
    }

    [TestMethod]
    public void DrawImage_PlacesAndSizesWithOneTransform()
    {
        var document = new PdfDocument();
        PdfPage page = document.AddPage(PdfPaperSize.A4);

        page.Content.DrawImage(PdfImage.FromFile(Fixture("rgb8.png")), 50, 60, 130, 70);

        Assert.AreEqual("q\n130 0 0 70 50 60 cm\n/Im1 Do\nQ\n", Latin1(page.Content.ToArray()));
    }

    [TestMethod]
    public void DrawImage_InsideATextObjectIsRejected()
    {
        var document = new PdfDocument();
        PdfPage page = document.AddPage(PdfPaperSize.A4);

        page.Content.BeginText();

        Assert.ThrowsException<InvalidOperationException>(
            () => page.Content.DrawImage(PdfImage.FromFile(Fixture("rgb8.png")), 0, 0, 10, 10));
    }

    [TestMethod]
    public void FitWithin_KeepsTheAspectRatio()
    {
        // logo.png is 240 x 80, so a square box is limited by width.
        (double width, double height) = PdfImage.FromFile(Fixture("logo.png")).FitWithin(120, 120);

        Assert.AreEqual(120, width, 1e-9);
        Assert.AreEqual(40, height, 1e-9);
    }

    [TestMethod]
    public void FitWithin_IsLimitedByWhicheverSideIsTighter()
    {
        (double width, double height) = PdfImage.FromFile(Fixture("logo.png")).FitWithin(1000, 30);

        Assert.AreEqual(90, width, 1e-9);
        Assert.AreEqual(30, height, 1e-9);
    }

    private static byte[] ToRgba(PngDecoder.Result image)
    {
        int pixels = image.Width * image.Height;
        var rgba = new byte[pixels * 4];

        for (int p = 0; p < pixels; p++)
        {
            if (image.Channels == 3)
            {
                rgba[p * 4] = image.Color[p * 3];
                rgba[(p * 4) + 1] = image.Color[(p * 3) + 1];
                rgba[(p * 4) + 2] = image.Color[(p * 3) + 2];
            }
            else
            {
                rgba[p * 4] = rgba[(p * 4) + 1] = rgba[(p * 4) + 2] = image.Color[p];
            }

            rgba[(p * 4) + 3] = image.Alpha?[p] ?? 255;
        }

        return rgba;
    }
}
