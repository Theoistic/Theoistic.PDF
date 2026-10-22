namespace Theoistic.PDF.Layout;

/// <summary>
/// Thrown when a document contains characters its fonts cannot show. Lists every one at once,
/// so a form or a database row can be fixed in one pass rather than one character at a time.
/// </summary>
public sealed class PdfTextCoverageException : Exception
{
    public PdfTextCoverageException(IReadOnlyList<UnsupportedText> problems, string? remedy = null)
        : base(BuildMessage(problems, remedy))
    {
        Problems = problems;
    }

    public IReadOnlyList<UnsupportedText> Problems { get; }

    private static string BuildMessage(IReadOnlyList<UnsupportedText> problems, string? remedy)
    {
        ArgumentNullException.ThrowIfNull(problems);

        // One line per character, naming every font that lacks it. Listing it once per font
        // would show a regular and bold pair as two problems when it is one.
        var characters = problems
            .GroupBy(p => p.Character)
            .Select(group => $"  U+{char.ConvertToUtf32(group.Key, 0):X4} '{group.Key}' in \"{group.First().InText}\" (not in {string.Join(", ", group.Select(p => p.FontName).Distinct())})")
            .ToList();

        IEnumerable<string> shown = characters.Take(10);
        string more = characters.Count > 10 ? $"{Environment.NewLine}  ...and {characters.Count - 10} more" : string.Empty;

        return $"{characters.Count} character(s) cannot be shown in the document's fonts:{Environment.NewLine}"
            + string.Join(Environment.NewLine, shown)
            + more
            + (remedy is null ? string.Empty : Environment.NewLine + remedy);
    }
}
