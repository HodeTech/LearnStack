namespace LearnStack.SharedKernel.Audit;

/// <summary>Scopes payload-free audit diagnostics to an explicitly admitted operation and its nested work.</summary>
public static class ConfidentialAuditDiagnostics
{
    private static readonly AsyncLocal<Frame?> Current = new();

    /// <summary>Whether the current execution has a confidential audit owner.</summary>
    public static bool IsActive => Current.Value?.Confidential == true;

    /// <summary>Enter around the complete owning audit lifecycle, including reconciliation.</summary>
    public static IDisposable Enter()
    {
        return BeginExecution(new object(), confidential: true);
    }

    /// <summary>
    /// Shares sticky privacy only with frames of the same audit capture. A nested
    /// confidential act remains protected through its outer owner's reconciliation.
    /// An independent capture neither inherits nor clears that classification.
    /// </summary>
    public static IDisposable BeginExecution(object captureOwner, bool confidential)
    {
        ArgumentNullException.ThrowIfNull(captureOwner);
        var previous = Current.Value;
        for (var parent = previous; parent is not null; parent = parent.Previous)
            if (ReferenceEquals(parent.Owner, captureOwner))
            {
                confidential |= parent.Confidential;
                if (confidential) parent.Confidential = true;
            }
        var frame = new Frame(previous, captureOwner, confidential);
        Current.Value = frame;
        return frame;
    }

    private sealed class Frame(Frame? previous, object owner, bool confidential) : IDisposable
    {
        public Frame? Previous { get; } = previous;
        public object Owner { get; } = owner;
        public bool Confidential { get; set; } = confidential;
        public void Dispose() => Current.Value = Previous;
    }
}
