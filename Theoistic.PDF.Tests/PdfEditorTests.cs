using System.Text;
using System.Text.RegularExpressions;
using Theoistic.PDF.Editing;
using Theoistic.PDF.Layout;
using Theoistic.PDF.Writing;

namespace Theoistic.PDF.Tests;

/// <summary>
/// Covers opening existing PDFs and extending them as incremental updates: every way the format
/// locates objects, the geometry of rotated and cropped pages, the transform that puts an overlay
/// where the caller drew it, and appending pages without disturbing the original bytes.
/// </summary>
[TestClass]
public sealed class PdfEditorTests
{
    private static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    private static string FontFixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Fonts", name);

    private static string ImageFixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Images", name);

    /// <summary>The bytes an update added after the original.</summary>
    private static string UpdateOf(byte[] original, byte[] saved)
    {
        CollectionAssert.AreEqual(original, saved.Take(original.Length).ToArray(), "the original bytes are not an exact prefix of the saved file");

        return Latin1(saved[original.Length..]);
    }

    private static byte[] TwoPageDocument()
    {
        var document = new PdfDocument();

        document.AddPage(PdfPaperSize.A4).Content.DrawText("First", StandardFont.Helvetica, 12, 72, 720);
        document.AddPage(PdfPaperSize.Letter, PdfOrientation.Landscape).Content.DrawText("Second", StandardFont.Helvetica, 12, 72, 500);

        return document.ToArray();
    }

    private static PdfDictionary Page(PdfFile file, int index) => PageTree.Read(file)[index].Dictionary;

    // ---------------------------------------------------------------- reading

    [TestMethod]
    public void Open_ReadsTheLibrarysOwnOutput()
    {
        PdfEditor editor = PdfEditor.Open(TwoPageDocument());

        Assert.AreEqual(2, editor.Pages.Count);
        Assert.AreEqual(new PdfPageInfo(0, 595, 842, 0), editor.Pages[0]);
        Assert.AreEqual(new PdfPageInfo(1, 792, 612, 0), editor.Pages[1]);
    }

    [TestMethod]
    public void Open_ReadsCrossReferenceAndObjectStreams()
    {
        byte[] pdf = PdfFixtures.ObjectStreams();
        PdfFile file = PdfFile.Open(pdf);

        Assert.IsTrue(file.LastSectionIsStream);
        Assert.IsFalse(file.Reconstructed, "the cross-reference stream should have been read, not scanned around");
        Assert.AreEqual(XrefKind.Compressed, file.Entries[3].Kind);

        // The page's content stream has an indirect /Length, which has to be resolved to find
        // where its data ends.
        var content = (PdfStream)file.Resolve(Page(file, 0)["Contents"])!;

        Assert.AreEqual("0 0 1 rg 10 10 100 50 re f", Encoding.ASCII.GetString(content.Data));
        Assert.AreEqual(new PdfPageInfo(0, 400, 300, 0), PdfEditor.Open(pdf).Pages[0]);
    }

    [TestMethod]
    public void Open_ReadsHybridFiles()
    {
        byte[] pdf = PdfFixtures.ObjectStreams(hybrid: true);
        PdfFile file = PdfFile.Open(pdf);

        // The table lists the compressed objects as free; /XRefStm has the real entries.
        Assert.IsFalse(file.LastSectionIsStream);
        Assert.IsFalse(file.Reconstructed);
        Assert.AreEqual(XrefKind.Compressed, file.Entries[1].Kind);
        Assert.AreEqual(1, PdfEditor.Open(pdf).Pages.Count);
    }

    [TestMethod]
    public void Open_BrokenCrossReference_FallsBackToScanning()
    {
        byte[] original = TwoPageDocument();
        byte[] broken = PdfFixtures.ReplaceOnce(original, "xref\n0 ", "xrex\n0 ");

        PdfFile file = PdfFile.Open(broken);

        Assert.IsTrue(file.Reconstructed);
        Assert.AreEqual(2, PdfEditor.Open(broken).Pages.Count);
    }

