namespace LearnStack.SharedKernel.Persistence;

/// <summary>Opt-in diagnostic privacy for the current physical transaction; grants no authority.</summary>
public interface IConfidentialCommitUnitOfWork
{
    /// <summary>Bind confidential diagnostics to the currently active transaction.</summary>
    void CaptureConfidentialCommitDiagnostics();
}
