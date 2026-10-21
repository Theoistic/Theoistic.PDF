using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Theoistic.PDF.Rendering.Cdp;

/// <summary>
/// A browser this library started, and the DevTools endpoint it reported.
/// <para>
/// The port is left for the browser to choose. A fixed port would collide between two processes
/// on one machine and, worse, would let an unrelated process answer on it.
/// </para>
/// </summary>
internal sealed class ChromiumProcess : IDisposable
{
    private readonly Process process;
    private readonly string? temporaryProfile;
    private readonly StringBuilder diagnostics = new StringBuilder();

    private int disposed;

    private ChromiumProcess(Process process, string executablePath, Uri endpoint, string? temporaryProfile)
    {
        this.process = process;
        this.temporaryProfile = temporaryProfile;

        ExecutablePath = executablePath;
        Endpoint = endpoint;
    }

    public string ExecutablePath { get; }

    public Uri Endpoint { get; }

    public static async Task<ChromiumProcess> LaunchAsync(
        string executablePath,
        PdfRendererOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(options);

        string? temporaryProfile = null;
        string profile;

        if (options.UserDataDirectory is { } configured)
        {
            profile = configured;
            Directory.CreateDirectory(profile);
        }
        else
        {
            // A profile of its own keeps renders out of the user's browser state, and keeps two
            // renderers in one process from fighting over the same profile lock.
            temporaryProfile = Path.Combine(
                Path.GetTempPath(),
                "theoistic-pdf-" + Guid.NewGuid().ToString("N")[..12]);

            Directory.CreateDirectory(temporaryProfile);
            profile = temporaryProfile;
        }

        var start = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };

        foreach (string argument in BuildArguments(options, profile))
        {
            start.ArgumentList.Add(argument);
        }

        // A stale port file from an earlier run in a reused profile would point at a browser
        // that is no longer there.
        string portFile = Path.Combine(profile, "DevToolsActivePort");

        try
        {
            File.Delete(portFile);
        }
        catch (IOException)
        {
            // Nothing there, or locked by a browser still using this profile; the wait below
            // distinguishes those cases far more usefully than a guess here would.
        }

        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        var log = new StringBuilder();

        // Both pipes are drained whether or not anything is parsed out of them: a full pipe
        // buffer blocks the browser, and the symptom is a hang rather than an error.
        process.ErrorDataReceived += (_, e) => Capture(log, e.Data);
        process.OutputDataReceived += (_, e) => Capture(log, e.Data);

