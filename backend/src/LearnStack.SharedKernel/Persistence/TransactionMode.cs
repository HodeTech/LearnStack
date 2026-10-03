namespace LearnStack.SharedKernel.Persistence;

/// <summary>The physical ambient transaction's access intent (ADR-0052).</summary>
public enum TransactionMode
{
    ReadWrite,
    ReadOnly,
}
