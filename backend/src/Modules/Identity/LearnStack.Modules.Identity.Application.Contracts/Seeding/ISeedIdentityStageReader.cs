using LearnStack.SharedKernel.Identity;

namespace LearnStack.Modules.Identity.Application.Contracts.Seeding;

/// <summary>
/// Tests the later Identity stage on an exact fixed-content invocation's existing
/// exclusive Tenant plan. Retained Role rows are tenant-wide; no read hatch is used.
/// </summary>
public interface ISeedIdentityStageReader
{
    Task<bool> HasNoRoleHistoryAsync(IdentityMutationLockPlan plan, CancellationToken cancellationToken = default);
}
