using System.Globalization;
using System.Text;

namespace Theoistic.PDF;

public enum Unit
{
    Inches,
    Millimeters,
    Centimeters
}

public enum ContentErrorHandling
{
    Abort,
    Skip,
    Ignore
}

public enum ColorMode
{
    Color,
    Grayscale
}

public enum Orientation
{
    Landscape,
    Portrait
}

[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public class WkHtmlAttribute : Attribute
{
    public string Name { get; private set; }

    public WkHtmlAttribute(string name)
    {
        Name = name;
    }
}


public class ObjectSettings : IObject
{
    /// <summary>
    /// The URL or path of the web page to convert, if "-" input is read from stdin. Default = ""
    /// </summary>
    [WkHtml("page")]
    public string? Page { get; set; }

    /// <summary>
    /// Should external links in the HTML document be converted into external pdf links. Default = true
    /// </summary>
    [WkHtml("useExternalLinks")]
    public bool? UseExternalLinks { get; set; }

    /// <summary>
    /// Should internal links in the HTML document be converted into pdf references. Default = true
    /// </summary>
    [WkHtml("useLocalLinks")]
    public bool? UseLocalLinks { get; set; }

    /// <summary>
    /// Should we turn HTML forms into PDF forms. Default = false
    /// </summary>
    [WkHtml("produceForms")]
    public bool? ProduceForms { get; set; }

    /// <summary>
    /// Should the sections from this document be included in the outline and table of content. Default = false
    /// </summary>
    [WkHtml("includeInOutline")]
    public bool? IncludeInOutline { get; set; }

    /// <summary>
    /// Should we count the pages of this document, in the counter used for TOC, headers and footers. Default = false
    /// </summary>
    [WkHtml("pagesCount")]
    public bool? PagesCount { get; set; }

    public string? HtmlContent { get; set; }

    private WebSettings webSettings = new WebSettings();

    public WebSettings WebSettings {
        get { return webSettings; }
        set { webSettings = value; }
    }

    private HeaderSettings headerSettings = new HeaderSettings();

    public HeaderSettings HeaderSettings {
        get { return headerSettings; }
        set { headerSettings = value; }
    }

    private FooterSettings footerSettings = new FooterSettings();

    public FooterSettings FooterSettings {
        get { return footerSettings; }
        set { footerSettings = value; }
    }

    private LoadSettings loadSettings = new LoadSettings();

    public LoadSettings LoadSettings {
        get { return loadSettings; }
        set { loadSettings = value; }
    }

    public byte[] GetContent()
    {
        if (string.IsNullOrEmpty(HtmlContent))
        {
            return new byte[] { 0 };
        }

        // wkhtmltopdf_add_object reads this as a NUL terminated const char *, so the terminator
        // has to be part of the buffer; without it the native side runs off the end of the array.
        int byteCount = Encoding.UTF8.GetByteCount(HtmlContent);
        var content = new byte[byteCount + 1];
        Encoding.UTF8.GetBytes(HtmlContent, 0, HtmlContent.Length, content, 0);

        return content;
    }
}

public class MarginSettings
{
    public Unit Unit { get; set; }

    public double? Top { get; set; }

    public double? Bottom { get; set; }

    public double? Left { get; set; }

    public double? Right { get; set; }

    public MarginSettings()
    {
        Unit = Unit.Millimeters;
    }

    public MarginSettings(double top, double right, double bottom, double left) : this()
    {
        Top = top;

        Bottom = bottom;

        Left = left;

        Right = right;
    }

    public string? GetMarginValue(double? value)
    {
        if (!value.HasValue)
        {
            return null;
        }

        string strUnit = "in";

        switch (Unit)
        {
            case Unit.Inches:
                strUnit = "in";
                break;
            case Unit.Millimeters:
                strUnit = "mm";
                break;
            case Unit.Centimeters:
                strUnit = "cm";
                break;
            default:
                strUnit = "in";
                break;
        }

        return value.Value.ToString("0.##", CultureInfo.InvariantCulture) + strUnit;

    }
}

public class WebSettings : ISettings
{
    /// <summary>
    /// Should we print the background. Default = true
    /// </summary>
    [WkHtml("web.background")]
    public bool? Background { get; set; }

    /// <summary>
    /// Should we load images. Default = true
    /// </summary>
    [WkHtml("web.loadImages")]
    public bool? LoadImages { get; set; }

    /// <summary>
    /// Should we enable javascript. Default = true
    /// </summary>
    [WkHtml("web.enableJavascript")]
    public bool? EnableJavascript { get; set; }

    /// <summary>
    /// Should we enable intelligent shrinkng to fit more content on one page. Has no effect for wkhtmltoimage. Default = true
    /// </summary>
    [WkHtml("web.enableIntelligentShrinking")]
    public bool? EnableIntelligentShrinking { get; set; }

    /// <summary>
    /// The minimum font size allowed. Default = -1
    /// </summary>
    [WkHtml("web.minimumFontSize")]
    public int? MinimumFontSize { get; set; }

    /// <summary>
    /// Should the content be printed using the print media type instead of the screen media type. Default = false
    /// </summary>
    [WkHtml("web.printMediaType")]
    public bool? PrintMediaType { get; set; }

    /// <summary>
    /// What encoding should we guess content is using if they do not specify it properly. Default = ""
    /// </summary>
    [WkHtml("web.defaultEncoding")]
    public string? DefaultEncoding { get; set; }

    /// <summary>
    /// Url or path to a user specified style sheet. Default = ""
    /// </summary>
    [WkHtml("web.userStyleSheet")]
    public string? UserStyleSheet { get; set; }

    /// <summary>
    /// Should we enable NS plugins. Enabling this will have limited success. Default = false
    /// </summary>
    [WkHtml("web.enablePlugins")]
    public bool? enablePlugins { get; set; }
}

public class LoadSettings : ISettings
{
    /// <summary>
    /// The user name to use when loging into a website. Default = ""
    /// </summary>
    [WkHtml("load.username")]
    public string? Username { get; set; }

    /// <summary>
    /// The password to used when logging into a website. Default = ""
    /// </summary>
    [WkHtml("load.password")]
    public string? Password { get; set; }

    /// <summary>
    /// The mount of time in milliseconds to wait after a page has done loading until it is actually printed. E.g. "1200". We will wait this amount of time or until, javascript calls window.print(). Default = 200
    /// </summary>
    [WkHtml("load.jsdelay")]
    public int? JSDelay { get; set; }

    /// <summary>
    /// How much should we zoom in on the content. Default = 1.0
    /// </summary>
    [WkHtml("load.zoomFactor")]
    public double? ZoomFactor { get; set; }

    /// <summary>
    /// Disallow local and piped files to access other local files. Default = false
    /// </summary>
    [WkHtml("load.blockLocalFileAccess")]
    public bool? BlockLocalFileAccess { get; set; }

    /// <summary>
    /// Stop slow running javascript. Default = true
    /// </summary>
    [WkHtml("load.stopSlowScript")]
    public bool? StopSlowScript { get; set; }

    /// <summary>
    /// Forward javascript warnings and errors to the warning callback. Default = false
    /// </summary>
    [WkHtml("load.debugJavascript")]
    public bool? DebugJavascript { get; set; }

    /// <summary>
    /// How should we handle obejcts that fail to load. Default = Abort
    /// </summary>
    [WkHtml("load.loadErrorHandling")]
    public ContentErrorHandling? LoadErrorHandling { get; set; }

    /// <summary>
    /// String describing what proxy to use when loading the object. Default = ""
    /// </summary>
    [WkHtml("load.proxy")]
    public string? Proxy { get; set; }

    /// <summary>
    /// Custom headers used when requesting page. Defaulty = empty
    /// </summary>
    [WkHtml("load.customHeaders")]
    public Dictionary<string, string>? CustomHeaders { get; set; }

    /// <summary>
    /// Should the custom headers be sent all elements loaded instead of only the main page. Default = false
    /// </summary>
    [WkHtml("load.repeatCustomHeaders")]
    public bool? RepeatCustomHeaders { get; set; }

    /// <summary>
    /// Cookies used when requesting page. Default = empty
    /// </summary>
    [WkHtml("load.cookies")]
    public Dictionary<string, string>? Cookies { get; set; }
}

public class HeaderSettings : ISettings
{
    /// <summary>
    /// The font size to use for the header. Default = 12
    /// </summary>
    [WkHtml("header.fontSize")]
    public int? FontSize { get; set; }

    /// <summary>
    /// The name of the font to use for the header. Default = "Ariel"
    /// </summary>
    [WkHtml("header.fontName")]
    public string? FontName { get; set; }

    /// <summary>
    /// The string to print in the left part of the header, note that some sequences are replaced in this string, see the wkhtmltopdf manual. Default = ""
    /// </summary>
    [WkHtml("header.left")]
    public string? Left { get; set; }

    /// <summary>
    /// The text to print in the right part of the header, note that some sequences are replaced in this string, see the wkhtmltopdf manual. Default = ""
    /// </summary>
    [WkHtml("header.center")]
    public string? Center { get; set; }

    /// <summary>
    /// The text to print in the right part of the header, note that some sequences are replaced in this string, see the wkhtmltopdf manual. Default = ""
    /// </summary>
    [WkHtml("header.right")]
    public string? Right { get; set; }

    /// <summary>
    /// Whether a line should be printed under the header. Default = false
    /// </summary>
    [WkHtml("header.line")]
    public bool? Line { get; set; }

    /// <summary>
    /// The amount of space to put between the header and the content, e.g. "1.8". Be aware that if this is too large the header will be printed outside the pdf document. This can be corrected with the margin.top setting. Default = 0.00
    /// </summary>
    [WkHtml("header.spacing")]
    public double? Spacing { get; set; }

    /// <summary>
    /// Url for a HTML document to use for the header. Default = ""
    /// </summary>
    [WkHtml("header.htmlUrl")]
    public string? HtmUrl { get; set; }
}

public class GlobalSettings : ISettings
{


    private MarginSettings margins = new MarginSettings();

    /// <summary>
    /// The orientation of the output document, must be either "Landscape" or "Portrait". Default = "portrait"
    /// </summary>
    [WkHtml("orientation")]
    public Orientation? Orientation { get; set; }

    /// <summary>
    /// Should the output be printed in color or gray scale, must be either "Color" or "Grayscale". Default = "color"
    /// </summary>
    [WkHtml("colorMode")]
    public ColorMode? ColorMode { get; set; }

    /// <summary>
    /// Should we use loss less compression when creating the pdf file. Default = true
    /// </summary>
    [WkHtml("useCompression")]
    public bool? UseCompression { get; set; }

    /// <summary>
    /// What dpi should we use when printing. Default = 96
    /// </summary>
    [WkHtml("dpi")]
    public int? DPI { get; set; }

    /// <summary>
    /// A number that is added to all page numbers when printing headers, footers and table of content. Default = 0
    /// </summary>
    [WkHtml("pageOffset")]
    public int? PageOffset { get; set; }

    /// <summary>
    /// How many copies should we print. Default = 1
    /// </summary>
    [WkHtml("copies")]
    public int? Copies { get; set; }

    /// <summary>
    /// Should the copies be collated. Default = true
    /// </summary>
    [WkHtml("collate")]
    public bool? Collate { get; set; }

    /// <summary>
    /// Should a outline (table of content in the sidebar) be generated and put into the PDF. Default = true
    /// </summary>
    [WkHtml("outline")]
    public bool? Outline { get; set; }

    /// <summary>
    /// The maximal depth of the outline. Default = 4
    /// </summary>
    [WkHtml("outlineDepth")]
    public int? OutlineDepth { get; set; }

    /// <summary>
    /// If not set to the empty string a XML representation of the outline is dumped to this file. Default = ""
    /// </summary>
    [WkHtml("dumpOutline")]
    public string? DumpOutline { get; set; }

    /// <summary>
    /// The path of the output file, if "-" output is sent to stdout, if empty the output is stored in a buffer. Default = ""
    /// </summary>
    [WkHtml("out")]
    public string? Out { get; set; }

    /// <summary>
    /// The title of the PDF document. Default = ""
    /// </summary>
    [WkHtml("documentTitle")]
    public string? DocumentTitle { get; set; }

    /// <summary>
    /// The maximal DPI to use for images in the pdf document. Default = 600
    /// </summary>
    [WkHtml("imageDPI")]
    public int? ImageDPI { get; set; }

    /// <summary>
    /// The jpeg compression factor to use when producing the pdf document. Default = 94
    /// </summary>
    [WkHtml("imageQuality")]
    public int? ImageQuality { get; set; }

    /// <summary>
    /// Path of file used to load and store cookies. Default = ""
    /// </summary>
    [WkHtml("load.cookieJar")]
    public string? CookieJar { get; set; }

    /// <summary>
    /// Size of output paper
    /// </summary>
    public PechkinPaperSize? PaperSize { get; set; }

    /// <summary>
    /// The height of the output document
    /// </summary>
    [WkHtml("size.height")]
    private string? PaperHeight
    {
        get
        {
            return PaperSize == null ? null : PaperSize.Height;
        }
    }

    /// <summary>
    /// The width of the output document
    /// </summary>
    [WkHtml("size.width")]
    private string? PaperWidth
    {
        get
        {
            return PaperSize == null ? null : PaperSize.Width;
        }
    }

    public MarginSettings Margins
    {
        get
        {
            return this.margins;
        }
        set
        {
            margins = value;
        }
    }

    /// <summary>
    /// Size of the left margin
    /// </summary>
    [WkHtml("margin.left")]
    private string? MarginLeft
    {
        get
        {
            return margins.GetMarginValue(margins.Left);
        }
    }

    /// <summary>
    /// Size of the right margin
    /// </summary>
    [WkHtml("margin.right")]
    private string? MarginRight
    {
        get
        {
            return margins.GetMarginValue(margins.Right);
        }
    }

    /// <summary>
    /// Size of the top margin
    /// </summary>
    [WkHtml("margin.top")]
    private string? MarginTop
    {
        get
        {
            return margins.GetMarginValue(margins.Top);
        }
    }

    /// <summary>
    /// Size of the bottom margin
    /// </summary>
    [WkHtml("margin.bottom")]
    private string? MarginBottom
    {
        get
        {
            return margins.GetMarginValue(margins.Bottom);
        }
    }

    /// <summary>
    /// Set viewport size. Not supported in wkhtmltopdf API since v0.12.2.4 
    /// </summary>
    [WkHtml("viewportSize")]
    public string? ViewportSize { get; set; }
}

public class FooterSettings : ISettings
{
    /// <summary>
    /// The font size to use for the footer. Default = 12
    /// </summary>
    [WkHtml("footer.fontSize")]
    public int? FontSize { get; set; }

    /// <summary>
    /// The name of the font to use for the footer. Default = "Ariel"
    /// </summary>
    [WkHtml("footer.fontName")]
    public string? FontName { get; set; }

    /// <summary>
    /// The string to print in the left part of the footer, note that some sequences are replaced in this string, see the wkhtmltopdf manual. Default = ""
    /// </summary>
    [WkHtml("footer.left")]
    public string? Left { get; set; }

    /// <summary>
    /// The text to print in the right part of the footer, note that some sequences are replaced in this string, see the wkhtmltopdf manual. Default = ""
    /// </summary>
    [WkHtml("footer.center")]
    public string? Center { get; set; }

    /// <summary>
    /// The text to print in the right part of the footer, note that some sequences are replaced in this string, see the wkhtmltopdf manual. Default = ""
    /// </summary>
    [WkHtml("footer.right")]
    public string? Right { get; set; }

    /// <summary>
    /// Whether a line should be printed above the footer. Default = false
    /// </summary>
    [WkHtml("footer.line")]
    public bool? Line { get; set; }

    /// <summary>
    /// The amount of space to put between the footer and the content, e.g. "1.8". Be aware that if this is too large the footer will be printed outside the pdf document. This can be corrected with the margin.bottom setting. Default = 0.00
    /// </summary>
    [WkHtml("footer.spacing")]
    public double? Spacing { get; set; }

    /// <summary>
    /// Url for a HTML document to use for the footer. Default = ""
    /// </summary>
    [WkHtml("footer.htmlUrl")]
    public string? HtmUrl { get; set; }
}
