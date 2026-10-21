using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Theoistic.PDF.Rendering;

/// <summary>
/// Translates the library's settings into the parameters of the DevTools
/// <c>Page.printToPDF</c> command.
/// <para>
/// Not everything carries over. The settings were modelled on wkhtmltox, and some of them
/// describe either that engine's internals or a physical printer. The entries listed in
/// <see cref="UnsupportedSettings"/> are accepted and ignored rather than removed, so that code
/// written against the old API still compiles.
/// </para>
/// </summary>
internal static class PrintSettingsMapper
{
    /// <summary>
    /// Settings with no equivalent in a browser-based renderer, documented here so the gap is
    /// stated in one place rather than discovered per property.
    /// </summary>
    internal static readonly string[] UnsupportedSettings =
    {
        // A browser prints in colour; converting to grey needs a pass over the output.
        nameof(GlobalSettings.ColorMode),

        // Chromium always compresses its streams and exposes no knob for resolution or
        // image quality.
        nameof(GlobalSettings.UseCompression),
        nameof(GlobalSettings.DPI),
        nameof(GlobalSettings.ImageDPI),
        nameof(GlobalSettings.ImageQuality),

        // Printer concepts, not document ones.
        nameof(GlobalSettings.Copies),
        nameof(GlobalSettings.Collate),

        // Chromium generates no document outline when printing.
        nameof(GlobalSettings.Outline),
        nameof(GlobalSettings.OutlineDepth),
        nameof(GlobalSettings.DumpOutline),

        // wkhtmltox-specific layout behaviour.
        nameof(WebSettings.EnableIntelligentShrinking),
        nameof(WebSettings.MinimumFontSize),
        nameof(WebSettings.enablePlugins),

        // Links and form fields are produced by the browser on its own terms.
        nameof(ObjectSettings.UseExternalLinks),
        nameof(ObjectSettings.UseLocalLinks),
        nameof(ObjectSettings.ProduceForms),
        nameof(ObjectSettings.IncludeInOutline),
        nameof(ObjectSettings.PagesCount),

        // Browser-wide rather than per document, because it is a launch flag: use
        // PdfRendererOptions.AllowFileAccess instead.
        nameof(LoadSettings.BlockLocalFileAccess),

        // No equivalent in the protocol.
        nameof(LoadSettings.StopSlowScript),
        nameof(LoadSettings.DebugJavascript),
        nameof(LoadSettings.RepeatCustomHeaders),
        nameof(GlobalSettings.CookieJar),
    };

    private const double DefaultWidthInches = 210 / 25.4;
    private const double DefaultHeightInches = 297 / 25.4;

