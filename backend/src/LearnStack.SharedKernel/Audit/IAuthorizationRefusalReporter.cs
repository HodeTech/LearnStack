using LearnStack.SharedKernel.Permissions;

namespace LearnStack.SharedKernel.Audit;

public enum AuthorizationRefusalReason { Capability = 1, ResourceScope = 2, Delegation = 3, Ownership = 4, Coverage = 5 }

/// <summary>Records a resource refusal from its actual live decision; accepts no caller attribution or payload.</summary>
public interface IAuthorizationRefusalReporter
{
    void ReportRecheck(ProtectedOperationDecision decision);
    void ReportResourceRefusal(ProtectedOperationDecision decision, AuthorizationRefusalReason reason);
}
