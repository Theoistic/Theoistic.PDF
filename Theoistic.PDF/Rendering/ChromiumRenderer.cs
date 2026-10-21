using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Theoistic.PDF.Rendering.Cdp;

namespace Theoistic.PDF.Rendering;

/// <summary>
/// Renders HTML by driving a Chromium-family browser over the DevTools Protocol.
/// <para>
/// No engine is shipped and nothing is downloaded: the browser is one already installed on the
/// machine, one named explicitly, or one running elsewhere. See
/// <see cref="PdfRendererOptions"/> for the three arrangements.
/// </para>
/// <para>
/// One browser is started on first use and reused. Each render gets its own tab, so concurrent
/// callers do not interfere, and a browser that dies is replaced on the next render.
/// </para>
/// </summary>
public sealed class ChromiumRenderer : IPdfRenderer, IDisposable
{
    private readonly PdfRendererOptions options;
    private readonly SemaphoreSlim startLock = new SemaphoreSlim(1, 1);

    private ChromiumProcess? browser;
    private CdpConnection? connection;
    private int disposed;

    public ChromiumRenderer()
        : this(new PdfRendererOptions())
    {
    }

    public ChromiumRenderer(PdfRendererOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        this.options = options.Clone();
    }

    /// <inheritdoc />
    public async Task<PdfRendererStatus> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            CdpConnection active = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

