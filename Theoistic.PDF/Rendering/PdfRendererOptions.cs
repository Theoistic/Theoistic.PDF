namespace Theoistic.PDF.Rendering;

/// <summary>
/// How <see cref="ChromiumRenderer"/> obtains a browser.
/// <para>
/// Three arrangements are supported, checked in this order:
/// </para>
/// <list type="number">
/// <item>
/// <description>
/// <see cref="RemoteEndpoint"/> - connect to a browser running elsewhere. This is the one to use
/// from a slim container: run a Chrome sidecar and point at it, rather than installing a browser
/// into the application image.
/// </description>
/// </item>
/// <item>
/// <description>
/// <see cref="ExecutablePath"/> - launch a specific browser binary.
/// </description>
/// </item>
/// <item>
/// <description>
/// Neither - discover an installed browser with <see cref="ChromiumLocator"/>.
/// </description>
/// </item>
/// </list>
/// </summary>
public sealed class PdfRendererOptions
{
    /// <summary>
    /// A DevTools WebSocket endpoint of an already-running browser, for example
    /// <c>ws://chrome:9222/devtools/browser/&lt;id&gt;</c>. When set, nothing is launched and
    /// the browser's lifetime is not this library's concern.
    /// </summary>
    public Uri? RemoteEndpoint { get; set; }

    /// <summary>
    /// An HTTP DevTools address, for example <c>http://chrome:9222</c>. Resolved to a WebSocket
    /// endpoint on first use, which is more convenient than a browser WebSocket url because the
    /// latter contains a per-launch identifier that a container cannot know in advance.
    /// </summary>
    public Uri? RemoteAddress { get; set; }

    /// <summary>
    /// The browser to launch. When null and no remote endpoint is set, an installed browser is
    /// discovered instead.
    /// </summary>
    public string? ExecutablePath { get; set; }

    /// <summary>
    /// Extra command-line arguments for the launched browser. Ignored in remote mode.
    /// </summary>
    public IList<string> Arguments { get; } = new List<string>();

    /// <summary>
    /// Profile directory for the launched browser. When null a temporary directory is created
    /// per renderer and removed on disposal, which keeps renders away from the user's own
    /// profile and from each other.
    /// </summary>
    public string? UserDataDirectory { get; set; }

    /// <summary>
    /// A proxy for the launched browser, in Chromium's <c>--proxy-server</c> syntax.
    /// </summary>
    public string? Proxy { get; set; }

    /// <summary>
    /// Whether to run without a visible window. Only worth turning off while debugging a page
    /// that renders differently when it believes it is on screen.
    /// </summary>
    public bool Headless { get; set; } = true;

    /// <summary>
    /// Lets a document loaded from a file url read other local files programmatically, by
    /// putting every local file in one origin instead of giving each an opaque origin.
    /// <para>
    /// Off by default, and ordinary rendering does not need it: stylesheets, fonts and images
    /// referenced from a local stylesheet load across directories without it. It is only needed
    /// by a page that goes after local files itself, through fetch, XHR or a canvas read.
    /// </para>
    /// <para>
    /// Turning it on means a page being rendered can read any file the host process can, so it
    /// is worth turning on only for markup you author.
    /// </para>
    /// </summary>
    public bool AllowFileAccess { get; set; }

    /// <summary>
    /// Adds the sandbox-disabling flags that most container images need, because an unprivileged
    /// container cannot create the namespaces Chromium's sandbox relies on. This weakens the
    /// isolation between the browser and the host process, so it is off by default and should
    /// only be turned on where the content being rendered is trusted.
    /// </summary>
    public bool DisableSandbox { get; set; }

    /// <summary>How long to wait for a launched browser to report its DevTools endpoint.</summary>
    public TimeSpan LaunchTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a single render may take before it is abandoned.</summary>
    public TimeSpan RenderTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long to wait for the page's load event. A page that never finishes loading still
    /// gets printed once this elapses, which matches how a person would use the print dialog.
    /// </summary>
    public TimeSpan NavigationTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Validates the combination and returns a copy, so that later mutation of the options a
    /// caller handed in cannot change a renderer's behaviour mid-flight.
    /// </summary>
    internal PdfRendererOptions Clone()
    {
        if (RemoteEndpoint is not null && RemoteAddress is not null)
        {
            throw new InvalidOperationException(
                "Set either RemoteEndpoint or RemoteAddress, not both.");
        }

        if ((RemoteEndpoint is not null || RemoteAddress is not null) && ExecutablePath is not null)
        {
            throw new InvalidOperationException(
                "A remote browser and ExecutablePath are alternatives: a remote browser is not launched locally. Set one or the other.");
        }

        if (RemoteEndpoint is { } endpoint && endpoint.Scheme is not ("ws" or "wss"))
        {
            throw new InvalidOperationException(
                $"RemoteEndpoint must be a ws:// or wss:// url; got '{endpoint}'. Use RemoteAddress for an http:// address.");
        }

        if (RemoteAddress is { } address && address.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException(
                $"RemoteAddress must be an http:// or https:// url; got '{address}'.");
        }

        var copy = new PdfRendererOptions
        {
            RemoteEndpoint = RemoteEndpoint,
            RemoteAddress = RemoteAddress,
            ExecutablePath = ExecutablePath,
            UserDataDirectory = UserDataDirectory,
            Proxy = Proxy,
            Headless = Headless,
            AllowFileAccess = AllowFileAccess,
            DisableSandbox = DisableSandbox,
            LaunchTimeout = LaunchTimeout,
            RenderTimeout = RenderTimeout,
            NavigationTimeout = NavigationTimeout,
        };

        foreach (string argument in Arguments)
        {
            copy.Arguments.Add(argument);
        }

        return copy;
    }
}
