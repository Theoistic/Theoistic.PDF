using System.Runtime.InteropServices;

namespace Theoistic.PDF.Rendering;

/// <summary>
/// Finds a Chromium-family browser already installed on the machine.
/// <para>
/// This is what replaces shipping a rendering engine: on Windows the Edge that comes with the
/// operating system is enough, and elsewhere any Chrome or Chromium build will do. Nothing is
/// downloaded and nothing is bundled.
/// </para>
/// </summary>
public static class ChromiumLocator
{
    /// <summary>
    /// Returns the first browser found, or null when none is installed. Results are not cached,
    /// so a browser installed while the process runs is picked up.
    /// </summary>
    public static string? Find()
    {
        foreach (string candidate in EnumerateCandidates())
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        // On Unix the browser is usually on PATH rather than at a fixed location.
        foreach (string name in EnumerateCommandNames())
        {
            string? resolved = ResolveOnPath(name);

            if (resolved is not null)
            {
                return resolved;
            }
        }

        return null;
    }

    /// <summary>Every path probed by <see cref="Find"/>, for diagnostics.</summary>
    public static IReadOnlyList<string> ProbedLocations() =>
        EnumerateCandidates().Concat(EnumerateCommandNames()).ToArray();

    private static IEnumerable<string> EnumerateCandidates()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Both program-files roots are probed because Edge installs 32-bit on 64-bit
            // Windows, so the (x86) path is the common one rather than the exception.
            foreach (string? root in new[]
            {
                Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                Environment.GetEnvironmentVariable("ProgramFiles"),
                Environment.GetEnvironmentVariable("LocalAppData"),
            })
            {
                if (string.IsNullOrEmpty(root))
                {
                    continue;
                }

                yield return Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe");
                yield return Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe");
                yield return Path.Combine(root, "Chromium", "Application", "chrome.exe");
            }

            yield break;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            yield return "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
            yield return "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge";
            yield return "/Applications/Chromium.app/Contents/MacOS/Chromium";

            yield break;
        }

        yield return "/usr/bin/google-chrome";
        yield return "/usr/bin/google-chrome-stable";
        yield return "/usr/bin/chromium";
        yield return "/usr/bin/chromium-browser";
        yield return "/usr/bin/microsoft-edge";
        yield return "/snap/bin/chromium";
    }

    private static IEnumerable<string> EnumerateCommandNames()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            yield break;
        }

        yield return "google-chrome";
        yield return "google-chrome-stable";
        yield return "chromium";
        yield return "chromium-browser";
        yield return "microsoft-edge";
    }

    private static string? ResolveOnPath(string name)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");

        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(directory, name);

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
