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

public class ObjectSettings
{
    /// <summary>
    /// The URL or path of the web page to convert, if "-" input is read from stdin. Default = ""
    /// </summary>
    public string? Page { get; set; }

    /// <summary>
    /// Should external links in the HTML document be converted into external pdf links. Default = true
    /// </summary>
    public bool? UseExternalLinks { get; set; }

    /// <summary>
    /// Should internal links in the HTML document be converted into pdf references. Default = true
    /// </summary>
    public bool? UseLocalLinks { get; set; }

    /// <summary>
    /// Should we turn HTML forms into PDF forms. Default = false
    /// </summary>
    public bool? ProduceForms { get; set; }

    /// <summary>
    /// Should the sections from this document be included in the outline and table of content. Default = false
    /// </summary>
    public bool? IncludeInOutline { get; set; }

    /// <summary>
    /// Should we count the pages of this document, in the counter used for TOC, headers and footers. Default = false
    /// </summary>
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

public class WebSettings
{
    /// <summary>
    /// Should we print the background. Default = true
    /// </summary>
    public bool? Background { get; set; }

    /// <summary>
    /// Should we load images. Default = true
    /// </summary>
    public bool? LoadImages { get; set; }

    /// <summary>
    /// Should we enable javascript. Default = true
    /// </summary>
    public bool? EnableJavascript { get; set; }

    /// <summary>
    /// Should we enable intelligent shrinkng to fit more content on one page. Has no effect for wkhtmltoimage. Default = true
    /// </summary>
    public bool? EnableIntelligentShrinking { get; set; }

    /// <summary>
    /// The minimum font size allowed. Default = -1
    /// </summary>
    public int? MinimumFontSize { get; set; }

    /// <summary>
    /// Should the content be printed using the print media type instead of the screen media type. Default = false
    /// </summary>
    public bool? PrintMediaType { get; set; }

    /// <summary>
    /// What encoding should we guess content is using if they do not specify it properly. Default = ""
    /// </summary>
    public string? DefaultEncoding { get; set; }

    /// <summary>
    /// Url or path to a user specified style sheet. Default = ""
    /// </summary>
    public string? UserStyleSheet { get; set; }

    /// <summary>
    /// Should we enable NS plugins. Enabling this will have limited success. Default = false
    /// </summary>
    public bool? enablePlugins { get; set; }
}

public class LoadSettings
{
    /// <summary>
    /// The user name to use when loging into a website. Default = ""
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// The password to used when logging into a website. Default = ""
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// The mount of time in milliseconds to wait after a page has done loading until it is actually printed. E.g. "1200". We will wait this amount of time or until, javascript calls window.print(). Default = 200
    /// </summary>
    public int? JSDelay { get; set; }

    /// <summary>
    /// How much should we zoom in on the content. Default = 1.0
    /// </summary>
    public double? ZoomFactor { get; set; }

    /// <summary>
    /// Disallow local and piped files to access other local files. Default = false
    /// <para>
    /// Not honoured per document: local file access is a browser-wide launch decision. Set
    /// <see cref="Rendering.PdfRendererOptions.AllowFileAccess"/> instead.
    /// </para>
    /// </summary>
    public bool? BlockLocalFileAccess { get; set; }

    /// <summary>
    /// Stop slow running javascript. Default = true
    /// </summary>
    public bool? StopSlowScript { get; set; }

    /// <summary>
    /// Forward javascript warnings and errors to the warning callback. Default = false
    /// </summary>
    public bool? DebugJavascript { get; set; }

    /// <summary>
    /// How should we handle obejcts that fail to load. Default = Abort
    /// </summary>
    public ContentErrorHandling? LoadErrorHandling { get; set; }

    /// <summary>
    /// String describing what proxy to use when loading the object. Default = ""
    /// </summary>
    public string? Proxy { get; set; }

    /// <summary>
    /// Custom headers used when requesting page. Defaulty = empty
    /// </summary>
    public Dictionary<string, string>? CustomHeaders { get; set; }

    /// <summary>
    /// Should the custom headers be sent all elements loaded instead of only the main page. Default = false
    /// </summary>
    public bool? RepeatCustomHeaders { get; set; }

    /// <summary>
    /// Cookies used when requesting page. Default = empty
    /// </summary>
    public Dictionary<string, string>? Cookies { get; set; }
}

public class HeaderSettings
{
    /// <summary>
    /// The font size to use for the header. Default = 12
    /// </summary>
    public int? FontSize { get; set; }

    /// <summary>
    /// The name of the font to use for the header. Default = "Ariel"
    /// </summary>
    public string? FontName { get; set; }

    /// <summary>
    /// The string to print in the left part of the header, note that some sequences are replaced in this string, see the wkhtmltopdf manual. Default = ""
    /// </summary>
    public string? Left { get; set; }

    /// <summary>
    /// The text to print in the right part of the header, note that some sequences are replaced in this string, see the wkhtmltopdf manual. Default = ""
    /// </summary>
    public string? Center { get; set; }

