using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Theoistic.PDF.Rendering;

namespace Theoistic.PDF;

public static class TheoisticPDFExtensions
{
    internal static IServiceProvider? ServiceProvider { get; set; }

    /// <summary>
    /// Registers the HTML renderer. The browser is discovered on first use; nothing is
    /// downloaded and no engine is shipped with this package.
    /// </summary>
    public static IServiceCollection AddTheoisticPDF(this IServiceCollection services) =>
        services.AddTheoisticPDF(static _ => { });

    /// <summary>
    /// Registers the HTML renderer with explicit options - a specific browser binary, or a
    /// remote DevTools address for deployments with no browser of their own.
    /// </summary>
    public static IServiceCollection AddTheoisticPDF(
        this IServiceCollection services,
        Action<PdfRendererOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddSingleton(_ =>
        {
            var options = new PdfRendererOptions();

            configure(options);

            return options;
        });

        // A singleton because the renderer keeps one browser alive and hands each render its
        // own tab; starting a browser per request would cost more than the render.
        services.AddSingleton<IPdfRenderer>(provider =>
            new ChromiumRenderer(provider.GetRequiredService<PdfRendererOptions>()));

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
}
