using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Theoistic.PDF.Rendering;

namespace Theoistic.PDF.Tests;

/// <summary>
/// Covers the builder. The settings plumbing is exercised against a stub renderer so it runs
/// anywhere; the end-to-end tests drive a real browser and report themselves inconclusive when
/// none is installed, because an absent browser is a property of the machine rather than a bug.
/// </summary>
[TestClass]
public sealed class PDFBuilderTests
{
    private const string Html = """
        <!DOCTYPE html>
        <html><head><meta charset="utf-8"><title>t</title></head>
        <body><h1>Hello</h1><p>Some content to lay out.</p></body></html>
        """;

    private static string? browserPath;

    [ClassInitialize]
    public static void Initialize(TestContext _)
    {
        browserPath = ChromiumLocator.Find();
    }

    private static void RequireBrowser()
    {
        if (browserPath is null)
        {
            Assert.Inconclusive(
                "No Chromium-family browser is installed, so the end-to-end path cannot run here. " +
                $"Probed: {string.Join(", ", ChromiumLocator.ProbedLocations())}");
        }
    }

    private static void AssertIsPdf(byte[] bytes)
    {
        Assert.IsTrue(bytes.Length > 0, "render produced no bytes");
        Assert.AreEqual("%PDF", Encoding.ASCII.GetString(bytes, 0, 4), "output is not a PDF");
    }

    /// <summary>
    /// Records what the builder passed down and returns a syntactically valid stub, so the
    /// builder's own behaviour can be tested without a browser.
    /// </summary>
    private sealed class StubRenderer : IPdfRenderer
    {
        public GlobalSettings? Global { get; private set; }

        public ObjectSettings? Content { get; private set; }

        public int Calls { get; private set; }

        public Task<byte[]> RenderAsync(
            GlobalSettings global,
            ObjectSettings content,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Global = global;
            Content = content;
            Calls++;

            return Task.FromResult(Encoding.ASCII.GetBytes("%PDF-1.7\nstub\n%%EOF\n"));
        }

        public Task<PdfRendererStatus> CheckAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new PdfRendererStatus(true, "stub"));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // ---------------------------------------------------------------- builder plumbing

    [TestMethod]
    public async Task BuildAsync_PassesTheHtmlToTheRenderer()
    {
        var stub = new StubRenderer();
        using var builder = new PDFBuilder(stub);

        await builder.BuildAsync(Html);

        Assert.AreEqual(1, stub.Calls);
        Assert.AreEqual(Html, stub.Content!.HtmlContent);
    }

    [TestMethod]
    public async Task BuildAsync_AppliesSettingsWithoutLosingDefaults()
    {
        var stub = new StubRenderer();
        using var builder = new PDFBuilder(stub);

        await builder
            .Settings(s => s.Margins = new MarginSettings { Unit = Unit.Inches, Top = 1 })
            .BuildAsync(Html);

        // The action mutates the defaults rather than replacing them, so A4 and portrait survive.
        Assert.AreEqual(Orientation.Portrait, stub.Global!.Orientation);
        Assert.AreEqual("210mm", stub.Global.PaperSize!.Width);
        Assert.AreEqual(1, stub.Global.Margins.Top);
        Assert.AreEqual(Unit.Inches, stub.Global.Margins.Unit);
    }

    [TestMethod]
    public async Task BuildAsync_KeepsTheUtf8EncodingDefaultOnObjectSettings()
    {
        var stub = new StubRenderer();
        using var builder = new PDFBuilder(stub);

        await builder.BuildAsync(Html, o => o.LoadSettings.JSDelay = 0);

        Assert.AreEqual("utf-8", stub.Content!.WebSettings.DefaultEncoding);
        Assert.AreEqual(0, stub.Content.LoadSettings.JSDelay);
    }

