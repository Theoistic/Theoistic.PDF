using Microsoft.Extensions.DependencyInjection;

namespace Theoistic.PDF;

public class PDFBuilder : IDisposable
{
    private readonly ThreadSafeHTMLToPDFConverter converter;

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

    public PDFBuilder()
    {
        var services = TheoisticPDFExtensions.ServiceProvider
            ?? throw new InvalidOperationException(
                "Theoistic.PDF has not been initialised. Call services.AddTheoisticPDF() and app.UseTheoisticPDF() during startup before creating a PDFBuilder.");

        converter = services.GetRequiredService<ThreadSafeHTMLToPDFConverter>();
    }

    /// <summary>
    /// Configures the document-wide settings. The action mutates the defaults (A4, portrait,
    /// colour) rather than replacing them, so only what it touches changes.
    /// </summary>
    public PDFBuilder Settings(Action<GlobalSettings> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        settings(GlobalSettings);

        return this;
    }

    public PDFBuilder InjectCSS(string file)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);

        if (!File.Exists(file))
        {
            throw new FileNotFoundException("CSS file not found.", file);
        }

        // AbsoluteUri escapes spaces and every other character that is illegal in a URL,
        // which a manual %20 replacement does not.
        var href = new Uri(Path.GetFullPath(file)).AbsoluteUri;

        IncludeCSS = $"<link href=\"{href}\" rel=\"stylesheet\" type=\"text/css\" media=\"screen\">";

        return this;
    }

    /// <summary>
    /// Renders <paramref name="html"/> to PDF. <paramref name="settings"/> mutates the object
    /// settings defaults (UTF-8 encoding) rather than replacing them.
    /// </summary>
    public Task<byte[]> BuildAsync(string html, Action<ObjectSettings>? settings = null)
        => BuildAsync(html, settings, CancellationToken.None);

    /// <summary>
    /// Renders <paramref name="html"/> to PDF. The token only cancels the request while it is
    /// still queued; a conversion that has reached wkhtmltox cannot be interrupted.
    /// </summary>
    public Task<byte[]> BuildAsync(string html, Action<ObjectSettings>? settings, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(html);

        settings?.Invoke(ObjectSettings);

        ObjectSettings objSetting = ObjectSettings;
        objSetting.HtmlContent = InjectStyles(html);

        var doc = new HtmlToPdfDocument
        {
            GlobalSettings = GlobalSettings,
            Objects = { objSetting }
        };

        return converter.ConvertAsync(doc, cancellationToken);
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
        // The converter is a singleton owned by the container, so there is nothing per-builder
        // to release. Kept so existing `using` blocks continue to compile.
        disposed = true;

        GC.SuppressFinalize(this);
    }
}