    [TestMethod]
    public void Open_CrossReferenceOffsetsWrong_FindsObjectsAnyway()
    {
        // Every offset in the table off by a few bytes, which a careless producer can do.
        byte[] original = TwoPageDocument();
        string text = Latin1(original);
        string shifted = Regex.Replace(text, @"(?m)^(\d{10}) 00000 n", match =>
            (long.Parse(match.Groups[1].Value) + 3).ToString("D10") + " 00000 n");

        PdfEditor editor = PdfEditor.Open(Encoding.Latin1.GetBytes(shifted));

        Assert.AreEqual(2, editor.Pages.Count);
        Assert.AreEqual(842, editor.Pages[0].Height);
    }

    [TestMethod]
    public void Open_BrokenCrossReference_SaveWritesACompleteSection()
    {
        byte[] broken = PdfFixtures.ReplaceOnce(TwoPageDocument(), "xref\n0 ", "xrex\n0 ");
        PdfEditor editor = PdfEditor.Open(broken);

        editor.DrawOn(1).Content.DrawText("Added", StandardFont.Helvetica, 10, 20, 20);

        byte[] saved = editor.Save();
        string update = UpdateOf(broken, saved);

        // The old sections are what was broken, so the update lists every object itself and does
        // not chain back to them.
        StringAssert.Contains(update, "xref\n0 ");
        Assert.IsFalse(update.Contains("/Prev", StringComparison.Ordinal), "an update to a repaired file must not chain to the broken section");

        PdfFile reopened = PdfFile.Open(saved);

        Assert.IsFalse(reopened.Reconstructed, "the saved file should be readable without repair");
        Assert.AreEqual(2, PageTree.Read(reopened).Count);
    }

    [TestMethod]
    public void Open_Encrypted_Throws()
    {
        byte[] encrypted = PdfFixtures.ReplaceOnce(TwoPageDocument(), "/Root", "/Encrypt 99 0 R/Root");

        PdfFormatException error = Assert.ThrowsException<PdfFormatException>(() => PdfEditor.Open(encrypted));

        StringAssert.Contains(error.Message, "encrypted");
    }

    [TestMethod]
    public void Open_NotAPdf_Throws()
    {
        Assert.ThrowsException<PdfFormatException>(() => PdfEditor.Open(Encoding.ASCII.GetBytes("<html>not a pdf</html>")));
        Assert.ThrowsException<PdfFormatException>(() => PdfEditor.Open(Array.Empty<byte>()));
        Assert.ThrowsException<PdfFormatException>(() => PdfEditor.Open(Encoding.ASCII.GetBytes("%PDF-1.7\ngarbage with no objects at all\n%%EOF")));
    }

    [TestMethod]
    public void Parser_RoundTripsNumbersAndNamesExactly()
    {
        // A real with more than five decimals, and a name whose bytes are not UTF-8: both have to
        // be written back exactly as read, or a copied font matrix drifts and a resource name no
        // longer matches the content stream that uses it.
        byte[] source = Encoding.Latin1.GetBytes("[0.000488281 /Caf#E9 /Plain -.5 3.]");
        PdfObject value = new PdfParser(source).ReadObject();

        using var buffer = new MemoryStream();
        new PdfWriter(buffer).WriteObject(value);

        Assert.AreEqual("[0.000488281 /Caf#E9 /Plain -.5 3.]", Latin1(buffer.ToArray()));
    }

    // ---------------------------------------------------------------- drawing on pages

    [TestMethod]
    public void DrawOn_UnrotatedPage_MapsWithAnIdentityTransform()
    {
        byte[] original = TwoPageDocument();
        PdfEditor editor = PdfEditor.Open(original);

        PdfPage surface = editor.DrawOn(0);

        Assert.AreEqual(595, surface.Width);
        Assert.AreEqual(842, surface.Height);

        surface.Content.DrawText("Signed", StandardFont.Helvetica, 12, 100, 100);

        string update = UpdateOf(original, editor.Save());

        StringAssert.Contains(update, "\nQ\nq\n1 0 0 1 0 0 cm\n/TheoisticOverlay1 Do\nQ\n");
    }

