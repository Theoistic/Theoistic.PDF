namespace Theoistic.PDF;

internal class ErrorArgs : EventArgs
{
    public IDocument? Document { get; set; }

    public string? Message { get; set; }
}

internal class FinishedArgs : EventArgs
{
    public IDocument? Document { get; set; }

    public bool Success { get; set; }
}

internal class PhaseChangedArgs : EventArgs
{
    public IDocument? Document { get; set; }

    public int PhaseCount { get; set; }

    public int CurrentPhase { get; set; }

    public string? Description { get; set; }
}

internal class ProgressChangedArgs : EventArgs
{
    public IDocument? Document { get; set; }

    public string? Description { get; set; }
}

internal class WarningArgs : EventArgs
{
    public IDocument? Document { get; set; }

    public string? Message { get; set; }
}
