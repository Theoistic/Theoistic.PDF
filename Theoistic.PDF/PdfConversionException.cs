namespace Theoistic.PDF;

/// <summary>
/// Thrown when wkhtmltox could not produce a document. Carries whatever the native library
/// reported through its error and warning callbacks, which is usually the only clue as to why
/// a conversion failed.
/// </summary>
public sealed class PdfConversionException : Exception
{
    public PdfConversionException(string message)
        : this(message, Array.Empty<string>(), Array.Empty<string>())
    {
    }

    public PdfConversionException(string message, IReadOnlyList<string> errors, IReadOnlyList<string> warnings)
        : base(message)
    {
        Errors = errors;
        Warnings = warnings;
    }

    /// <summary>Messages reported by the wkhtmltox error callback during the conversion.</summary>
    public IReadOnlyList<string> Errors { get; }

    /// <summary>Messages reported by the wkhtmltox warning callback during the conversion.</summary>
    public IReadOnlyList<string> Warnings { get; }
}
