using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace Theoistic.PDF;

public static class TheoisticPDFExtensions
{
    internal static IServiceProvider? ServiceProvider { get; set; }

    private static int resolverRegistered;
    private static IntPtr cachedLibrary;

    public static IServiceCollection AddTheoisticPDF(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Registering the handler more than once would leave duplicates on the event for the
        // life of the process, so only the first call wins.
        if (Interlocked.Exchange(ref resolverRegistered, 1) == 0)
        {
            AssemblyLoadContext.Default.ResolvingUnmanagedDll += Default_ResolvingUnmanagedDll;
        }

        services.AddSingleton<ThreadSafeHTMLToPDFConverter>();

        return services;
    }

    public static IApplicationBuilder UseTheoisticPDF(this IApplicationBuilder webApplication)
    {
        ArgumentNullException.ThrowIfNull(webApplication);

        webApplication.ApplicationServices.UseTheoisticPDF();

        return webApplication;
    }

    /// <summary>
    /// Initialises Theoistic.PDF outside of an ASP.NET Core pipeline - worker services, console
    /// hosts and tests all need this without having an <see cref="IApplicationBuilder"/> to hand.
    /// </summary>
    public static IServiceProvider UseTheoisticPDF(this IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        ServiceProvider = serviceProvider;

        return serviceProvider;
    }

    private static IntPtr Default_ResolvingUnmanagedDll(Assembly assembly, string unmanagedDllName)
    {
        if (!unmanagedDllName.Contains("libwkhtmltox", StringComparison.OrdinalIgnoreCase))
        {
            return IntPtr.Zero;
        }

        IntPtr cached = cachedLibrary;

        if (cached != IntPtr.Zero)
        {
            return cached;
        }

        string? runtimeIdentifier =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win" :
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux" :
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx" :
            null;

        if (runtimeIdentifier == null)
        {
            return IntPtr.Zero; // Unsupported platform
        }

        string architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException(
                $"No libwkhtmltox build is available for {RuntimeInformation.ProcessArchitecture}.")
        };

        string fileExtension = runtimeIdentifier switch
        {
            "win" => ".dll",
            "linux" => ".so",
            _ => ".dylib"
        };

        string fileName = $"libwkhtmltox{fileExtension}";
        string relativePath = Path.Combine("runtimes", $"{runtimeIdentifier}-{architecture}", "native", fileName);

        var probed = new List<string>();

        // Assembly.Location is empty for single-file and some hosted deployments, so the app base
        // directory is probed as well, along with the flat layout used when the native asset is
        // copied next to the managed one.
        foreach (var root in EnumerateProbeRoots())
        {
            foreach (var candidate in new[] { Path.Combine(root, relativePath), Path.Combine(root, fileName) })
            {
                if (probed.Contains(candidate))
                {
                    continue;
                }

                probed.Add(candidate);

                if (File.Exists(candidate))
                {
                    IntPtr handle = NativeLibrary.Load(candidate);
                    cachedLibrary = handle;
                    return handle;
                }
            }
        }

        throw new FileNotFoundException(
            $"Could not find {fileName}. Probed: {string.Join(", ", probed)}");
    }

    private static IEnumerable<string> EnumerateProbeRoots()
    {
        string? assemblyDirectory = null;

        string location = typeof(TheoisticPDFExtensions).Assembly.Location;

        if (!string.IsNullOrEmpty(location))
        {
            assemblyDirectory = Path.GetDirectoryName(location);
        }

        if (!string.IsNullOrEmpty(assemblyDirectory))
        {
            yield return assemblyDirectory;
        }

        string baseDirectory = AppContext.BaseDirectory;

        if (!string.IsNullOrEmpty(baseDirectory) &&
            !string.Equals(baseDirectory.TrimEnd(Path.DirectorySeparatorChar), assemblyDirectory?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            yield return baseDirectory;
        }
    }
}
