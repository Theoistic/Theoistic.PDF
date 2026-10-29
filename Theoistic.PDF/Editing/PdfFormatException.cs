namespace Theoistic.PDF.Editing;

/// <summary>
/// Thrown when bytes handed to <see cref="PdfEditor"/>, <see cref="PdfSigner"/> or
/// <see cref="PdfSignatureVerifier"/> cannot be used as a PDF: they are not a PDF at all, the file
/// is damaged beyond what the reader can repair, or it is encrypted.
/// </summary>
public sealed class PdfFormatException : Exception
{
    public PdfFormatException(string message)
        : base(message)
    {
    }

    public PdfFormatException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
