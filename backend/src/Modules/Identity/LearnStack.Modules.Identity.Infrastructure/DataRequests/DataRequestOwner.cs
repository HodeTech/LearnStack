using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Modules.Identity.Infrastructure.DataRequests;

/// <summary>
/// The announced-owner test the data-request adapters share: an active, not rollback-only transaction, writable when the
/// caller writes, announced for exactly the context's tenant and organization. Each adapter keeps its own refusal, so no
/// message or exception type changes with the sharing.
/// </summary>
internal static class DataRequestOwner
{
    internal static bool IsAnnounced(IUnitOfWork unit, ITenantContext tenant, bool writable) =>
        unit.HasActiveTransaction && !unit.IsRollbackOnly && (!writable || unit.Mode == TransactionMode.ReadWrite)
        && unit.GetTenantContextAnnouncement(unit.Transaction) is { } announcement
        && announcement.TenantId == tenant.TenantId && announcement.OrganizationId == tenant.OrganizationId;
}
