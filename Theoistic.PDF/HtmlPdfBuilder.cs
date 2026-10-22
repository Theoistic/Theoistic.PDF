using Microsoft.Extensions.DependencyInjection;
using Theoistic.PDF.Rendering;

namespace Theoistic.PDF;

/// <summary>
/// Renders HTML and CSS to PDF by driving an installed Chromium-family browser. For documents
/// composed without HTML or a browser, see <see cref="Layout.FlowDocument"/> and the templates in
/// <see cref="Templates"/>.
/// </summary>
public class HtmlPdfBuilder : IDisposable
{
    private readonly IPdfRenderer renderer;

    private string? IncludeCSS;
    private bool disposed;

    private ObjectSettings? _ObjectSettings;
    private ObjectSettings ObjectSettings
    {
        get
        {
            _ObjectSettings ??= new ObjectSettings
            {
                WebSettings = { DefaultEncoding = "utf-8" },
            };

            return _ObjectSettings;
        }
    }

    private GlobalSettings? _GlobalSettings;
    private GlobalSettings GlobalSettings
    {
        get
        {
            _GlobalSettings ??= new GlobalSettings
            {
                ColorMode = ColorMode.Color,
                Orientation = Orientation.Portrait,
                PaperSize = PaperKind.A4,
            };

            return _GlobalSettings;
        }
    }

    public HtmlPdfBuilder()
    {
        var services = TheoisticPDFExtensions.ServiceProvider
            ?? throw new InvalidOperationException(
                "Theoistic.PDF has not been initialised. Call services.AddTheoisticPDF() and app.UseTheoisticPDF() during startup before creating an HtmlPdfBuilder.");

        renderer = services.GetRequiredService<IPdfRenderer>();
    }

    /// <summary>
    /// Uses a renderer directly, for callers that resolve it themselves or want to substitute
    /// one in a test.
    /// </summary>
    public HtmlPdfBuilder(IPdfRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);

        this.renderer = renderer;
    }

    /// <summary>
    /// Configures the document-wide settings. The action mutates the defaults (A4, portrait,
    /// colour) rather than replacing them, so only what it touches changes.
    /// </summary>
    public HtmlPdfBuilder Settings(Action<GlobalSettings> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        settings(GlobalSettings);

        return this;
    }

    public HtmlPdfBuilder InjectCSS(string file)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);

        if (!File.Exists(file))
        {
            throw new FileNotFoundException("CSS file not found.", file);
        }

        // AbsoluteUri escapes spaces and every other character that is illegal in a URL,
        // which a manual %20 replacement does not.
        var href = new Uri(Path.GetFullPath(file)).AbsoluteUri;

        // media="all", not "screen". A screen-only stylesheet is ignored while the browser is
        // rendering print media, which is what printing a document does - so the injected CSS
        // would silently do nothing. Injecting a stylesheet is an instruction to style the PDF,
        // so it has to apply whichever media the page is rendered for.
        IncludeCSS = $"<link href=\"{href}\" rel=\"stylesheet\" type=\"text/css\" media=\"all\">";

        return this;
    }

    /// <summary>
    /// Renders <paramref name="html"/> to PDF. <paramref name="settings"/> mutates the object
    /// settings defaults (UTF-8 encoding) rather than replacing them.
    /// </summary>
    public Task<byte[]> BuildAsync(string html, Action<ObjectSettings>? settings = null)
        => BuildAsync(html, settings, CancellationToken.None);

    /// <summary>
    /// Renders <paramref name="html"/> to PDF.
    /// </summary>
    public async Task<byte[]> BuildAsync(string html, Action<ObjectSettings>? settings, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(html);

        settings?.Invoke(ObjectSettings);

        ObjectSettings objSetting = ObjectSettings;
        objSetting.HtmlContent = InjectStyles(html);

        byte[] pdf = await renderer.RenderAsync(GlobalSettings, objSetting, cancellationToken)
            .ConfigureAwait(false);

        if (GlobalSettings.Out is { Length: > 0 } path)
        {
            // wkhtmltox used to write this file itself. The renderer deals only in bytes, so
            // the setting is honoured here instead.
            await File.WriteAllBytesAsync(path, pdf, cancellationToken).ConfigureAwait(false);
        }

        return pdf;
    }

    private string InjectStyles(string html)
    {
        if (string.IsNullOrEmpty(IncludeCSS))
        {
            return html;
        }

        // Plain string work rather than Regex.Replace: the link tag is a replacement string
        // there, so a path containing '$' would be treated as a substitution pattern.
        int head = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);

        if (head < 0)
        {
            // No head to inject into - a bare fragment still renders with a leading link tag.
            return IncludeCSS + Environment.NewLine + html;
        }

        return html.Insert(head, string.Concat(Environment.NewLine, IncludeCSS, Environment.NewLine));
    }

    public void Dispose()
    {
        // The renderer is a singleton owned by the container, so there is nothing per-builder
        // to release. Kept so existing `using` blocks continue to compile.
        disposed = true;

        GC.SuppressFinalize(this);
    }
}