    /// <summary>
    /// Builds the command parameters. Only keys the settings actually determine are included,
    /// so that anything left unset keeps the browser's own default.
    /// </summary>
    public static Dictionary<string, object?> ToPrintParameters(GlobalSettings global, ObjectSettings content)
    {
        ArgumentNullException.ThrowIfNull(global);
        ArgumentNullException.ThrowIfNull(content);

        (double width, double height) = ResolvePaperSize(global);

        // Chromium ignores the landscape flag when explicit page dimensions are given, so the
        // rotation is applied to the dimensions instead of asked for.
        if (global.Orientation == Orientation.Landscape)
        {
            (width, height) = (height, width);
        }

        var parameters = new Dictionary<string, object?>
        {
            ["paperWidth"] = width,
            ["paperHeight"] = height,
            ["landscape"] = false,

            // The settings are the authority on page size here. Letting a stylesheet's @page
            // rule win would silently override whatever the caller configured.
            ["preferCSSPageSize"] = false,

            // wkhtmltox printed backgrounds by default and callers' stylesheets assume it.
            ["printBackground"] = content.WebSettings.Background ?? true,

            ["marginTop"] = MarginInches(global.Margins, global.Margins.Top),
            ["marginBottom"] = MarginInches(global.Margins, global.Margins.Bottom),
            ["marginLeft"] = MarginInches(global.Margins, global.Margins.Left),
            ["marginRight"] = MarginInches(global.Margins, global.Margins.Right),
        };

        if (content.LoadSettings.ZoomFactor is { } zoom)
        {
            // The protocol rejects anything outside this range outright.
            parameters["scale"] = Math.Clamp(zoom, 0.1, 2.0);
        }

        HeaderSettings head = content.HeaderSettings;
        FooterSettings foot = content.FooterSettings;

        string? header = BuildTemplate(
            head.Left, head.Center, head.Right, head.FontSize, head.FontName, head.Line, head.HtmUrl, isHeader: true);

        string? footer = BuildTemplate(
            foot.Left, foot.Center, foot.Right, foot.FontSize, foot.FontName, foot.Line, foot.HtmUrl, isHeader: false);

        if (header is not null || footer is not null)
        {
            parameters["displayHeaderFooter"] = true;

            // An omitted template falls back to Chromium's own, which prints a title and a url
            // nobody asked for, so both are always supplied.
            parameters["headerTemplate"] = header ?? "<span></span>";
            parameters["footerTemplate"] = footer ?? "<span></span>";
        }
        else
        {
            parameters["displayHeaderFooter"] = false;
        }

        return parameters;
    }

    /// <summary>
    /// Page dimensions in inches. Defaults to A4, which is what the builder has always defaulted
    /// to, rather than Chromium's Letter.
    /// </summary>
    private static (double Width, double Height) ResolvePaperSize(GlobalSettings global)
    {
        if (global.PaperSize is not { } size)
        {
            return (DefaultWidthInches, DefaultHeightInches);
        }

        return (ToInches(size.Width, nameof(size.Width)), ToInches(size.Height, nameof(size.Height)));
    }

    /// <summary>
    /// Parses the unit-suffixed dimension strings the paper table holds, such as "8.5in" or
    /// "210mm".
    /// </summary>
    internal static double ToInches(string value, string what)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        Match match = Regex.Match(
            value.Trim(),
            @"^(?<number>-?\d+(?:\.\d+)?)\s*(?<unit>in|mm|cm|pt|px)?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        if (!match.Success)
        {
            throw new PdfConversionException(
                $"'{value}' is not a usable {what}. Expected a number with a unit, such as \"210mm\" or \"8.5in\".");
        }

        double number = double.Parse(
            match.Groups["number"].Value,
            CultureInfo.InvariantCulture);

        string unit = match.Groups["unit"].Success
            ? match.Groups["unit"].Value.ToLowerInvariant()

            // A bare number is rejected rather than guessed: treating 210 as inches or as
            // millimetres differ by a factor of twenty-five, and a silently wrong page size is
            // worse than a failure here.
            : throw new PdfConversionException(
                $"'{value}' is missing a unit for {what}. Use \"210mm\", \"8.5in\", \"21cm\", \"595pt\" or \"794px\".");

        return unit switch
        {
            "in" => number,
            "mm" => number / 25.4,
            "cm" => number / 2.54,
            "pt" => number / 72.0,
            "px" => number / 96.0,
            _ => throw new PdfConversionException($"Unknown unit '{unit}' in {what}.")
        };
    }

    /// <summary>
    /// Margins in inches. Defaults to 10mm on each side, which is roughly what wkhtmltox used
    /// and keeps existing documents looking the same.
    /// </summary>
    private static double MarginInches(MarginSettings margins, double? value)
    {
        if (value is not { } amount)
        {
            return 10 / 25.4;
        }

        return margins.Unit switch
        {
            Unit.Inches => amount,
            Unit.Millimeters => amount / 25.4,
            Unit.Centimeters => amount / 2.54,
            _ => amount
        };
    }

