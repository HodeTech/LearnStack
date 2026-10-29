using System.Collections.Immutable;
using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Pagination;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace LearnStack.Modules.Tenancy.Infrastructure.Persistence;

/// <summary>Detached bounded facts on the existing announced tenant owner, real model filters and RLS.</summary>
public sealed class TenancyAdministrationReader(TenancyDbContext db, IUnitOfWork unit, ITenantContext context)
    : ITenancyAdministrationReader
{
    public Task<TenantAdministrationFacts?> ReadTenantAsync(CancellationToken cancellationToken = default) => ReadAsync(() =>
        db.Tenants.AsNoTracking().Where(row => row.Id == context.TenantId)
            .Select(row => new TenantAdministrationFacts(row.Id, row.Status, row.DeletedAt != null || row.DeletedBy != null,
                row.DefaultOrganizationId, row.Version)).SingleOrDefaultAsync(cancellationToken));

    public Task<OrganizationAdministrationFacts?> ReadOrganizationAsync(OrganizationId id, CancellationToken cancellationToken = default) => ReadAsync(() =>
        db.Organizations.AsNoTracking().Where(row => row.Id == id && row.TenantId == context.TenantId)
            .Select(row => new OrganizationAdministrationFacts(row.Id, row.TenantId, row.Status,
                row.DeletedAt != null || row.DeletedBy != null, row.Version)).SingleOrDefaultAsync(cancellationToken));

    public Task<bool> HasOtherActiveOrganizationAsync(OrganizationId id, CancellationToken cancellationToken = default) => ReadAsync(() =>
        db.Organizations.AsNoTracking().AnyAsync(row => row.TenantId == context.TenantId && row.Id != id
            && row.Status == OrganizationStatus.Active && row.DeletedAt == null && row.DeletedBy == null, cancellationToken));

    public Task<TenantBrandingFacts?> ReadBrandingAsync(CancellationToken cancellationToken = default) => ReadAsync(() =>
        db.TenantSettings.AsNoTracking().Where(row => row.TenantId == context.TenantId && row.OrganizationId == null
                && row.Key == "branding.theme" && row.DeletedAt == null && row.DeletedBy == null)
            .Select(row => new TenantBrandingFacts(row.Id, row.Version)).SingleOrDefaultAsync(cancellationToken));

    public async Task<OrganizationDirectoryPage?> ListOrganizationsAsync(string? cursor, int limit, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > CursorPagination.MaxLimit) throw new ArgumentOutOfRangeException(nameof(limit));
        Admit();
        OrganizationId? after = null;
        var scope = TenancyOrganizationCursor.Scope(context.TenantId, context.UserId!.Value, context.OrganizationId);
        if (cursor is not null && !TenancyOrganizationCursor.TryDecode(cursor, scope, out after))
            throw new TenancyAdministrationStorageException(TenancyAdministrationStorageFailure.InvalidCursor);
        return await ReadAsync(async () =>
        {
            // The UUID comparison stays in SQL. The composable entity source retains its normal filters.
            var organizations = after is { } anchor
                ? db.Organizations.FromSqlInterpolated($"SELECT * FROM public.organizations WHERE id > {anchor.Value}")
                : db.Organizations;
            // A LEFT LATERAL join retains Tenant metadata even when the bounded page is empty.
            // One statement means the page/default/version cannot combine different READ COMMITTED snapshots.
            var rows = await (from tenant in db.Tenants.AsNoTracking()
                              where tenant.Id == context.TenantId && tenant.DeletedAt == null && tenant.DeletedBy == null
                                  && (tenant.Status == TenantStatus.Trial || tenant.Status == TenantStatus.Active || tenant.Status == TenantStatus.Suspended)
                              from organization in organizations.AsNoTracking()
                                  .Where(row => row.TenantId == tenant.Id && row.DeletedAt == null && row.DeletedBy == null)
                                  .OrderBy(row => row.Id).Take(limit + 1).DefaultIfEmpty()
                              select new { tenant.DefaultOrganizationId, TenantVersion = tenant.Version, Organization = organization })
                .AsSingleQuery().ToArrayAsync(cancellationToken);
            if (rows.Length == 0) return null;
            var page = rows.Where(row => row.Organization is not null).Select(row => row.Organization!).ToArray();
            var hasNext = page.Length > limit;
            var items = page.Take(limit).Select(row => new OrganizationDirectoryItem(row.Id, row.Slug, row.DisplayName,
                Map(row.Status), row.Id == rows[0].DefaultOrganizationId, row.Version)).ToImmutableArray();
            return new OrganizationDirectoryPage(items, rows[0].DefaultOrganizationId, rows[0].TenantVersion,
                new PageInfo(hasNext ? TenancyOrganizationCursor.Encode(scope, items[^1].OrganizationId) : null,
                    null, hasNext, cursor is not null));
        });
    }

    public Task<OrganizationAdministrationDetail?> ReadOrganizationDetailAsync(OrganizationId id, CancellationToken cancellationToken = default) =>
        ReadAsync(async () =>
        {
            var row = await (from organization in db.Organizations.AsNoTracking()
                             join tenant in db.Tenants.AsNoTracking() on organization.TenantId equals tenant.Id
                             where tenant.Id == context.TenantId && organization.Id == id
                                 && tenant.DeletedAt == null && tenant.DeletedBy == null
                                 && (tenant.Status == TenantStatus.Trial || tenant.Status == TenantStatus.Active || tenant.Status == TenantStatus.Suspended)
                                 && organization.DeletedAt == null && organization.DeletedBy == null
                             select new { Organization = organization, IsDefault = tenant.DefaultOrganizationId == organization.Id })
                .SingleOrDefaultAsync(cancellationToken);
            return row is null ? null : new OrganizationAdministrationDetail(row.Organization, row.IsDefault);
        });

    private async Task<T> ReadAsync<T>(Func<Task<T>> read)
    {
        Admit();
        using var boundary = TenancyAdministrationStorageScope.Enter(db);
        var result = await TenancyAdministrationStorageBoundary.ReadAsync(read);
        Admit();
        return result;
    }

    private void Admit()
    {
        if (!context.IsResolved || context.TenantId == TenantId.PlatformSentinel || context.UserId is null
            || context.UserId == UserId.SystemActor || !unit.HasActiveTransaction || unit.IsRollbackOnly
            || unit.Mode is not (TransactionMode.ReadOnly or TransactionMode.ReadWrite)
            || unit is IGlobalIdentityUnitOfWork { IsGlobalIdentityTransaction: true }
            || !unit.IsTenantContextIssuedOn(unit.Transaction)
            || unit.GetTenantContextAnnouncement(unit.Transaction) is not { Generation: > 0 } announcement
            || announcement.TenantId != context.TenantId || announcement.OrganizationId != context.OrganizationId
            || db.CurrentTenantId != context.TenantId || db.CurrentOrganizationId != context.OrganizationId
            || !ReferenceEquals(db.Database.GetDbConnection(), unit.Connection)
            || db.ChangeTracker.Entries().Any(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
            throw new TenantContextMissingException("Tenancy administration requires its clean announced ordinary owner.");
        if (db.Database.CurrentTransaction is not { } current || !ReferenceEquals(current.GetDbTransaction(), unit.Transaction))
            db.Database.UseTransaction(unit.Transaction);
    }

    private static ManagedOrganizationStatus Map(OrganizationStatus status) => status switch
    {
        OrganizationStatus.Active => ManagedOrganizationStatus.Active,
        OrganizationStatus.Suspended => ManagedOrganizationStatus.Suspended,
        OrganizationStatus.Archived => ManagedOrganizationStatus.Archived,
        _ => throw new InvalidOperationException("The organization status is invalid."),
    };
}