        try
        {
            process.Start();
        }
        catch (Exception error)
        {
            CleanUp(temporaryProfile);

            throw new PdfConversionException(
                $"Could not start the browser at '{executablePath}': {error.Message}");
        }

        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        try
        {
            Uri endpoint = await WaitForEndpointAsync(portFile, process, options, log, cancellationToken)
                .ConfigureAwait(false);

            return new ChromiumProcess(process, executablePath, endpoint, temporaryProfile);
        }
        catch
        {
            Terminate(process);
            process.Dispose();
            CleanUp(temporaryProfile);

            throw;
        }
    }

    /// <summary>
    /// Waits for the browser to publish its port.
    /// <para>
    /// The port is read from the DevToolsActivePort file in the profile rather than from the
    /// "DevTools listening on" line on stderr. On Windows the executable is a launcher that
    /// starts the real browser and exits immediately, so neither its stderr nor its exit code
    /// says anything about the browser that actually came up. The file is written by whichever
    /// process ends up owning the debugging port.
    /// </para>
    /// </summary>
    private static async Task<Uri> WaitForEndpointAsync(
        string portFile,
        Process process,
        PdfRendererOptions options,
        StringBuilder log,
        CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();
        bool everExited = false;

        while (deadline.Elapsed < options.LaunchTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (TryReadEndpoint(portFile, out Uri? endpoint))
            {
                return endpoint;
            }

            // A launcher exiting is normal, so its exit is not fatal on its own - but if the
            // port never appears either, its exit code is the most useful thing to report.
            everExited |= SafeHasExited(process);

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        string exited = everExited
            ? $" The process exited with code {ExitCodeOf(process)}; on Windows this is normal for the Edge launcher, so the browser itself most likely failed to start."
            : string.Empty;

        throw new PdfConversionException(
            $"The browser did not publish a DevTools port within {options.LaunchTimeout.TotalSeconds:0.#}s.{exited}{Environment.NewLine}{Snapshot(log)}");
    }

    private static bool TryReadEndpoint(string portFile, out Uri endpoint)
    {
        endpoint = null!;

        if (!File.Exists(portFile))
        {
            return false;
        }

        string[] lines;

        try
        {
            // Opened share-all because the browser keeps its own handle on the file.
            using var stream = new FileStream(
                portFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            using var reader = new StreamReader(stream);

            lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }
        catch (IOException)
        {
            // Being written right now; the next poll will catch it.
            return false;
        }

        if (lines.Length < 1 || !int.TryParse(lines[0].Trim(), out int port) || port <= 0)
        {
            return false;
        }

        // The second line is the browser's own websocket path, which carries a per-launch id.
        string path = lines.Length > 1 ? lines[1].Trim() : "/devtools/browser";

        return Uri.TryCreate($"ws://127.0.0.1:{port}{path}", UriKind.Absolute, out endpoint!);
    }

    private static void Capture(StringBuilder log, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (log)
        {
            if (log.Length < 16 * 1024)
            {
                log.AppendLine(line);
            }
        }
    }

    private static bool SafeHasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static IEnumerable<string> BuildArguments(PdfRendererOptions options, string profile)
    {
        if (options.Headless)
        {
            yield return "--headless=new";
        }

        yield return "--remote-debugging-port=0";
        yield return $"--user-data-dir={profile}";

        // Everything a browser does for a human and nothing for a renderer: first-run prompts,
        // extensions, background update checks, telemetry, audio.
        yield return "--no-first-run";
        yield return "--no-default-browser-check";
        yield return "--disable-extensions";
        yield return "--disable-background-networking";
        yield return "--disable-component-update";
        yield return "--disable-sync";
        yield return "--disable-default-apps";
        yield return "--metrics-recording-only";
        yield return "--mute-audio";
        yield return "--disable-gpu";

        if (options.AllowFileAccess)
        {
            // Only for pages that read local files themselves. Subresources - stylesheets,
            // fonts, images - load across directories without this.
            yield return "--allow-file-access-from-files";
        }

        if (options.DisableSandbox)
        {
            yield return "--no-sandbox";

            // The default shared-memory segment in a container is 64 MB, which Chromium
            // outgrows on a large page and then crashes the tab.
            yield return "--disable-dev-shm-usage";
        }

        if (options.Proxy is { } proxy)
        {
            yield return $"--proxy-server={proxy}";
        }

        foreach (string argument in options.Arguments)
        {
            yield return argument;
        }

        // A browser with nothing open may decide it has no reason to live.
        yield return "about:blank";
    }

    /// <summary>
    /// Resolves an http:// DevTools address to the browser's WebSocket endpoint, which carries a
    /// per-launch identifier that a caller cannot know in advance.
    /// </summary>
    public static async Task<Uri> ResolveEndpointAsync(Uri address, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        var version = new Uri(address, "/json/version");

        string body;

        try
        {
            body = await client.GetStringAsync(version, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            throw new PdfConversionException(
                $"Could not reach a browser at '{address}': {error.Message}");
        }

        string? endpoint = JsonNode.Parse(body)?["webSocketDebuggerUrl"]?.GetValue<string>();

        if (endpoint is null || !Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? parsed))
        {
            throw new PdfConversionException(
                $"The browser at '{address}' did not report a usable webSocketDebuggerUrl.");
        }

        return parsed;
    }

    public string Diagnostics => diagnostics.ToString();

    private static string Snapshot(StringBuilder log)
    {
        lock (log)
        {
            return log.ToString();
        }
    }

    private static int ExitCodeOf(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }

    private static void Terminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (Exception)
        {
            // Already gone, or not ours to kill any more.
        }
    }

    private static void CleanUp(string? directory)
    {
        if (directory is null)
        {
            return;
        }

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception)
        {
            // The browser may still be releasing handles. A temporary profile left behind is
            // untidy but harmless, and failing a render over it would not be.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        Terminate(process);
        process.Dispose();
        CleanUp(temporaryProfile);
    }
}