    [TestMethod]
    public void DrawOn_RotatedPage_ReportsDisplayedSizeAndRotatesTheOverlay()
    {
        byte[] original = PdfFixtures.RotatedPage();
        PdfEditor editor = PdfEditor.Open(original);

        // /Rotate 90 on a portrait media box displays landscape, inherited from the tree root.
        Assert.AreEqual(new PdfPageInfo(0, 842, 595, 90), editor.Pages[0]);

        PdfPage surface = editor.DrawOn(0);

        Assert.AreEqual(842, surface.Width);
        Assert.AreEqual(595, surface.Height);

        surface.Content.Rectangle(10, 10, 100, 20).Fill();

        byte[] saved = editor.Save();
        string update = UpdateOf(original, saved);

        // The displayed origin is the bottom-right corner of the media box in user space.
        StringAssert.Contains(update, "0 1 -1 0 595 0 cm");

        // The shared resources already use TheoisticOverlay1, so the overlay takes the next name.
        StringAssert.Contains(update, "/TheoisticOverlay2 Do");

        // The shared resource dictionary (object 5) is copied onto the page, never rewritten.
        Assert.IsFalse(Regex.IsMatch(update, @"(?m)^5 0 obj"), "the shared resource dictionary was modified in place");
        StringAssert.Matches(update, new Regex(@"(?m)^3 0 obj"));

        PdfFile reopened = PdfFile.Open(saved);
        PdfDictionary page = Page(reopened, 0);
        var resources = (PdfDictionary)page["Resources"]!;
        var xobjects = (PdfDictionary)resources["XObject"]!;

        Assert.IsTrue(xobjects.ContainsKey("TheoisticOverlay1"), "the page lost its existing XObject");
        Assert.IsTrue(xobjects.ContainsKey("TheoisticOverlay2"));
        Assert.IsNotNull(reopened.Resolve(((PdfDictionary)resources["Font"]!)["F1"]), "the page lost its inherited font");

        var contents = (PdfArray)page["Contents"]!;

        Assert.AreEqual(3, contents.Count, "contents should be [q, original, overlay]");
        Assert.AreEqual(4, ((PdfReference)contents[1]).ObjectNumber, "the original content stream should be referenced, not copied");
    }

    [TestMethod]
    public void DrawOn_CroppedPage_OffsetsByTheCropBox()
    {
        byte[] original = PdfFixtures.CroppedPage();
        PdfEditor editor = PdfEditor.Open(original);

        Assert.AreEqual(new PdfPageInfo(0, 500, 700, 0), editor.Pages[0]);

        editor.DrawOn(0).Content.Rectangle(0, 0, 10, 10).Fill();

        string update = UpdateOf(original, editor.Save());

        StringAssert.Contains(update, "1 0 0 1 50 60 cm");
    }

    [TestMethod]
    [DataRow(180, "-1 0 0 -1 550 760 cm")]
    [DataRow(270, "0 -1 1 0 50 760 cm")]
    [DataRow(-90, "0 -1 1 0 50 760 cm")]
    [DataRow(450, "0 1 -1 0 550 60 cm")]
    public void DrawOn_OtherRotations_MapTheDisplayedOrigin(int rotate, string expected)
    {
        byte[] original = PdfFixtures.ReplaceOnce(PdfFixtures.CroppedPage(), "/CropBox", $"/Rotate {rotate} /CropBox");
        PdfEditor editor = PdfEditor.Open(original);

        editor.DrawOn(0).Content.Rectangle(0, 0, 10, 10).Fill();

        StringAssert.Contains(UpdateOf(original, editor.Save()), expected);
    }

