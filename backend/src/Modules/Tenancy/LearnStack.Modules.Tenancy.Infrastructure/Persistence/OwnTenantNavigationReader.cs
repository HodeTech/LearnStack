using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Tenancy.Application.Contracts.IdentityLifecycle;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Pagination;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace LearnStack.Modules.Tenancy.Infrastructure.Persistence;

/// <summary>Own-current-tenant labels on its existing ordinary owner; no scope setter or general organization directory.</summary>
public sealed class OwnTenantNavigationReader(TenancyDbContext db, IUnitOfWork unit, ITenantContext context,
    IOperationInvocationContext invocations, IOwnTenantNavigationCoverage coverage) : IOwnTenantNavigationReader
{
    public async Task<Result<Page<OwnTenantNavigationItem>>> ReadAsync(string? cursor, int limit, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > CursorPagination.MaxLimit) return Fail("lockey_validation_failed");
        var admission = await coverage.ReadCurrentAsync(cancellationToken);
        if (admission.IsFailure) return Result<Page<OwnTenantNavigationItem>>.Fail(admission.Error);
        var access = admission.Value;
        if (!Matches(access)) return Fail("lockey_forbidden");
        var scope = OwnTenantNavigationCursor.Scope(context.UserId!.Value, context.TenantId, context.OrganizationId, access.HasTenantMembership);
        OrganizationId? after = null;
        if (cursor is not null && !OwnTenantNavigationCursor.TryDecode(cursor, scope, out after)) return Fail("lockey_validation_failed");
        if (cursor is not null && after is null && !access.HasTenantMembership) return Fail("lockey_validation_failed");
        try
        {
            using var storage = TenancyAdministrationStorageScope.Enter(db);
            var items = await TenancyAdministrationStorageBoundary.ReadAsync(async () =>
            {
                var rows = new List<OwnTenantNavigationItem>();
                var tenants = db.Tenants.AsNoTracking().Where(tenant => tenant.Id == context.TenantId
                    && tenant.DeletedAt == null && tenant.DeletedBy == null
                    && (tenant.Status == TenantStatus.Trial || tenant.Status == TenantStatus.Active));
                if (access.HasTenantMembership && cursor is null)
                {
                    var tenantItem = await (from tenant in tenants
                                            from host in db.PlatformHostMappings.AsNoTracking()
                                                .Where(host => host.TenantId == tenant.Id && host.OrganizationId == null && host.IsActive)
                                                .OrderBy(host => host.Host).Take(1)
                                            select new OwnTenantNavigationItem(tenant.Id, null, tenant.DisplayName, null, host.Host))
                        .SingleOrDefaultAsync(cancellationToken);
                    if (tenantItem is not null) rows.Add(tenantItem);
                }
                var organizations = after is { } anchor
                    ? db.Organizations.FromSqlInterpolated($"SELECT * FROM public.organizations WHERE id > {anchor.Value}") : db.Organizations;
                var branches = await (from organization in organizations.AsNoTracking()
                                      join tenant in tenants on organization.TenantId equals tenant.Id
                                      where organization.TenantId == context.TenantId && organization.DeletedAt == null
                                          && organization.DeletedBy == null && organization.Status == OrganizationStatus.Active
                                          && (access.HasTenantMembership || organization.Id == context.OrganizationId)
                                      from host in db.PlatformHostMappings.AsNoTracking()
                                          .Where(host => host.TenantId == tenant.Id && host.IsActive
                                              && (host.OrganizationId == null || host.OrganizationId == organization.Id))
                                          .OrderByDescending(host => host.OrganizationId != null).ThenBy(host => host.Host).Take(1)
                                      orderby organization.Id
                                      select new OwnTenantNavigationItem(tenant.Id, organization.Id, tenant.DisplayName, organization.DisplayName, host.Host))
                    .Take(limit + 1 - rows.Count).ToArrayAsync(cancellationToken);
                rows.AddRange(branches);
                return rows;
            });
            if (!Matches(access) || items.Any(item => EffectiveHost.Normalize(item.EntryHost) != item.EntryHost))
                return Fail("lockey_dependency_unavailable");
            // The provider closes its independent actual-fact owner before returning.
            // A lost tenant Membership must not leave derived branches in the answer.
            var final = await coverage.ReadCurrentAsync(cancellationToken);
            if (final.IsFailure) return Result<Page<OwnTenantNavigationItem>>.Fail(final.Error);
            if (!Matches(final.Value) || !ReferenceEquals(final.Value.Invocation, access.Invocation)
                || final.Value.HasTenantMembership != access.HasTenantMembership) return Fail("lockey_not_found");
            var page = items.Take(limit).ToArray();
            var next = items.Count > limit && page.Length != 0 ? OwnTenantNavigationCursor.Encode(scope, page[^1].OrganizationId) : null;
            return Result.Ok(new Page<OwnTenantNavigationItem>(page, new PageInfo(next, null, next is not null, false)));
        }
        catch (TenancyAdministrationStorageException)
        {
            return Fail("lockey_dependency_unavailable");
        }
    }

    private bool Matches(OwnTenantNavigationAccess access)
    {
        var invocation = access.Invocation;
        if (!ReferenceEquals(invocations.CurrentInvocation, invocation) || !invocation.IsActive
            || invocation.EntryKind != OperationEntryKind.Application || invocation.TrustedSource is not null
            || invocation.Policy.Kind != OperationPolicyKind.OwnMembershipRead
            || invocation.Policy.Id != OwnTenantNavigationContract.Operation
            || invocation.Policy.ReadScope != OperationReadScope.Scoped || invocation.Policy.RequiresOrglessWrite
            || invocation.Policy.RequiresOrganization
            || invocation.Policy.RequestType != invocation.Operation.GetType() || !ReferenceEquals(invocation.Context, context)
            || invocations.CurrentDecision is not { IsActive: true, Outcome: ProtectedOperationOutcome.Allowed } decision
            || !ReferenceEquals(decision.Invocation, invocation)
            || invocation.Candidate is not { IsActive: true, SourceKind: AuthorizationSourceKind.Http } candidate
            || !context.IsResolved || context.TenantId == TenantId.PlatformSentinel || context.UserId is null
            || context.UserId == UserId.SystemActor || candidate.Actor != context.UserId || candidate.TenantId != context.TenantId
            || candidate.OrganizationId != context.OrganizationId || invocation.Actor != context.UserId
            || invocation.TenantId != context.TenantId || invocation.OrganizationId != context.OrganizationId
            || (!access.HasTenantMembership && context.OrganizationId is null)
            || !unit.HasActiveTransaction || unit.IsRollbackOnly || unit.Mode != TransactionMode.ReadOnly
            || unit is IOperationReadScopeView { IsTenantWideRead: true }
            || unit is IGlobalIdentityUnitOfWork { IsGlobalIdentityTransaction: true }
            || !unit.IsTenantContextIssuedOn(unit.Transaction)
            || unit.GetTenantContextAnnouncement(unit.Transaction) is not { Generation: > 0 } announcement
            || announcement.TenantId != context.TenantId || announcement.OrganizationId != context.OrganizationId
            || db.CurrentTenantId != context.TenantId || db.CurrentOrganizationId != context.OrganizationId || db.CurrentTenantWideRead
            || !ReferenceEquals(db.Database.GetDbConnection(), unit.Connection)
            || db.ChangeTracker.Entries().Any(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)) return false;
        if (db.Database.CurrentTransaction is not { } current || !ReferenceEquals(current.GetDbTransaction(), unit.Transaction))
            db.Database.UseTransaction(unit.Transaction);
        return true;
    }

    private static Result<Page<OwnTenantNavigationItem>> Fail(string key) =>
        Result<Page<OwnTenantNavigationItem>>.Fail(new Error(LocalizedMessage.Of(key)));
}
