using Microsoft.Extensions.DependencyInjection;
using System.Text.RegularExpressions;

namespace Theoistic.PDF;

public class PDFBuilder : IDisposable
{
    private readonly ThreadSafeHTMLToPDFConverter converter;
    private IServiceScope scope;

    private string IncludeCSS;

    private ObjectSettings _ObjectSettings;
    private ObjectSettings ObjectSettings
    {
        get
        {
            if(_ObjectSettings == null)
            {
                _ObjectSettings = new ObjectSettings()
                {
                    WebSettings = { DefaultEncoding = "utf-8" },
                };
            }
            return _ObjectSettings;
        }
        set
        {
            _ObjectSettings = value;
        }
    }

    private GlobalSettings _GlobalSettings;
    private GlobalSettings GlobalSettings
    {
        get
        {
            if(_GlobalSettings == null)
            {
                _GlobalSettings = new GlobalSettings
                {
                    ColorMode = ColorMode.Color,
                    Orientation = Orientation.Portrait,
                    PaperSize = PaperKind.A4,
                };
            }
            return _GlobalSettings;
        }
        set
        {
            _GlobalSettings = value;
        }
    }

    private string _view { get; set; }
    private object _model { get; set; }

    public PDFBuilder()
    {
        scope = TheoisticPDFExtensions.ServiceProvider.CreateScope();
        converter = scope.ServiceProvider.GetRequiredService<ThreadSafeHTMLToPDFConverter>();
    }

    public PDFBuilder Settings(Action<GlobalSettings> settings)
    {
        this._GlobalSettings = new GlobalSettings();
        settings(this._GlobalSettings);
        return this;
    }

    public PDFBuilder InjectCSS(string file)
    {
        if(!File.Exists(file))
            throw new FileNotFoundException("CSS file not found.", file);

        var fullPath = "file:///" + Path.GetFullPath(file).Replace("\\", "/").Replace(" ", "%20");

        IncludeCSS = $"<link href=\"{fullPath}\" rel=\"stylesheet\" type=\"text/css\" media=\"screen\">";


        return this;
    }

    public async Task<byte[]> BuildAsync(string html, Action<ObjectSettings>? settings = null)
    {
        if (settings != null)
        {
            this._ObjectSettings = new ObjectSettings();
            settings(this.ObjectSettings);
        }

        var _content = html;

        if (!string.IsNullOrEmpty(IncludeCSS))
        {
            _content = Regex.Replace(_content, @"</head>", $"{Environment.NewLine}{IncludeCSS}{Environment.NewLine}</head>");
        }

        ObjectSettings objSetting = this.ObjectSettings;
        objSetting.HtmlContent = _content;

        var doc = new HtmlToPdfDocument()
        {
            GlobalSettings = GlobalSettings,
            Objects = {
                objSetting
            }
        };
        return converter.Convert(doc);
    }

    public void Dispose()
    {
        scope.Dispose();
    }
}
