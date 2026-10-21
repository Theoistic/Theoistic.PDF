namespace Theoistic.PDF.Rendering;

/// <summary>
/// Turns HTML into a PDF.
/// <para>
/// The abstraction exists so that the engine is a deployment decision rather than a compile-time
/// one. <see cref="ChromiumRenderer"/> drives a browser already installed on the machine; a
/// consumer with no browser available can compose documents directly with
/// <see cref="Writing.PdfDocument"/> instead, which needs nothing external at all.
/// </para>
/// </summary>
public interface IPdfRenderer : IAsyncDisposable
{
    /// <summary>
    /// Renders <paramref name="content"/> under <paramref name="global"/> and returns the PDF
    /// bytes.
    /// </summary>
    /// <exception cref="PdfConversionException">The document could not be produced.</exception>
    Task<byte[]> RenderAsync(
        GlobalSettings global,
        ObjectSettings content,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks that the renderer can actually run, so that a misconfigured deployment fails at
    /// startup with a clear message rather than on the first user request.
    /// </summary>
    Task<PdfRendererStatus> CheckAsync(CancellationToken cancellationToken = default);
}

/// <summary>The outcome of <see cref="IPdfRenderer.CheckAsync"/>.</summary>
/// <param name="IsAvailable">Whether a render would be expected to succeed.</param>
/// <param name="Description">What the renderer found - a browser path and version, or why not.</param>
public readonly record struct PdfRendererStatus(bool IsAvailable, string Description);
