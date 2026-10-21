# Theoistic.PDF

PDF generation for .NET with **no bundled engine, no native binaries and no copyleft**.

The package is pure managed code. It ships nothing to install, downloads nothing at runtime, and
carries no LGPL or AGPL obligations — which is unusual in this corner of the .NET ecosystem,
where most options are either a 40 MB native library or a dual licence with a revenue threshold.

It gives you two ways to make a PDF, and you can use either or both:

| | What it does | Needs a browser? |
|---|---|---|
| **`Theoistic.PDF.Writing`** | Compose a document directly — pages, text, paths, colour, measured text | **No.** Works anywhere .NET runs |
| **`PDFBuilder`** | Render HTML + CSS to PDF | **Yes.** Drives an installed Chrome/Edge/Chromium |

The HTML path has full modern fidelity — flexbox, grid, web fonts, CJK, Arabic shaping, emoji —
because it drives a real, current browser rather than a frozen engine. The cost is that a browser
has to be present. **If you are deploying to Docker, Azure or DigitalOcean, read
[Deployment](#deployment) before you ship.**

- Targets `net9.0` and `net10.0`
- Dependencies: `Microsoft.AspNetCore.Http.Abstractions`, `Microsoft.Extensions.DependencyInjection.Abstractions` — both MIT
- Licence: MIT

## Install

```bash
dotnet add package Theoistic.PDF
```

## Quick start — HTML to PDF

Register the renderer once at startup. Both calls are needed: `AddTheoisticPDF` registers the
services, `UseTheoisticPDF` lets the parameterless `PDFBuilder()` constructor find them.

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTheoisticPDF();

var app = builder.Build();

app.UseTheoisticPDF();
```

Outside ASP.NET Core — a worker, a console app, a test — there is an `IServiceProvider` overload:

```csharp
var services = new ServiceCollection();
services.AddTheoisticPDF();

ServiceProvider provider = services.BuildServiceProvider();
provider.UseTheoisticPDF();
```

Then build a document:

```csharp
using var pdfBuilder = new PDFBuilder();

byte[] pdf = await pdfBuilder
    .Settings(settings =>
    {
        settings.PaperSize = PaperKind.A4;
        settings.Orientation = Orientation.Portrait;
        settings.DocumentTitle = "Invoice 2026-0042";
        settings.Margins = new MarginSettings { Unit = Unit.Millimeters, Top = 20, Bottom = 18, Left = 15, Right = 15 };
    })
    .BuildAsync("<html><body><h1>Hello</h1></body></html>");
```

A browser is launched on first use and reused. Each render gets its own tab, so concurrent
requests do not interfere, and a browser that dies is replaced on the next render.

### Check at startup, not on the first request

A missing browser is a deployment problem, and you want to find out about it when the app starts
rather than when a user asks for a document:

```csharp
var renderer = app.Services.GetRequiredService<IPdfRenderer>();
PdfRendererStatus status = await renderer.CheckAsync();

if (!status.IsAvailable)
{
    app.Logger.LogError("No PDF renderer: {Reason}", status.Description);
}
```

`Description` names the browser and version when it works (`Edg/155.0.4283.62 (C:\Program Files
(x86)\Microsoft\Edge\Application\msedge.exe)`), and says what it probed and failed to find when
it does not.

## Quick start — PDF without a browser

`Theoistic.PDF.Writing` composes a document directly. No HTML, no browser, no external anything —
useful for backends with no browser available, and for documents where you want exact control
over placement.

```csharp
using Theoistic.PDF.Writing;

var document = new PdfDocument();
document.Info.Title = "Statement";

PdfPage page = document.AddPage(PdfPaperSize.A4);

page.Content
    .SetFillColor(PdfColor.FromHex("#1f3a5f"))
    .Rectangle(0, page.Height - 96, page.Width, 96)
    .Fill();

page.Content.DrawText("STATEMENT", StandardFont.HelveticaBold, 28, 56, page.Height - 56);

// Text can be measured, so right-aligning is arithmetic rather than guesswork.
const string total = "2,580.00";
double width = StandardFont.HelveticaBold.GetTextWidth(total, 12);

page.Content.DrawText(total, StandardFont.HelveticaBold, 12, page.Width - 56 - width, 200);

byte[] pdf = document.ToArray();
```

Things worth knowing:

- **Coordinates are PDF user space**: the origin is the **bottom-left** corner and y increases
  upwards. Nothing flips the axis for you.
- **Twelve standard typefaces** (Helvetica, Times, Courier × regular/bold/italic/bold-italic) are
  available via `StandardFont`. They embed no font data, so no font licence applies to your
  output. Metrics are exact for printable ASCII; accented Latin letters resolve through their
  base letter.
- **Text must be representable in WinAnsi.** Anything outside it throws rather than silently
  dropping characters — use the HTML path for scripts the standard fonts cannot show.
- **Output is deterministic.** Nothing timestamped or random is written unless you set it, so the
  same calls produce byte-identical files.

## Templating

A small Mustache renderer is included, so you do not need a templating dependency:

```csharp
string template = await File.ReadAllTextAsync("Templates/Invoice.html");
string html = new MustacheRenderer().Render(template, model);
```

It supports `{{value}}`, `{{#section}}` for lists and truthy checks, `{{^section}}` for inverted
sections, `{{.}}` for the current list item, and dotted paths like `{{#Order.Lines}}`. Inside a
list section each item becomes the context, so its properties are referenced unqualified.

## Styling

`InjectCSS` points at a stylesheet on disk. The path is rewritten to an absolute `file://` URL and
the link is inserted just above the closing `</head>`, which is what lets the stylesheet reach
fonts and images sitting beside it by relative URL:

```csharp
await pdfBuilder
    .InjectCSS(Path.Combine(env.WebRootPath, "PDFStyle.css"))
    .BuildAsync(html);
```

The injected link is `media="all"`, deliberately. A `media="screen"` stylesheet is ignored while a
browser renders print media, which is what printing does — so a screen-only link silently does
nothing.

By default the page is rendered as **screen** media. Set `WebSettings.PrintMediaType = true` to
render print media instead, which is what activates your `@media print` rules.

## Headers and footers

Set any of `Left`, `Center`, `Right` on `HeaderSettings` or `FooterSettings` and they are rendered
as a three-column band. The old bracket placeholders are translated for you:

| Placeholder | Becomes |
|---|---|
| `[page]`, `[sitepage]` | current page number |
| `[topage]`, `[sitepages]` | total pages |
| `[date]`, `[isodate]`, `[time]` | the date |
| `[title]`, `[doctitle]` | the document title |
| `[webpage]` | the page URL |

```csharp
.BuildAsync(html, content =>
{
    content.HeaderSettings.Right = "[title]";
    content.HeaderSettings.Line = true;
    content.FooterSettings.Right = "Page [page] of [topage]";
    content.FooterSettings.FontSize = 8;
});
```

`[frompage]`, `[section]` and `[subsection]` have no counterpart and are dropped. Literal text
around a placeholder is HTML-escaped, so an ampersand in your text is safe.

Two gotchas inherited from the browser: the header and footer area is carved out of the page
**margins**, so a zero top margin leaves no room for a header; and set `FontSize`/`FontName`
explicitly, because the band renders in a bare document with a tiny default font.

## Choosing a browser

`PdfRendererOptions` offers three arrangements. Pick **one** — a remote browser and a local
executable are alternatives, and setting both throws.

```csharp
// 1. Discover an installed browser. The default; nothing to configure.
builder.Services.AddTheoisticPDF();

// 2. A specific binary.
builder.Services.AddTheoisticPDF(options => options.ExecutablePath = "/usr/bin/chromium");

// 3. A browser running elsewhere. This is the one for a slim container.
//    RemoteAddress takes an http:// DevTools address; RemoteEndpoint takes a ws:// one.
builder.Services.AddTheoisticPDF(options => options.RemoteAddress = new Uri("http://chrome:9222"));
```

Other options worth knowing:

| Option | Default | Why you would change it |
|---|---|---|
| `DisableSandbox` | `false` | **Set this in containers.** An unprivileged container cannot create the namespaces Chromium's sandbox needs. Also adds `--disable-dev-shm-usage`, without which a large page outgrows the default 64 MB shared-memory segment and crashes the tab |
| `AllowFileAccess` | `false` | Only for a page that reads local files itself via `fetch`/XHR. Stylesheets, fonts and images load across directories without it |
| `RenderTimeout` | 60s | Long or asset-heavy documents |
| `NavigationTimeout` | 30s | How long to wait for the load event. The page is printed anyway when it elapses, so one slow third-party asset is not fatal |
| `Arguments` | — | Extra browser flags |
| `Proxy` | — | `--proxy-server` syntax |

`ChromiumLocator.Find()` returns the browser that would be discovered, and
`ChromiumLocator.ProbedLocations()` lists every path it looks at — both useful when diagnosing a
deployment.

## Deployment

The managed writer (`Theoistic.PDF.Writing`) needs nothing and runs anywhere. Everything below is
about the **HTML path**.

### Windows and macOS, development and servers

Nothing to do. Edge ships with Windows and updates itself; Chrome on macOS is found automatically.

### Docker

You must install a browser into the image, and — easy to miss — **fonts**. A slim base image has
almost none, and without them text renders as empty boxes rather than failing loudly.

**.NET 9** images are Debian 12 based, where `chromium` is a real package:

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base

RUN apt-get update && apt-get install -y --no-install-recommends \
        chromium \
        fontconfig \
        fonts-liberation \
        fonts-noto-core \
        fonts-noto-color-emoji \
        fonts-noto-cjk \
    && fc-cache -f \
    && rm -rf /var/lib/apt/lists/*

ENV CHROMIUM_PATH=/usr/bin/chromium
```

**.NET 10** images are Ubuntu 24.04 based, and there `chromium` is a transitional package that
pulls in snap — which does not work in a container. Install Google Chrome from Google's own
repository instead, or pin an older Debian-based tag such as `9.0-bookworm-slim`, or use the
[sidecar](#sidecar-the-option-that-avoids-all-of-this):

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base

RUN apt-get update && apt-get install -y --no-install-recommends curl gnupg ca-certificates \
    && curl -fsSL https://dl.google.com/linux/linux_signing_key.pub \
        | gpg --dearmor -o /usr/share/keyrings/google-chrome.gpg \
    && echo "deb [arch=amd64 signed-by=/usr/share/keyrings/google-chrome.gpg] http://dl.google.com/linux/chrome/deb/ stable main" \
        > /etc/apt/sources.list.d/google-chrome.list \
    && apt-get update && apt-get install -y --no-install-recommends \
        google-chrome-stable \
        fontconfig fonts-liberation fonts-noto-core fonts-noto-color-emoji fonts-noto-cjk \
    && fc-cache -f \
    && rm -rf /var/lib/apt/lists/*

ENV CHROMIUM_PATH=/usr/bin/google-chrome
```

Note that Google Chrome is proprietary, whereas Debian's `chromium` is the open-source build — if
avoiding proprietary dependencies is the point, prefer the Debian route or the sidecar.

Then point the renderer at it and disable the sandbox:

```csharp
builder.Services.AddTheoisticPDF(options =>
{
    options.ExecutablePath = Environment.GetEnvironmentVariable("CHROMIUM_PATH");
    options.DisableSandbox = true;
});
```

`DisableSandbox` weakens the isolation between the browser and your process, so treat the
container as the security boundary and only render markup you control. If you must render
untrusted HTML, use a sidecar on its own network instead.

**Alpine** (`9.0-alpine`) works too, with different package names:

```dockerfile
RUN apk add --no-cache chromium font-noto font-noto-cjk font-noto-emoji ttf-liberation fontconfig \
    && fc-cache -f
# The binary has been /usr/bin/chromium-browser and /usr/bin/chromium across Alpine releases,
# so confirm which one your image has rather than assuming:
#   docker run --rm <image> sh -c 'command -v chromium chromium-browser'
ENV CHROMIUM_PATH=/usr/bin/chromium
```

### Sidecar: the option that avoids all of this

Run the browser as its own container and keep your application image slim. Nothing to install,
nothing to keep patched in your image, and the browser can be restarted independently.

```yaml
services:
  app:
    build: .
    environment:
      - Pdf__RemoteAddress=http://chrome:9222
    depends_on: [chrome]

  chrome:
    image: chromedp/headless-shell:latest
    # Chrome binds the DevTools port to loopback by default, so a sidecar must be told
    # to listen on the container's interface or nothing can reach it.
    command:
      - --remote-debugging-address=0.0.0.0
      - --remote-debugging-port=9222
      - --disable-dev-shm-usage
      - --no-sandbox
    expose: ["9222"]
```

```csharp
builder.Services.AddTheoisticPDF(options =>
    options.RemoteAddress = new Uri(builder.Configuration["Pdf:RemoteAddress"]!));
```

**Never publish the DevTools port publicly.** It grants full control of the browser, including
reading local files. Keep it on an internal network, use `expose` rather than `ports`, and if you
must map it to a host, bind it to loopback (`127.0.0.1:9222:9222`).

A sidecar image needs fonts just as much as your own would, and they vary a lot — minimal images
such as `headless-shell` carry very few. If your documents are not purely Latin, check inside the
image before trusting it:

```bash
docker run --rm --entrypoint sh chromedp/headless-shell -c 'fc-match "sans-serif:lang=ja"'
```

If that comes back with a fallback carrying no glyphs, build your own sidecar image from the
browser image plus the font packages above.

### Azure

| Target | Works? |
|---|---|
| App Service (Linux, **Code**) | **No.** OS packages cannot be installed, and Chromium fails on missing shared libraries such as `libnspr4.so` |
| App Service (Windows, Code) | Not recommended — no reliable way to install or update a browser |
| App Service for **Containers** | **Yes** — use the Dockerfile above |
| Container Apps | **Yes** — and the natural home for the sidecar pattern |
| Container Instances | **Yes** |
| Functions (Code plans) | **No**, same reason as App Service Code |
| Functions (custom container) | **Yes** |

If you are on App Service Code today and need HTML rendering, your options are to move to a
custom container, point `RemoteAddress` at a browser running elsewhere (a Container App, say), or
use the managed writer instead.

### DigitalOcean

- **App Platform with a Dockerfile** — works. App Platform uses a `Dockerfile` in the repository
  root if one is present, so the Dockerfile above applies unchanged.
- **App Platform with a buildpack** — you cannot run `apt-get`, but an `Aptfile` declaring the
  Chromium and font packages is installed at build time. Packages land outside the default
  `PATH`, so set `ExecutablePath` explicitly rather than relying on discovery.
- **Droplet** — `apt-get install chromium` plus the font packages, same as any Debian/Ubuntu host.
- **App Platform worker + sidecar** — not supported in one component; run the browser as its own
  service and point `RemoteAddress` at it.

### Deployment checklist

1. A browser is installed, or `RemoteAddress` points at one.
2. Font packages are installed — including CJK and emoji if your documents need them.
3. `DisableSandbox = true` in containers.
4. `CheckAsync` is called at startup and its failure is logged loudly.
5. The DevTools port, if any, is not reachable from the internet.

## Settings that a browser cannot honour

The settings types were modelled on wkhtmltox, and some of them describe either that engine's
internals or a physical printer. They are still accepted so existing code compiles, and ignored:

`ColorMode` · `UseCompression` · `DPI` · `ImageDPI` · `ImageQuality` · `Copies` · `Collate` ·
`Outline` · `OutlineDepth` · `DumpOutline` · `EnableIntelligentShrinking` · `MinimumFontSize` ·
`enablePlugins` · `UseExternalLinks` · `UseLocalLinks` · `ProduceForms` · `IncludeInOutline` ·
`PagesCount` · `StopSlowScript` · `DebugJavascript` · `RepeatCustomHeaders` · `CookieJar`

`BlockLocalFileAccess` is a browser-wide launch decision rather than a per-document one — use
`PdfRendererOptions.AllowFileAccess`.

The authoritative list is `PrintSettingsMapper.UnsupportedSettings`, and a test fails if it drifts
from the implementation.

## Troubleshooting

| Symptom | Cause |
|---|---|
| `No Chromium-family browser was found` | Nothing installed. The message lists every path probed; set `ExecutablePath` or `RemoteAddress` |
| `The browser did not publish a DevTools port` | It failed to start. In a container this is almost always the sandbox — set `DisableSandbox = true` |
| Text renders as empty boxes | No fonts in the image. Install `fonts-liberation`, `fonts-noto-core`, and CJK/emoji packages if needed |
| Injected CSS seems ignored | Check the stylesheet is not `@media print` only while rendering screen media, or set `PrintMediaType = true` |
| Custom `@font-face` falls back | The stylesheet is not being applied at all — see above |
| `N resource(s) failed to load` | A referenced asset 404'd. The message names the URLs. Set `LoadSettings.LoadErrorHandling = ContentErrorHandling.Ignore` to render anyway |
| PDF title is `document.html` | The markup has no `<title>` and `DocumentTitle` was not set |
| Renders are slow | A browser is started on first use. Call `CheckAsync` at startup to pay that cost before the first request |

## Contributing

Contributions are welcome. The test suite covers both target frameworks:

```bash
dotnet test
```

Tests that need a browser report themselves inconclusive when none is installed, so the suite
passes on a machine without one.

## Licence

MIT. No native binaries are shipped and nothing is downloaded at runtime, so the only licences
involved are this one and those of the Microsoft packages it references. The browser the HTML path
drives is one you install and license yourself.