    [TestMethod]
    public async Task BuildAsync_WritesToFileWhenOutIsSet()
    {
        string path = Path.Combine(Path.GetTempPath(), $"out-{Guid.NewGuid():N}.pdf");
        var stub = new StubRenderer();

        using var builder = new PDFBuilder(stub);

        try
        {
            byte[] returned = await builder.Settings(s => s.Out = path).BuildAsync(Html);

            Assert.IsTrue(File.Exists(path), "the file named by Out was not written");
            CollectionAssert.AreEqual(returned, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task InjectCSS_PutsTheLinkBeforeTheClosingHead()
    {
        string css = Path.Combine(Path.GetTempPath(), $"style-{Guid.NewGuid():N}.css");

        await File.WriteAllTextAsync(css, "body { color: red }");

        var stub = new StubRenderer();
        using var builder = new PDFBuilder(stub);

        try
        {
            await builder.InjectCSS(css).BuildAsync(Html);

            string html = stub.Content!.HtmlContent!;
            int link = html.IndexOf("<link", StringComparison.Ordinal);
            int head = html.IndexOf("</head>", StringComparison.Ordinal);

            Assert.IsTrue(link > 0, "no stylesheet link was injected");
            Assert.IsTrue(link < head, "the link was injected after the closing head tag");
            Assert.IsTrue(html.Contains("style-", StringComparison.Ordinal), "the link does not name the file");
        }
        finally
        {
            File.Delete(css);
        }
    }

    [TestMethod]
    public async Task InjectCSS_AppliesToEveryMediaNotJustScreen()
    {
        string css = Path.Combine(Path.GetTempPath(), $"style-{Guid.NewGuid():N}.css");

        await File.WriteAllTextAsync(css, "body { color: red }");

        var stub = new StubRenderer();
        using var builder = new PDFBuilder(stub);

        try
        {
            await builder.InjectCSS(css).BuildAsync(Html);

            string html = stub.Content!.HtmlContent!;

            // A screen-only stylesheet is ignored while print media is being rendered, which is
            // what printing does - the injected CSS would silently do nothing.
            Assert.IsTrue(html.Contains("media=\"all\"", StringComparison.Ordinal), html);
            Assert.IsFalse(html.Contains("media=\"screen\"", StringComparison.Ordinal), "the link is screen-only");
        }
        finally
        {
            File.Delete(css);
        }
    }

    [TestMethod]
    public void InjectCSS_RejectsAMissingFile()
    {
        using var builder = new PDFBuilder(new StubRenderer());

        Assert.ThrowsException<FileNotFoundException>(
            () => builder.InjectCSS(Path.Combine(Path.GetTempPath(), "no-such-file.css")));
    }

    [TestMethod]
    public async Task BuildAsync_InjectsIntoAFragmentWithNoHead()
    {
        string css = Path.Combine(Path.GetTempPath(), $"style-{Guid.NewGuid():N}.css");

        await File.WriteAllTextAsync(css, "p { margin: 0 }");

        var stub = new StubRenderer();
        using var builder = new PDFBuilder(stub);

        try
        {
            await builder.InjectCSS(css).BuildAsync("<p>bare fragment</p>");

            Assert.IsTrue(stub.Content!.HtmlContent!.StartsWith("<link", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(css);
        }
    }

    [TestMethod]
    public async Task BuildAsync_RejectsNullHtml()
    {
        using var builder = new PDFBuilder(new StubRenderer());

        await Assert.ThrowsExceptionAsync<ArgumentNullException>(() => builder.BuildAsync(null!));
    }

    [TestMethod]
    public async Task BuildAsync_AfterDisposeIsRejected()
    {
        var builder = new PDFBuilder(new StubRenderer());

        builder.Dispose();

        await Assert.ThrowsExceptionAsync<ObjectDisposedException>(() => builder.BuildAsync(Html));
    }

    [TestMethod]
    public async Task BuildAsync_HonoursCancellation()
    {
        using var builder = new PDFBuilder(new StubRenderer());
        using var cancelled = new CancellationTokenSource();

        await cancelled.CancelAsync();

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(
            () => builder.BuildAsync(Html, null, cancelled.Token));
    }

    [TestMethod]
    public void Constructor_WithoutInitialisationExplainsWhatIsMissing()
    {
        IServiceProvider? saved = TheoisticPDFExtensions.ServiceProvider;

        TheoisticPDFExtensions.ServiceProvider = null;

        try
        {
            InvalidOperationException error = Assert.ThrowsException<InvalidOperationException>(() => new PDFBuilder());

            Assert.IsTrue(error.Message.Contains("AddTheoisticPDF", StringComparison.Ordinal), error.Message);
        }
        finally
        {
            TheoisticPDFExtensions.ServiceProvider = saved;
        }
    }

    // ---------------------------------------------------------------- registration

    [TestMethod]
    public void AddTheoisticPDF_RegistersTheRendererAsASingleton()
    {
        var services = new ServiceCollection();

        services.AddTheoisticPDF();

        using ServiceProvider provider = services.BuildServiceProvider();

        var first = provider.GetRequiredService<IPdfRenderer>();
        var second = provider.GetRequiredService<IPdfRenderer>();

        Assert.IsInstanceOfType<ChromiumRenderer>(first);

        // One browser is shared across renders; a transient registration would start one per
        // request and cost more than the render itself.
        Assert.AreSame(first, second);
    }

    [TestMethod]
    public void AddTheoisticPDF_PassesOptionsThrough()
    {
        var services = new ServiceCollection();

        services.AddTheoisticPDF(o =>
        {
            o.ExecutablePath = @"C:\configured\chrome.exe";
            o.RenderTimeout = TimeSpan.FromSeconds(5);
        });

        using ServiceProvider provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<PdfRendererOptions>();

        Assert.AreEqual(@"C:\configured\chrome.exe", options.ExecutablePath);
        Assert.AreEqual(TimeSpan.FromSeconds(5), options.RenderTimeout);
    }

    // ---------------------------------------------------------------- end to end

    [TestMethod]
    public async Task Render_ProducesAPdfFromARealBrowser()
    {
        RequireBrowser();

        await using var renderer = new ChromiumRenderer();
        using var builder = new PDFBuilder(renderer);

        AssertIsPdf(await builder.BuildAsync(Html));
    }

    [TestMethod]
    public async Task Check_ReportsTheBrowserItFound()
    {
        RequireBrowser();

        await using var renderer = new ChromiumRenderer();

        PdfRendererStatus status = await renderer.CheckAsync();

        Assert.IsTrue(status.IsAvailable, status.Description);
        Assert.IsTrue(
            status.Description.Contains("Chrome", StringComparison.OrdinalIgnoreCase)
                || status.Description.Contains("Edg", StringComparison.OrdinalIgnoreCase),
            $"unexpected product string: {status.Description}");
    }

    [TestMethod]
    public async Task Render_HandlesNonAsciiContent()
    {
        RequireBrowser();

        await using var renderer = new ChromiumRenderer();
        using var builder = new PDFBuilder(renderer);

        byte[] pdf = await builder.BuildAsync(
            """
            <!DOCTYPE html><html><head><meta charset="utf-8"></head>
            <body><p>caf&#233; &#8364;1.234,56 &#8212; &#20013;&#25991; &#1605;&#1585;&#1581;&#1576;&#1575;</p></body></html>
            """);

        AssertIsPdf(pdf);
    }

    [TestMethod]
    public async Task Render_ReusesOneBrowserAcrossConcurrentCallers()
    {
        RequireBrowser();

        await using var renderer = new ChromiumRenderer();

        // Each render gets its own tab on a shared browser, so these must not interfere.
        byte[][] results = await Task.WhenAll(Enumerable.Range(0, 4).Select(async i =>
        {
            using var builder = new PDFBuilder(renderer);

            return await builder.BuildAsync($"<html><body><h1>document {i}</h1></body></html>");
        }));

        foreach (byte[] pdf in results)
        {
            AssertIsPdf(pdf);
        }
    }

    [TestMethod]
    public async Task Render_AppliesLandscapeToThePageBox()
    {
        RequireBrowser();

        await using var renderer = new ChromiumRenderer();

        async Task<byte[]> BuildAsync(Orientation orientation)
        {
            using var builder = new PDFBuilder(renderer);

            return await builder
                .Settings(s => s.Orientation = orientation)
                .BuildAsync("<html><body><p>orientation</p></body></html>");
        }

        byte[] portrait = await BuildAsync(Orientation.Portrait);
        byte[] landscape = await BuildAsync(Orientation.Landscape);

        AssertIsPdf(portrait);
        AssertIsPdf(landscape);

        // Chromium writes the page box into the page dictionary uncompressed, so the swap is
        // observable without parsing the whole file.
        Assert.AreNotEqual(
            MediaBoxOf(portrait),
            MediaBoxOf(landscape),
            "landscape produced the same page box as portrait");
    }

    [TestMethod]
    public async Task Render_DocumentTitleReachesThePdfMetadata()
    {
        RequireBrowser();

        await using var renderer = new ChromiumRenderer();
        using var builder = new PDFBuilder(renderer);

        byte[] pdf = await builder
            .Settings(s => s.DocumentTitle = "Quarterly report")
            .BuildAsync("<html><head><title>overridden</title></head><body><p>x</p></body></html>");

        // Chromium writes the information dictionary uncompressed, so the title is readable
        // without parsing the whole file.
        Assert.IsTrue(
            Encoding.Latin1.GetString(pdf).Contains("Quarterly report", StringComparison.Ordinal),
            "DocumentTitle did not reach the PDF metadata");
    }

    [TestMethod]
    public async Task Render_WithoutATitleFallsBackToTheDocumentFileName()
    {
        RequireBrowser();

        // Chromium records document.title or, failing that, the document's file name. Markup is
        // rendered from a temporary file, so that name is the one this library chooses - and it
        // has to stay something innocuous rather than a generated string, because it ends up in
        // the metadata of every document whose markup carries no title.
        await using var renderer = new ChromiumRenderer();
        using var builder = new PDFBuilder(renderer);

        byte[] pdf = await builder.BuildAsync("<html><body><p>no title element</p></body></html>");

        string text = Encoding.Latin1.GetString(pdf);

        Assert.IsTrue(text.Contains("/Title (document.html)", StringComparison.Ordinal), "unexpected fallback title");
        Assert.IsFalse(text.Contains("theoistic-pdf-", StringComparison.Ordinal), "a generated temp name leaked into the metadata");
    }

    [TestMethod]
    public async Task Render_AppliesAnInjectedStylesheet()
    {
        RequireBrowser();

        // The whole point of InjectCSS, and it was silently a no-op while the link said
        // media="screen" and the browser rendered print media. A system font is used rather
        // than an @font-face so the test needs no font fixture: if the stylesheet is applied,
        // the typeface appears in the output.
        string css = Path.Combine(Path.GetTempPath(), $"style-{Guid.NewGuid():N}.css");

        await File.WriteAllTextAsync(css, "body { font-family: 'Courier New', monospace; }");

        await using var renderer = new ChromiumRenderer();
        using var builder = new PDFBuilder(renderer);

        try
        {
            byte[] pdf = await builder
                .InjectCSS(css)
                .BuildAsync("<html><body><p>styled by an injected stylesheet</p></body></html>");

            AssertIsPdf(pdf);

            Assert.IsTrue(
                Encoding.Latin1.GetString(pdf).Contains("Courier", StringComparison.OrdinalIgnoreCase),
                "the injected stylesheet was not applied: no Courier font is referenced");
        }
        finally
        {
            File.Delete(css);
        }
    }

    [TestMethod]
    public async Task Render_FromAUrlLoadsThatPage()
    {
        RequireBrowser();

        string path = Path.Combine(Path.GetTempPath(), $"page-{Guid.NewGuid():N}.html");

        await File.WriteAllTextAsync(path, "<html><body><h1>from a file url</h1></body></html>");

        await using var renderer = new ChromiumRenderer();

        try
        {
            byte[] pdf = await renderer.RenderAsync(
                new GlobalSettings { PaperSize = PaperKind.A4 },
                new ObjectSettings { Page = new Uri(path).AbsoluteUri });

            AssertIsPdf(pdf);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task Render_WithNoBrowserAnywhereExplainsEveryOption()
    {
        var options = new PdfRendererOptions
        {
            ExecutablePath = Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.exe"),
            LaunchTimeout = TimeSpan.FromSeconds(5),
            RenderTimeout = TimeSpan.FromSeconds(10),
        };

        await using var renderer = new ChromiumRenderer(options);

        PdfConversionException error = await Assert.ThrowsExceptionAsync<PdfConversionException>(
            () => renderer.RenderAsync(
                new GlobalSettings(),
                new ObjectSettings { HtmlContent = "<p>x</p>" }));

        Assert.IsTrue(
            error.Message.Contains("Could not start the browser", StringComparison.Ordinal),
            error.Message);
    }

    /// <summary>Reads the first page box out of a PDF, for comparing page geometry.</summary>
    private static string MediaBoxOf(byte[] pdf)
    {
        string text = Encoding.Latin1.GetString(pdf);
        int at = text.IndexOf("/MediaBox", StringComparison.Ordinal);

        Assert.IsTrue(at >= 0, "the document has no /MediaBox");

        int close = text.IndexOf(']', at);

        return text[at..(close + 1)];
    }
}
