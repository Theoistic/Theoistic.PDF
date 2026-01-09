# Theoistic.PDF

Theoistic.PDF is a powerful library for .NET that provides capabilities to build PDF documents using HTML. With Theoistic.PDF, you can create complex PDF documents, It's perfect for generating invoices, reports, forms, and more!

## Features
- Generate PDFs using HTML.
- Inject CSS for styling your PDFs.
- Comprehensive PDF settings like compression, size, orientation, and more.
- Asynchronous methods for building PDFs.

## Installation

You can add Theoistic.PDF to your project via the NuGet package manager. Use the following command in your Package Manager Console:

```
Install-Package Theoistic.PDF
```

## Configuration

To use Theoistic.PDF in your project, you need to configure the services and application builder typically in your `Startup.cs`.

```csharp
public void ConfigureServices(IServiceCollection services)
{
    services.AddTheoisticPDF();
}

public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
{
    app.UseTheoisticPDF();
}
```

### Style

If you need to have a specific style, I would recommend using a CSS file and injecting it using the InjectCSS method.
the InjectCSS method takes a string as a parameter, this string is the (relative) path to the CSS file you want to inject.
since we cannot use relative paths, its converted to absolute path and the link stylesheet is injected right above the closing head tag.
Once the CSS has been injected with a full absolute path, it can reference relative images and fonts.

## Contributing

Contributions to the Theoistic.PDF library are welcome! If you're interested in improving Theoistic.PDF
