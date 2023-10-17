﻿using Microsoft.Extensions.DependencyInjection;
using System.Text.RegularExpressions;

namespace RazorPDF;

public class PDFBuilder : IDisposable
{
    private readonly RazorViewToStringRenderer _renderer;
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
        scope = RazorPDFExtensions.ServiceProvider.CreateScope();
        _renderer = scope.ServiceProvider.GetRequiredService<RazorViewToStringRenderer>();
    }

    public PDFBuilder RazorView<TModel>(string view, TModel model)
    {
        this._view = view;
        this._model = model;
        return this;
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

        IncludeCSS = File.ReadAllText(file);

        return this;
    }

    public async Task<string> BuildHTMLAsync()
    {
        var _content = await _renderer.RenderViewToStringAsync(_view, _model);

        if (!string.IsNullOrEmpty(IncludeCSS))
        {
            _content = Regex.Replace(_content, @"</head>", $"<style>{Environment.NewLine}{IncludeCSS}{Environment.NewLine}</style>" + Environment.NewLine + "</head>");
        }

        return _content;
    }

    public async Task<byte[]> BuildAsync(Action<ObjectSettings>? settings = null)
    {
        if (settings != null)
        {
            this._ObjectSettings = new ObjectSettings();
            settings(this.ObjectSettings);
        }

        var _content = await BuildHTMLAsync();

        var converter = new ThreadSafeHTMLToPDFConverter();

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