    /// <summary>
    /// Builds a header or footer template from the three-part layout, or returns null when
    /// nothing was configured.
    /// </summary>
    private static string? BuildTemplate(
        string? left,
        string? center,
        string? right,
        int? fontSize,
        string? fontName,
        bool? line,
        string? htmlUrl,
        bool isHeader)
    {
        if (htmlUrl is { Length: > 0 })
        {
            return ReadTemplateFile(htmlUrl, isHeader);
        }

        if (left is not { Length: > 0 } && center is not { Length: > 0 } && right is not { Length: > 0 })
        {
            return null;
        }

        // Chromium renders the template in a bare document with a tiny default font, so the
        // font has to be stated or the text comes out unreadably small.
        var style = new StringBuilder("width:100%;display:flex;box-sizing:border-box;padding:0 0.4in;");

        style.Append(CultureInfo.InvariantCulture, $"font-size:{fontSize ?? 9}px;");
        style.Append(CultureInfo.InvariantCulture, $"font-family:{CssString(fontName ?? "Arial")};");

        if (line == true)
        {
            style.Append(isHeader
                ? "border-bottom:0.5px solid #000;padding-bottom:2px;"
                : "border-top:0.5px solid #000;padding-top:2px;");
        }

        var template = new StringBuilder();

        template.Append(CultureInfo.InvariantCulture, $"<div style=\"{style}\">");
        template.Append(CultureInfo.InvariantCulture, $"<div style=\"flex:1;text-align:left\">{Placeholders(left)}</div>");
        template.Append(CultureInfo.InvariantCulture, $"<div style=\"flex:1;text-align:center\">{Placeholders(center)}</div>");
        template.Append(CultureInfo.InvariantCulture, $"<div style=\"flex:1;text-align:right\">{Placeholders(right)}</div>");
        template.Append("</div>");

        return template.ToString();
    }

    private static string ReadTemplateFile(string location, bool isHeader)
    {
        string what = isHeader ? "header" : "footer";

        if (Uri.TryCreate(location, UriKind.Absolute, out Uri? uri) && !uri.IsFile)
        {
            throw new PdfConversionException(
                $"The {what} HTML must be a local file; '{location}' is a remote address. Fetch it yourself and assign the markup to Left, Center or Right, or pass a file path.");
        }

        string path = uri?.IsFile == true ? uri.LocalPath : location;

        if (!File.Exists(path))
        {
            throw new PdfConversionException($"The {what} HTML file was not found: {path}");
        }

        return File.ReadAllText(path);
    }

    /// <summary>
    /// Rewrites wkhtmltox's bracket placeholders as the spans Chromium substitutes, and escapes
    /// everything around them so that a stray angle bracket in a caller's text cannot break the
    /// template.
    /// </summary>
    internal static string Placeholders(string? text)
    {
        if (text is not { Length: > 0 })
        {
            return string.Empty;
        }

        var output = new StringBuilder();
        int index = 0;

        foreach (Match match in Regex.Matches(text, @"\[(\w+)\]", RegexOptions.CultureInvariant))
        {
            output.Append(WebUtility.HtmlEncode(text[index..match.Index]));

            output.Append(match.Groups[1].Value.ToLowerInvariant() switch
            {
                "page" or "sitepage" => "<span class=\"pageNumber\"></span>",
                "topage" or "sitepages" => "<span class=\"totalPages\"></span>",
                "date" or "isodate" or "time" => "<span class=\"date\"></span>",
                "title" or "doctitle" => "<span class=\"title\"></span>",
                "webpage" => "<span class=\"url\"></span>",

                // frompage, section and subsection have no counterpart; the literal text is
                // dropped rather than printed, which is what wkhtmltox did for an empty section.
                _ => string.Empty
            });

            index = match.Index + match.Length;
        }

        output.Append(WebUtility.HtmlEncode(text[index..]));

        return output.ToString();
    }

    /// <summary>Quotes a font family so a name containing spaces survives into CSS.</summary>
    private static string CssString(string value) =>
        "'" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'";
}
