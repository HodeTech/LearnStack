using System.Data.Common;

namespace LearnStack.Infrastructure.Persistence;

public sealed partial class NpgsqlUnitOfWork
{
    private DbTransaction? _confidentialCommitTransaction;

    /// <summary>Bind privacy to this physical transaction, never a later transaction on the unit.</summary>
    public void CaptureConfidentialCommitDiagnostics()
    {
        _confidentialCommitTransaction = _transaction
            ?? throw new InvalidOperationException("Confidential commit diagnostics require an active transaction.");
    }

    private IDisposable? EnterConfidentialCommitDiagnostics(DbTransaction transaction)
    {
        var confidential = ReferenceEquals(_confidentialCommitTransaction, transaction);
        _confidentialCommitTransaction = null;
        return confidential ? CustomFieldCommitDiagnostics.Enter() : null;
    }
}
