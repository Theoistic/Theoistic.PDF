using Theoistic.PDF.Rendering;

namespace Theoistic.PDF.Tests;

/// <summary>
/// Covers the settings translation, option validation and browser discovery. None of these
/// start a browser, so they run anywhere.
/// </summary>
[TestClass]
public sealed class PdfRendererTests
{
    private static Dictionary<string, object?> Map(
        Action<GlobalSettings>? global = null,
        Action<ObjectSettings>? content = null)
    {
        var globalSettings = new GlobalSettings { PaperSize = PaperKind.A4, Orientation = Orientation.Portrait };
        var objectSettings = new ObjectSettings { HtmlContent = "<p>x</p>" };

        global?.Invoke(globalSettings);
        content?.Invoke(objectSettings);

        return PrintSettingsMapper.ToPrintParameters(globalSettings, objectSettings);
    }

    private static double Number(Dictionary<string, object?> parameters, string key) =>
        Convert.ToDouble(parameters[key], System.Globalization.CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- paper size

    [TestMethod]
    public void PaperSize_A4IsConvertedToInches()
    {
        Dictionary<string, object?> parameters = Map();

        Assert.AreEqual(210 / 25.4, Number(parameters, "paperWidth"), 1e-6);
        Assert.AreEqual(297 / 25.4, Number(parameters, "paperHeight"), 1e-6);
    }

    [TestMethod]
    public void PaperSize_DefaultsToA4WhenUnset()
    {
        // The builder has always defaulted to A4; Chromium's own default is Letter, so leaving
        // this to the browser would silently change existing documents.
        Dictionary<string, object?> parameters = Map(g => g.PaperSize = null);

        Assert.AreEqual(210 / 25.4, Number(parameters, "paperWidth"), 1e-6);
    }

    [TestMethod]
    public void PaperSize_LetterIsAlreadyInInches()
    {
        Dictionary<string, object?> parameters = Map(g => g.PaperSize = PaperKind.Letter);

        Assert.AreEqual(8.5, Number(parameters, "paperWidth"), 1e-6);
        Assert.AreEqual(11.0, Number(parameters, "paperHeight"), 1e-6);
    }

    [TestMethod]
    [DataRow("1in", 1.0)]
    [DataRow("25.4mm", 1.0)]
    [DataRow("2.54cm", 1.0)]
    [DataRow("72pt", 1.0)]
    [DataRow("96px", 1.0)]
    [DataRow("210mm", 8.2677165)]
    public void ToInches_HandlesEveryUnit(string value, double expected)
    {
        Assert.AreEqual(expected, PrintSettingsMapper.ToInches(value, "width"), 1e-6);
    }

    [TestMethod]
    public void ToInches_RejectsABareNumber()
    {
        // 210 as inches and 210 as millimetres differ by a factor of twenty-five, so guessing
        // would produce a silently wrong page rather than an error.
        PdfConversionException error = Assert.ThrowsException<PdfConversionException>(
            () => PrintSettingsMapper.ToInches("210", "width"));

        Assert.IsTrue(error.Message.Contains("unit", StringComparison.OrdinalIgnoreCase), error.Message);
    }

    [TestMethod]
    public void ToInches_RejectsNonsense()
    {
        Assert.ThrowsException<PdfConversionException>(() => PrintSettingsMapper.ToInches("wide", "width"));
    }

    // ---------------------------------------------------------------- orientation

    [TestMethod]
    public void Orientation_LandscapeSwapsTheDimensions()
    {
        Dictionary<string, object?> parameters = Map(g => g.Orientation = Orientation.Landscape);

        Assert.AreEqual(297 / 25.4, Number(parameters, "paperWidth"), 1e-6);
        Assert.AreEqual(210 / 25.4, Number(parameters, "paperHeight"), 1e-6);
    }

    [TestMethod]
    public void Orientation_LandscapeFlagIsNotUsed()
    {
        // Chromium ignores the landscape flag when explicit dimensions are supplied, so the
        // rotation has to be baked into the dimensions and the flag left off.
        Dictionary<string, object?> parameters = Map(g => g.Orientation = Orientation.Landscape);

        Assert.AreEqual(false, parameters["landscape"]);
    }

    [TestMethod]
    public void PageSize_IgnoresTheStylesheet()
    {
        // The settings are the authority: an @page rule must not override what the caller set.
        Assert.AreEqual(false, Map()["preferCSSPageSize"]);
    }

    // ---------------------------------------------------------------- margins

    [TestMethod]
    public void Margins_DefaultToTenMillimetres()
    {
        Dictionary<string, object?> parameters = Map();

        Assert.AreEqual(10 / 25.4, Number(parameters, "marginTop"), 1e-6);
        Assert.AreEqual(10 / 25.4, Number(parameters, "marginRight"), 1e-6);
    }

    [TestMethod]
    public void Margins_HonourTheUnit()
    {
        Dictionary<string, object?> parameters = Map(g =>
            g.Margins = new MarginSettings(top: 1, right: 2, bottom: 3, left: 4) { Unit = Unit.Inches });

        Assert.AreEqual(1, Number(parameters, "marginTop"), 1e-6);
        Assert.AreEqual(2, Number(parameters, "marginRight"), 1e-6);
        Assert.AreEqual(3, Number(parameters, "marginBottom"), 1e-6);
        Assert.AreEqual(4, Number(parameters, "marginLeft"), 1e-6);
    }

    [TestMethod]
    public void Margins_ConvertCentimetres()
    {
        Dictionary<string, object?> parameters = Map(g =>
            g.Margins = new MarginSettings { Unit = Unit.Centimeters, Top = 2.54 });

        Assert.AreEqual(1, Number(parameters, "marginTop"), 1e-6);
    }

    // ---------------------------------------------------------------- background and scale

    [TestMethod]
    public void Background_IsPrintedByDefault()
    {
        // wkhtmltox printed backgrounds unless told otherwise, and callers' stylesheets assume it.
        Assert.AreEqual(true, Map()["printBackground"]);
    }

    [TestMethod]
    public void Background_CanBeTurnedOff()
    {
        Assert.AreEqual(false, Map(content: o => o.WebSettings.Background = false)["printBackground"]);
    }

    [TestMethod]
    public void ZoomFactor_BecomesScale()
    {
        Assert.AreEqual(1.5, Number(Map(content: o => o.LoadSettings.ZoomFactor = 1.5), "scale"), 1e-9);
    }

    [TestMethod]
    public void ZoomFactor_IsClampedToWhatTheProtocolAccepts()
    {
        Assert.AreEqual(2.0, Number(Map(content: o => o.LoadSettings.ZoomFactor = 9), "scale"), 1e-9);
        Assert.AreEqual(0.1, Number(Map(content: o => o.LoadSettings.ZoomFactor = 0.01), "scale"), 1e-9);
    }

    [TestMethod]
    public void Scale_IsOmittedWhenNoZoomIsSet()
    {
        Assert.IsFalse(Map().ContainsKey("scale"));
    }

    // ---------------------------------------------------------------- headers and footers

    [TestMethod]
    public void HeaderFooter_AreOffWhenNothingIsConfigured()
    {
        Assert.AreEqual(false, Map()["displayHeaderFooter"]);
    }

    [TestMethod]
    public void HeaderFooter_TurnOnWhenAnyPartIsSet()
    {
        Dictionary<string, object?> parameters = Map(content: o => o.FooterSettings.Center = "hello");

        Assert.AreEqual(true, parameters["displayHeaderFooter"]);
        Assert.IsTrue(((string)parameters["footerTemplate"]!).Contains("hello", StringComparison.Ordinal));
    }

    [TestMethod]
    public void HeaderFooter_SuppliesBothTemplatesSoChromiumUsesNeitherOfItsOwn()
    {
        // An omitted template falls back to Chromium's built-in one, which prints a title and
        // url nobody asked for.
        Dictionary<string, object?> parameters = Map(content: o => o.HeaderSettings.Left = "only a header");

        Assert.IsTrue(parameters.ContainsKey("footerTemplate"), "no footer template was supplied");
        Assert.IsFalse(((string)parameters["footerTemplate"]!).Contains("class=", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("[page]", "pageNumber")]
    [DataRow("[topage]", "totalPages")]
    [DataRow("[date]", "date")]
    [DataRow("[title]", "title")]
    [DataRow("[webpage]", "url")]
    public void Placeholders_AreTranslatedToChromiumsSpans(string placeholder, string expectedClass)
    {
        Assert.AreEqual(
            $"<span class=\"{expectedClass}\"></span>",
            PrintSettingsMapper.Placeholders(placeholder));
    }

    [TestMethod]
    public void Placeholders_KeepSurroundingText()
    {
        Assert.AreEqual(
            "Page <span class=\"pageNumber\"></span> of <span class=\"totalPages\"></span>",
            PrintSettingsMapper.Placeholders("Page [page] of [topage]"));
    }

    [TestMethod]
    public void Placeholders_EscapeTheTextAroundThem()
    {
        // Unescaped, a caller's angle bracket would become part of the template's markup.
        Assert.AreEqual(
            "a&lt;b&gt;&amp;c",
            PrintSettingsMapper.Placeholders("a<b>&c"));
    }

    [TestMethod]
    public void Placeholders_DropOnesWithNoCounterpart()
    {
        Assert.AreEqual("xy", PrintSettingsMapper.Placeholders("x[section]y"));
    }

    [TestMethod]
    public void Placeholders_OfNothingIsEmpty()
    {
        Assert.AreEqual(string.Empty, PrintSettingsMapper.Placeholders(null));
        Assert.AreEqual(string.Empty, PrintSettingsMapper.Placeholders(string.Empty));
    }

    [TestMethod]
    public void Header_AppliesFontAndRule()
    {
        Dictionary<string, object?> parameters = Map(content: o =>
        {
            o.HeaderSettings.Center = "titled";
            o.HeaderSettings.FontSize = 14;
            o.HeaderSettings.FontName = "Times New Roman";
            o.HeaderSettings.Line = true;
        });

        string template = (string)parameters["headerTemplate"]!;

        Assert.IsTrue(template.Contains("font-size:14px", StringComparison.Ordinal), template);
        Assert.IsTrue(template.Contains("'Times New Roman'", StringComparison.Ordinal), template);
        Assert.IsTrue(template.Contains("border-bottom", StringComparison.Ordinal), template);
    }

    [TestMethod]
    public void Footer_RuleGoesAboveTheText()
    {
        Dictionary<string, object?> parameters = Map(content: o =>
        {
            o.FooterSettings.Center = "footed";
            o.FooterSettings.Line = true;
        });

        Assert.IsTrue(((string)parameters["footerTemplate"]!).Contains("border-top", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Header_FromAMissingFileIsReported()
    {
        PdfConversionException error = Assert.ThrowsException<PdfConversionException>(
            () => Map(content: o => o.HeaderSettings.HtmUrl = @"C:\does\not\exist\header.html"));

        Assert.IsTrue(error.Message.Contains("not found", StringComparison.OrdinalIgnoreCase), error.Message);
    }

    [TestMethod]
    public void Header_FromARemoteUrlIsRejectedClearly()
    {
        PdfConversionException error = Assert.ThrowsException<PdfConversionException>(
            () => Map(content: o => o.HeaderSettings.HtmUrl = "https://example.com/header.html"));

        Assert.IsTrue(error.Message.Contains("local file", StringComparison.OrdinalIgnoreCase), error.Message);
    }

    [TestMethod]
    public void Header_FromALocalFileIsUsedVerbatim()
    {
        string path = Path.Combine(Path.GetTempPath(), $"header-{Guid.NewGuid():N}.html");

        File.WriteAllText(path, "<div id='mine'>from a file</div>");

        try
        {
            Dictionary<string, object?> parameters = Map(content: o => o.HeaderSettings.HtmUrl = path);

            Assert.AreEqual("<div id='mine'>from a file</div>", parameters["headerTemplate"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---------------------------------------------------------------- viewport

    [TestMethod]
    [DataRow("800x600", 800, 600)]
    [DataRow("1280 x 1024", 1280, 1024)]
    public void Viewport_IsParsed(string value, int width, int height)
    {
        Assert.IsTrue(ChromiumRenderer.TryParseViewport(value, out int w, out int h));
        Assert.AreEqual(width, w);
        Assert.AreEqual(height, h);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("wide")]
    [DataRow("800")]
    public void Viewport_IgnoresWhatItCannotParse(string value)
    {
        // A cosmetic setting must not fail a render.
        Assert.IsFalse(ChromiumRenderer.TryParseViewport(value, out _, out _));
    }

    // ---------------------------------------------------------------- options

    [TestMethod]
    public void Options_RejectBothRemoteForms()
    {
        var options = new PdfRendererOptions
        {
            RemoteEndpoint = new Uri("ws://localhost:9222/devtools/browser/x"),
            RemoteAddress = new Uri("http://localhost:9222"),
        };

        Assert.ThrowsException<InvalidOperationException>(() => new ChromiumRenderer(options));
    }

    [TestMethod]
    public void Options_RejectARemoteBrowserWithALocalPath()
    {
        var options = new PdfRendererOptions
        {
            RemoteEndpoint = new Uri("ws://localhost:9222/devtools/browser/x"),
            ExecutablePath = @"C:\chrome.exe",
        };

        Assert.ThrowsException<InvalidOperationException>(() => new ChromiumRenderer(options));
    }

    [TestMethod]
    public void Options_RejectARemoteAddressWithALocalPath()
    {
        // The same mistake as above but with the http form, which used to be accepted and then
        // silently preferred the remote browser.
        var options = new PdfRendererOptions
        {
            RemoteAddress = new Uri("http://localhost:9222"),
            ExecutablePath = @"C:\chrome.exe",
        };

        Assert.ThrowsException<InvalidOperationException>(() => new ChromiumRenderer(options));
    }

    [TestMethod]
    public void Options_RejectAnHttpEndpoint()
    {
        // A common mix-up, and the message says which property to use instead.
        var options = new PdfRendererOptions { RemoteEndpoint = new Uri("http://localhost:9222") };

        InvalidOperationException error =
            Assert.ThrowsException<InvalidOperationException>(() => new ChromiumRenderer(options));

        Assert.IsTrue(error.Message.Contains("RemoteAddress", StringComparison.Ordinal), error.Message);
    }

    [TestMethod]
    public void Options_RejectAWebSocketAddress()
    {
        var options = new PdfRendererOptions { RemoteAddress = new Uri("ws://localhost:9222") };

        Assert.ThrowsException<InvalidOperationException>(() => new ChromiumRenderer(options));
    }

    [TestMethod]
    public void Options_AreSnapshotSoLaterMutationCannotChangeBehaviour()
    {
        var options = new PdfRendererOptions { ExecutablePath = @"C:\first.exe" };

        using var renderer = new ChromiumRenderer(options);

        options.ExecutablePath = @"C:\second.exe";
        options.Arguments.Add("--added-later");

        // Nothing to assert directly without launching, but the clone must not throw and the
        // renderer must still be usable; a shared instance would have mutated underneath it.
        Assert.IsNotNull(renderer);
    }

    // ---------------------------------------------------------------- discovery

    [TestMethod]
    public void Locator_ProbesPlausibleLocations()
    {
        IReadOnlyList<string> probed = ChromiumLocator.ProbedLocations();

        Assert.IsTrue(probed.Count > 0, "nothing is probed");
        Assert.IsTrue(
            probed.Any(p => p.Contains("Edge", StringComparison.OrdinalIgnoreCase)
                || p.Contains("chrom", StringComparison.OrdinalIgnoreCase)),
            "no Chromium-family browser is probed for");
    }

    [TestMethod]
    public async Task Check_ReportsWhyItCannotRunRatherThanThrowing()
    {
        var options = new PdfRendererOptions
        {
            ExecutablePath = Path.Combine(Path.GetTempPath(), "definitely-not-a-browser.exe"),
            LaunchTimeout = TimeSpan.FromSeconds(5),
        };

        await using var renderer = new ChromiumRenderer(options);

        PdfRendererStatus status = await renderer.CheckAsync();

        Assert.IsFalse(status.IsAvailable);
        Assert.IsFalse(string.IsNullOrWhiteSpace(status.Description));
    }

    [TestMethod]
    public async Task Render_WithNoContentIsRejectedBeforeABrowserIsNeeded()
    {
        await using var renderer = new ChromiumRenderer();

        PdfConversionException error = await Assert.ThrowsExceptionAsync<PdfConversionException>(
            () => renderer.RenderAsync(new GlobalSettings(), new ObjectSettings()));

        Assert.IsTrue(error.Message.Contains("Nothing to render", StringComparison.Ordinal), error.Message);
    }

    [TestMethod]
    public void UnsupportedSettings_AreDocumentedInOnePlace()
    {
        // The list is the honest statement of what a browser backend cannot do; if it empties
        // out, the documentation has drifted from the mapper.
        Assert.IsTrue(PrintSettingsMapper.UnsupportedSettings.Length > 0);
        CollectionAssert.Contains(PrintSettingsMapper.UnsupportedSettings, nameof(GlobalSettings.ColorMode));
    }
}