    [TestMethod]
    public void DrawOn_SamePageTwice_ReturnsTheSameSurface()
    {
        PdfEditor editor = PdfEditor.Open(TwoPageDocument());

        Assert.AreSame(editor.DrawOn(1), editor.DrawOn(1));
        Assert.AreNotSame(editor.DrawOn(0), editor.DrawOn(1));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => editor.DrawOn(2));
    }

    [TestMethod]
    public void DrawOn_EmbedsFontsAndTransparentImages()
    {
        byte[] original = TwoPageDocument();
        PdfEditor editor = PdfEditor.Open(original);
        var font = TrueTypeFont.Load(FontFixture("TestSans.ttf"));

        PdfPage surface = editor.DrawOn(0);

        surface.Content.DrawText("ABŁá", font, 14, 72, 120);
        surface.Content.DrawImage(PdfImage.FromFile(ImageFixture("rgba8.png")), 72, 140, 60, 30);

        byte[] saved = editor.Save();
        PdfFile reopened = PdfFile.Open(saved);
        var resources = (PdfDictionary)Page(reopened, 0)["Resources"]!;
        var overlay = (PdfStream)reopened.Resolve(((PdfDictionary)resources["XObject"]!)["TheoisticOverlay1"])!;

        Assert.AreEqual("Form", ((PdfName)overlay["Subtype"]!).Value);

        var overlayResources = (PdfDictionary)reopened.Resolve(overlay["Resources"])!;
        var type0 = (PdfDictionary)reopened.Resolve(((PdfDictionary)reopened.Resolve(overlayResources["Font"])!)["F1"])!;
        var descendant = (PdfDictionary)reopened.Resolve(((PdfArray)reopened.Resolve(type0["DescendantFonts"])!)[0])!;
        var descriptor = (PdfDictionary)reopened.Resolve(descendant["FontDescriptor"])!;

        Assert.IsInstanceOfType<PdfStream>(reopened.Resolve(descriptor["FontFile2"]), "the subset font program did not come across");

        var image = (PdfStream)reopened.Resolve(((PdfDictionary)reopened.Resolve(overlayResources["XObject"])!)["Im1"])!;

        Assert.IsInstanceOfType<PdfStream>(reopened.Resolve(image["SMask"]), "the image's soft mask did not come across");
    }

    [TestMethod]
    public void Save_WithNothingDrawn_ReturnsTheOriginal()
    {
        byte[] original = TwoPageDocument();
        PdfEditor editor = PdfEditor.Open(original);

        editor.DrawOn(0);

        CollectionAssert.AreEqual(original, editor.Save());
    }

    [TestMethod]
    public void Save_KeepsTheOriginalAsAPrefixAndChainsWithPrev()
    {
        byte[] original = TwoPageDocument();
        PdfEditor editor = PdfEditor.Open(original);

        editor.DrawOn(1).Content.DrawText("Note", StandardFont.HelveticaBold, 9, 30, 30);

        byte[] saved = editor.Save();
        string update = UpdateOf(original, saved);
        long previous = PdfFile.Open(original).StartXref;

        // A classic table after a classic table.
        StringAssert.Contains(update, "\nxref\n");
        StringAssert.Contains(update, $"/Prev {previous}");

        // The same edits saved again produce the same bytes.
        CollectionAssert.AreEqual(saved, editor.Save());
    }

    [TestMethod]
    public void Save_XrefStreamFile_WritesAnXrefStream()
    {
        byte[] original = PdfFixtures.ObjectStreams();
        PdfEditor editor = PdfEditor.Open(original);

        editor.DrawOn(0).Content.DrawText("Over", StandardFont.Helvetica, 10, 10, 10);

        byte[] saved = editor.Save();
        string update = UpdateOf(original, saved);

        StringAssert.Contains(update, "/Type /XRef");
        Assert.IsFalse(update.Contains("\nxref\n", StringComparison.Ordinal), "a table after a cross-reference stream");

        PdfFile reopened = PdfFile.Open(saved);

        Assert.IsTrue(reopened.LastSectionIsStream);
        Assert.IsFalse(reopened.Reconstructed);
        Assert.AreEqual(3, ((PdfArray)Page(reopened, 0)["Contents"]!).Count);
    }

    [TestMethod]
    public void Save_HybridFile_WritesATable()
    {
        byte[] original = PdfFixtures.ObjectStreams(hybrid: true);
        PdfEditor editor = PdfEditor.Open(original);

        editor.DrawOn(0).Content.DrawText("Over", StandardFont.Helvetica, 10, 10, 10);

        byte[] saved = editor.Save();

        StringAssert.Contains(UpdateOf(original, saved), "\nxref\n");
        Assert.AreEqual(1, PdfEditor.Open(saved).Pages.Count);
    }

    [TestMethod]
    public void Save_KeepsTheFirstIdentifierAndReplacesTheSecond()
    {
        byte[] original = PdfFixtures.Classic(
            new[] { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>" },
            "/ID [<00112233445566778899AABBCCDDEEFF> <00112233445566778899AABBCCDDEEFF>] ");
        PdfEditor editor = PdfEditor.Open(original);

        editor.DrawOn(0).Content.Rectangle(1, 1, 2, 2).Fill();

        PdfFile reopened = PdfFile.Open(editor.Save());
        var id = (PdfArray)reopened.Trailer["ID"]!;

        CollectionAssert.AreEqual(Convert.FromHexString("00112233445566778899AABBCCDDEEFF"), ((PdfString)id[0]).Bytes);
        CollectionAssert.AreNotEqual(((PdfString)id[0]).Bytes, ((PdfString)id[1]).Bytes);
    }

    // ---------------------------------------------------------------- adding pages

    [TestMethod]
    public void AppendPages_AddsAFlowDocumentsPages()
    {
        byte[] original = TwoPageDocument();
        PdfEditor editor = PdfEditor.Open(original);

        var flow = new FlowDocument();

        flow.Heading("Appendix");
        flow.Paragraph("Terms the applicant has accepted.");
        flow.PageBreak();
        flow.Paragraph("Second appended page.");

        editor.AppendPages(flow.ToArray());

        Assert.AreEqual(4, editor.Pages.Count);
        Assert.AreEqual(new PdfPageInfo(2, 595, 842, 0), editor.Pages[2]);

        byte[] saved = editor.Save();

        UpdateOf(original, saved);

        PdfFile reopened = PdfFile.Open(saved);
        List<PdfPageNode> pages = PageTree.Read(reopened);
        var root = (PdfDictionary)reopened.Resolve(reopened.Catalog["Pages"])!;

        Assert.AreEqual(4, pages.Count);
        Assert.AreEqual(4L, ((PdfInteger)root["Count"]!).Value);
        Assert.AreEqual(reopened.Catalog["Pages"]!.ToString(), pages[3].Dictionary["Parent"]!.ToString());

        var font = (PdfDictionary)reopened.Resolve(((PdfDictionary)reopened.Resolve(pages[2].Resources)!)["Font"])!;

        Assert.IsTrue(font.Count > 0, "the appended page's fonts did not come across");
    }

    [TestMethod]
    public void AppendPages_InheritedRotationDoesNotLeakOntoAppendedPages()
    {
        // The tree root carries /Rotate 90 and a shared /Resources; an appended page must not
        // inherit either.
        byte[] original = PdfFixtures.RotatedPage();
        PdfEditor editor = PdfEditor.Open(original);
        var appendix = new PdfDocument();

        appendix.AddPage(PdfPaperSize.A5).Content.DrawText("Appendix", StandardFont.Helvetica, 12, 50, 500);
        editor.AppendPages(appendix.ToArray());

        PdfEditor reopened = PdfEditor.Open(editor.Save());

        Assert.AreEqual(new PdfPageInfo(0, 842, 595, 90), reopened.Pages[0]);
        Assert.AreEqual(new PdfPageInfo(1, 420, 595, 0), reopened.Pages[1]);
    }

    [TestMethod]
    public void AddPage_AppendsABlankPageDrawnWithTheNormalApi()
    {
        byte[] original = PdfFixtures.ObjectStreams();
        PdfEditor editor = PdfEditor.Open(original);

        PdfPage page = editor.AddPage(300, 200);

        page.Content.DrawText("Added", StandardFont.Helvetica, 12, 20, 100);

        Assert.AreSame(page, editor.DrawOn(1), "DrawOn on an added page should return that page");

        byte[] saved = editor.Save();
        PdfEditor reopened = PdfEditor.Open(saved);

        Assert.AreEqual(2, reopened.Pages.Count);
        Assert.AreEqual(new PdfPageInfo(1, 300, 200, 0), reopened.Pages[1]);
    }

    [TestMethod]
    public void DrawOn_AppendedPage_OverlaysTheImportedCopy()
    {
        byte[] original = TwoPageDocument();
        PdfEditor editor = PdfEditor.Open(original);

        editor.AppendPages(PdfFixtures.CroppedPage());
        editor.DrawOn(2).Content.Rectangle(0, 0, 5, 5).Fill();

        byte[] saved = editor.Save();

        StringAssert.Contains(UpdateOf(original, saved), "1 0 0 1 50 60 cm");
        Assert.AreEqual(new PdfPageInfo(2, 500, 700, 0), PdfEditor.Open(saved).Pages[2]);
    }
}
