namespace Theoistic.PDFWebTest.Models;

/// <summary>
/// The model handed to <see cref="Theoistic.PDF.MustacheRenderer"/>. Plain properties, because
/// the renderer resolves tags by reflecting over whatever object it is given.
/// </summary>
public sealed class InvoiceModel
{
    public string Number { get; init; } = string.Empty;

    public string Date { get; init; } = string.Empty;

    public string Customer { get; init; } = string.Empty;

    public string Total { get; init; } = string.Empty;

    /// <summary>
    /// Each entry becomes the context for one pass of the <c>{{#Items}}</c> section, so the
    /// template refers to its properties unqualified.
    /// </summary>
    public List<Line> Items { get; init; } = new List<Line>();

    /// <summary>Drives an inverted <c>{{^Items}}</c> section in the template.</summary>
    public bool HasItems => Items.Count > 0;

    public sealed class Line
    {
        public string Name { get; init; } = string.Empty;

        public string Value { get; init; } = string.Empty;
    }
}