    /// <summary>
    /// The text to print in the right part of the header, note that some sequences are replaced in this string, see the wkhtmltopdf manual. Default = ""
    /// </summary>
    public string? Right { get; set; }

    /// <summary>
    /// Whether a line should be printed under the header. Default = false
    /// </summary>
    public bool? Line { get; set; }

    /// <summary>
    /// The amount of space to put between the header and the content, e.g. "1.8". Be aware that if this is too large the header will be printed outside the pdf document. This can be corrected with the margin.top setting. Default = 0.00
    /// </summary>
    public double? Spacing { get; set; }

    /// <summary>
    /// Url for a HTML document to use for the header. Default = ""
    /// </summary>
    public string? HtmUrl { get; set; }
}

public class GlobalSettings
{


    private MarginSettings margins = new MarginSettings();

    /// <summary>
    /// The orientation of the output document, must be either "Landscape" or "Portrait". Default = "portrait"
    /// </summary>
    public Orientation? Orientation { get; set; }

    /// <summary>
    /// Should the output be printed in color or gray scale, must be either "Color" or "Grayscale". Default = "color"
    /// </summary>
    public ColorMode? ColorMode { get; set; }

    /// <summary>
    /// Should we use loss less compression when creating the pdf file. Default = true
    /// </summary>
    public bool? UseCompression { get; set; }

    /// <summary>
    /// What dpi should we use when printing. Default = 96
    /// </summary>
    public int? DPI { get; set; }

    /// <summary>
    /// A number that is added to all page numbers when printing headers, footers and table of content. Default = 0
    /// </summary>
    public int? PageOffset { get; set; }

    /// <summary>
    /// How many copies should we print. Default = 1
    /// </summary>
    public int? Copies { get; set; }

    /// <summary>
    /// Should the copies be collated. Default = true
    /// </summary>
    public bool? Collate { get; set; }

    /// <summary>
    /// Should a outline (table of content in the sidebar) be generated and put into the PDF. Default = true
    /// </summary>
    public bool? Outline { get; set; }

    /// <summary>
    /// The maximal depth of the outline. Default = 4
    /// </summary>
    public int? OutlineDepth { get; set; }

    /// <summary>
    /// If not set to the empty string a XML representation of the outline is dumped to this file. Default = ""
    /// </summary>
    public string? DumpOutline { get; set; }

    /// <summary>
    /// The path of the output file, if "-" output is sent to stdout, if empty the output is stored in a buffer. Default = ""
    /// </summary>
    public string? Out { get; set; }

    /// <summary>
    /// The title of the PDF document, and the value substituted for the <c>[title]</c>
    /// placeholder in headers and footers.
    /// <para>
    /// Worth setting explicitly. When it is left unset the browser records the document's own
    /// title element, and when the markup has none it falls back to the document's file name -
    /// so markup passed as content ends up titled "document.html".
    /// </para>
    /// </summary>
    public string? DocumentTitle { get; set; }

    /// <summary>
    /// The maximal DPI to use for images in the pdf document. Default = 600
    /// </summary>
    public int? ImageDPI { get; set; }

    /// <summary>
    /// The jpeg compression factor to use when producing the pdf document. Default = 94
    /// </summary>
    public int? ImageQuality { get; set; }

    /// <summary>
    /// Path of file used to load and store cookies. Default = ""
    /// </summary>
    public string? CookieJar { get; set; }

    /// <summary>
    /// Size of output paper
    /// </summary>
    public PechkinPaperSize? PaperSize { get; set; }



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
    /// Set viewport size. Not supported in wkhtmltopdf API since v0.12.2.4 
    /// </summary>
    public string? ViewportSize { get; set; }
}

public class FooterSettings
{
    /// <summary>
    /// The font size to use for the footer. Default = 12
    /// </summary>
    public int? FontSize { get; set; }

    /// <summary>
    /// The name of the font to use for the footer. Default = "Ariel"
    /// </summary>
    public string? FontName { get; set; }

    /// <summary>
    /// The string to print in the left part of the footer, note that some sequences are replaced in this string, see the wkhtmltopdf manual. Default = ""
    /// </summary>
    public string? Left { get; set; }

    /// <summary>
    /// The text to print in the right part of the footer, note that some sequences are replaced in this string, see the wkhtmltopdf manual. Default = ""
    /// </summary>
    public string? Center { get; set; }

    /// <summary>
    /// The text to print in the right part of the footer, note that some sequences are replaced in this string, see the wkhtmltopdf manual. Default = ""
    /// </summary>
    public string? Right { get; set; }

    /// <summary>
    /// Whether a line should be printed above the footer. Default = false
    /// </summary>
    public bool? Line { get; set; }

    /// <summary>
    /// The amount of space to put between the footer and the content, e.g. "1.8". Be aware that if this is too large the footer will be printed outside the pdf document. This can be corrected with the margin.bottom setting. Default = 0.00
    /// </summary>
    public double? Spacing { get; set; }

    /// <summary>
    /// Url for a HTML document to use for the footer. Default = ""
    /// </summary>
    public string? HtmUrl { get; set; }
}