            JsonNode version = await active.SendAsync("Browser.getVersion", cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            string product = (string?)version["product"] ?? "unknown";
            string where = browser?.ExecutablePath ?? options.RemoteEndpoint?.ToString() ?? "remote";

            return new PdfRendererStatus(true, $"{product} ({where})");
        }
        catch (Exception error)
        {
            return new PdfRendererStatus(false, error.Message);
        }
    }

    /// <inheritdoc />
    public async Task<byte[]> RenderAsync(
        GlobalSettings global,
        ObjectSettings content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(global);
        ArgumentNullException.ThrowIfNull(content);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

        if (content.HtmlContent is not { Length: > 0 } && content.Page is not { Length: > 0 })
        {
            throw new PdfConversionException(
                "Nothing to render: set ObjectSettings.HtmlContent to markup or ObjectSettings.Page to a url.");
        }

        using var expiry = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        expiry.CancelAfter(options.RenderTimeout);

        try
        {
            return await RenderCoreAsync(global, content, expiry.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PdfConversionException(
                $"The render did not finish within {options.RenderTimeout.TotalSeconds:0.#}s.");
        }
    }

    private async Task<byte[]> RenderCoreAsync(
        GlobalSettings global,
        ObjectSettings content,
        CancellationToken cancellationToken)
    {
        CdpConnection active = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        JsonNode created = await active.SendAsync(
            "Target.createTarget",
            new { url = "about:blank" },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        string targetId = (string?)created["targetId"]
            ?? throw new PdfConversionException("The browser did not return a target id.");

        try
        {
            // Flat mode means this one connection can address many tabs, each by its session id,
            // instead of opening a socket per tab.
            JsonNode attached = await active.SendAsync(
                "Target.attachToTarget",
                new { targetId, flatten = true },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            string session = (string?)attached["sessionId"]
                ?? throw new PdfConversionException("The browser did not return a session id.");

            return await RenderInSessionAsync(active, session, global, content, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await active.SendAsync("Target.closeTarget", new { targetId }).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A tab that cannot be closed is a leak in a browser that is about to be
                // replaced anyway; it must not mask the render's own outcome.
            }
        }
    }

    private async Task<byte[]> RenderInSessionAsync(
        CdpConnection active,
        string session,
        GlobalSettings global,
        ObjectSettings content,
        CancellationToken cancellationToken)
    {
        await active.SendAsync("Page.enable", sessionId: session, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        using var resources = new ResourceTracker();

        await ConfigureSessionAsync(active, session, global, content, resources, cancellationToken).ConfigureAwait(false);

        // Markup is written to a temporary file and navigated to, rather than pushed in with
        // Page.setDocumentContent.
        //
        // Content pushed into the tab lands in an about:blank document, whose origin is opaque,
        // and the browser then refuses to fetch its file:// subresources - which silently breaks
        // InjectCSS and every local font and image the stylesheet reaches for. A document loaded
        // from a file url may load them.
        string? temporaryDirectory = null;

        try
        {
            string url;

            if (content.Page is { Length: > 0 } page)
            {
                url = page;
            }
            else
            {
                // A directory of its own with a fixed file name inside it. The name matters:
                // with no title to go on the browser records the document's file name as the
                // PDF title, so a generated one would put a random string there.
                temporaryDirectory = Path.Combine(
                    Path.GetTempPath(),
                    $"theoistic-pdf-{Guid.NewGuid():N}");

                Directory.CreateDirectory(temporaryDirectory);

                string file = Path.Combine(temporaryDirectory, "document.html");

                // No byte-order mark: some parsers treat it as text before the doctype.
                await File.WriteAllTextAsync(
                    file,
                    content.HtmlContent!,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    cancellationToken).ConfigureAwait(false);

                url = new Uri(file).AbsoluteUri;
            }

            // Armed before the navigation so that a page which loads instantly cannot fire the
            // event before anyone is listening.
            using CdpEventWaiter loaded = active.Expect("Page.loadEventFired", session);

            await active.SendAsync("Page.navigate", new { url }, session, cancellationToken).ConfigureAwait(false);

            try
            {
                await loaded.WaitAsync(options.NavigationTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // A page that never finishes loading is still printed, which is what the
                // browser's own print command does. Failing here would make one slow
                // third-party asset fatal.
            }

            return await PrintAsync(active, session, global, content, resources, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (temporaryDirectory is not null)
            {
                try
                {
                    Directory.Delete(temporaryDirectory, recursive: true);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // The browser may still hold the file; a stray temp directory is not worth
                    // failing a finished render over.
                }
            }
        }
    }

    private async Task<byte[]> PrintAsync(
        CdpConnection active,
        string session,
        GlobalSettings global,
        ObjectSettings content,
        ResourceTracker resources,
        CancellationToken cancellationToken)
    {

        if (global.DocumentTitle is { Length: > 0 } title)
        {
            // Applied after load so it survives whatever the document itself set, and so it
            // reaches both the PDF metadata and the [title] header placeholder.
            //
            // Setting it to empty is deliberately not attempted when no title is configured:
            // Chromium records document.title or, failing that, the frame url, so clearing it
            // only swaps one value for the url rather than leaving the field blank.
            await active.SendAsync(
                "Runtime.evaluate",
                new
                {
                    expression = $"document.title = {JsonValue.Create(title)!.ToJsonString()}",
                    returnByValue = true,
                },
                session,
                cancellationToken).ConfigureAwait(false);
        }

        if (content.LoadSettings.JSDelay is { } delay and > 0)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        if ((content.LoadSettings.LoadErrorHandling ?? ContentErrorHandling.Abort) == ContentErrorHandling.Abort)
        {
            IReadOnlyList<string> failures = resources.Failures;

            if (failures.Count > 0)
            {
                throw new PdfConversionException(
                    $"{failures.Count} resource(s) failed to load and LoadErrorHandling is Abort: {string.Join("; ", failures)}",
                    failures,
                    Array.Empty<string>());
            }
        }

        Dictionary<string, object?> parameters = PrintSettingsMapper.ToPrintParameters(global, content);

        JsonNode printed = await active.SendAsync("Page.printToPDF", parameters, session, cancellationToken)
            .ConfigureAwait(false);

        string? data = (string?)printed["data"];

        if (data is null)
        {
            throw new PdfConversionException("The browser returned no PDF data.");
        }

        return Convert.FromBase64String(data);
    }

    private async Task ConfigureSessionAsync(
        CdpConnection active,
        string session,
        GlobalSettings global,
        ObjectSettings content,
        ResourceTracker resources,
        CancellationToken cancellationToken)
    {
        LoadSettings load = content.LoadSettings;
        WebSettings web = content.WebSettings;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (load.CustomHeaders is { Count: > 0 } custom)
        {
            foreach (KeyValuePair<string, string> header in custom)
            {
                headers[header.Key] = header.Value;
            }
        }

        if (load.Username is { Length: > 0 })
        {
            // Pre-emptive basic auth, rather than waiting to be challenged, because handling a
            // challenge needs request interception and this covers what the old settings meant.
            string credentials = Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes($"{load.Username}:{load.Password}"));

            headers["Authorization"] = "Basic " + credentials;
        }

        bool needsNetwork = headers.Count > 0
            || load.Cookies is { Count: > 0 }
            || (load.LoadErrorHandling ?? ContentErrorHandling.Abort) == ContentErrorHandling.Abort;

        if (needsNetwork)
        {
            await active.SendAsync("Network.enable", sessionId: session, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        if (headers.Count > 0)
        {
            await active.SendAsync(
                "Network.setExtraHTTPHeaders",
                new { headers },
                session,
                cancellationToken).ConfigureAwait(false);
        }

        if (load.Cookies is { Count: > 0 } cookies)
        {
            string host = content.Page is { Length: > 0 } page &&
                Uri.TryCreate(page, UriKind.Absolute, out Uri? parsed)
                    ? parsed.Host
                    : "localhost";

            await active.SendAsync(
                "Network.setCookies",
                new
                {
                    cookies = cookies
                        .Select(c => new { name = c.Key, value = c.Value, domain = host, path = "/" })
                        .ToArray(),
                },
                session,
                cancellationToken).ConfigureAwait(false);
        }

        if ((load.LoadErrorHandling ?? ContentErrorHandling.Abort) == ContentErrorHandling.Abort)
        {
            resources.Watch(active, session);
        }

        // Always stated, never left to the browser. Printing renders print media by default,
        // whereas the old engine rendered screen media unless asked otherwise - so staying
        // silent here would change which stylesheets apply to every existing document.
        await active.SendAsync(
            "Emulation.setEmulatedMedia",
            new { media = web.PrintMediaType == true ? "print" : "screen" },
            session,
            cancellationToken).ConfigureAwait(false);

        if (web.EnableJavascript == false)
        {
            await active.SendAsync(
                "Emulation.setScriptExecutionDisabled",
                new { value = true },
                session,
                cancellationToken).ConfigureAwait(false);
        }

        if (TryParseViewport(global.ViewportSize, out int width, out int height))
        {
            await active.SendAsync(
                "Emulation.setDeviceMetricsOverride",
                new { width, height, deviceScaleFactor = 1, mobile = false },
                session,
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Parses the "800x600" viewport form. Kept separate so an unparseable value is ignored
    /// rather than failing a render over a cosmetic setting.
    /// </summary>
    internal static bool TryParseViewport(string? value, out int width, out int height)
    {
        width = 0;
        height = 0;

        if (value is not { Length: > 0 })
        {
            return false;
        }

        Match match = Regex.Match(value, @"^(\d+)\s*[xX*]\s*(\d+)$", RegexOptions.CultureInvariant);

        return match.Success
            && int.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out width)
            && int.TryParse(match.Groups[2].Value, CultureInfo.InvariantCulture, out height);
    }

    /// <summary>
    /// Returns a live connection, starting or replacing the browser if necessary.
    /// </summary>
    private async Task<CdpConnection> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        // Health is the connection's, not the process's. On Windows the executable that was
        // started is a launcher which exits as soon as the real browser is up, so its exit says
        // nothing; and in remote mode there is no process here at all.
        if (connection is { IsOpen: true } healthy)
        {
            return healthy;
        }

        await startLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (connection is { IsOpen: true } stillHealthy)
            {
                return stillHealthy;
            }

            await TearDownAsync().ConfigureAwait(false);

            Uri endpoint;

            if (options.RemoteEndpoint is { } configured)
            {
                endpoint = configured;
            }
            else if (options.RemoteAddress is { } address)
            {
                endpoint = await ChromiumProcess.ResolveEndpointAsync(address, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                string executable = options.ExecutablePath
                    ?? ChromiumLocator.Find()
                    ?? throw new PdfConversionException(BuildNoBrowserMessage());

                browser = await ChromiumProcess.LaunchAsync(executable, options, cancellationToken).ConfigureAwait(false);
                endpoint = browser.Endpoint;
            }

            connection = await CdpConnection.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);

            return connection;
        }
        finally
        {
            startLock.Release();
        }
    }

    private static string BuildNoBrowserMessage() =>
        "No Chromium-family browser was found. Install Microsoft Edge, Google Chrome or Chromium, " +
        "or configure one of: PdfRendererOptions.ExecutablePath (a browser binary), " +
        "PdfRendererOptions.RemoteAddress (an http:// DevTools address, which is how a container " +
        "points at a Chrome sidecar), or PdfRendererOptions.RemoteEndpoint (a ws:// endpoint). " +
        "To produce PDFs with no browser at all, compose them directly with Theoistic.PDF.Writing.PdfDocument. " +
        "Probed: " + string.Join(", ", ChromiumLocator.ProbedLocations());

    private async Task TearDownAsync()
    {
        if (connection is { } existing)
        {
            // Only a browser this renderer started is closed. In remote mode the browser
            // belongs to whoever runs it, and shutting it down would take out every other
            // client with it.
            if (browser is not null && existing.IsOpen)
            {
                try
                {
                    // Asking over the protocol is what actually stops the browser: on Windows
                    // the process that was started is a launcher, so killing it leaves the real
                    // browser running.
                    await existing.SendAsync("Browser.close").ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Unreachable or already going; the kill below is the fallback.
                }
            }

            await existing.DisposeAsync().ConfigureAwait(false);
            connection = null;
        }

        browser?.Dispose();
        browser = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        await TearDownAsync().ConfigureAwait(false);

        startLock.Dispose();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>
    /// Records which resources a page failed to fetch, for <see cref="ContentErrorHandling"/>.
    /// <para>
    /// Request urls are remembered as they go out, because the failure event identifies the
    /// request only by id - reporting that id would name the failure without saying what failed.
    /// </para>
    /// <para>
    /// Subscriptions are per render and released on disposal. They live on a connection shared
    /// by every render, so leaving them attached would accumulate one pair per document for the
    /// life of the process.
    /// </para>
    /// </summary>
    private sealed class ResourceTracker : IDisposable
    {
        private readonly Dictionary<string, string> urls = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly List<string> failures = new List<string>();
        private readonly List<IDisposable> subscriptions = new List<IDisposable>();
        private readonly object gate = new object();

        public IReadOnlyList<string> Failures
        {
            get
            {
                lock (gate)
                {
                    return failures.ToArray();
                }
            }
        }

        public void Watch(CdpConnection connection, string session)
        {
            subscriptions.Add(connection.Subscribe("Network.requestWillBeSent", session, parameters =>
            {
                if ((string?)parameters["requestId"] is { } id &&
                    (string?)parameters["request"]?["url"] is { } url)
                {
                    lock (gate)
                    {
                        urls[id] = url;
                    }
                }
            }));

            subscriptions.Add(connection.Subscribe("Network.loadingFailed", session, parameters =>
            {
                // A cancelled request is not a failed one: navigating away supersedes requests
                // that were still in flight.
                if ((bool?)parameters["canceled"] == true)
                {
                    return;
                }

                string id = (string?)parameters["requestId"] ?? string.Empty;
                string reason = (string?)parameters["errorText"] ?? "failed";

                lock (gate)
                {
                    string url = urls.TryGetValue(id, out string? known) ? known : "(unknown url)";

                    // The browser asks for a favicon whether or not the document mentions one.
                    // wkhtmltox never did, so failing over a missing one would be a regression
                    // caused purely by changing engine.
                    if (IsFavicon(url))
                    {
                        return;
                    }

                    failures.Add($"{url} ({reason})");
                }
            }));
        }

        private static bool IsFavicon(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed)
                ? parsed.AbsolutePath.EndsWith("/favicon.ico", StringComparison.OrdinalIgnoreCase)
                : url.EndsWith("favicon.ico", StringComparison.OrdinalIgnoreCase);

        public void Dispose()
        {
            foreach (IDisposable subscription in subscriptions)
            {
                subscription.Dispose();
            }

            subscriptions.Clear();
        }
    }
}
