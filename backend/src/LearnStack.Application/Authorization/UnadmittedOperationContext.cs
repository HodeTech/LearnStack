using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Permissions;

namespace LearnStack.Application.Authorization;

/// <summary>The staged ordinary graph admits no grant operation before its actual owner is installed.</summary>
public sealed class UnadmittedOperationContext : IOperationInvocationContext, IAuthorizationRefusalReporter
{
    public OperationInvocation? CurrentInvocation => null;
    public ProtectedOperationDecision? CurrentDecision => null;
    public void ReportRecheck(ProtectedOperationDecision decision) =>
        throw new AuditWriteFailedException("Authorization evidence has no admitted operation owner.");
    public void ReportResourceRefusal(ProtectedOperationDecision decision, AuthorizationRefusalReason reason) =>
        throw new AuditWriteFailedException("Authorization evidence has no admitted operation owner.");
}
