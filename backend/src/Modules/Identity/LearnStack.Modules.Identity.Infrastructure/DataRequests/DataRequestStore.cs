using LearnStack.Application.Messaging;
using LearnStack.Modules.Identity.Application.DataRequests;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace LearnStack.Modules.Identity.Infrastructure.DataRequests;

/// <summary>
/// <see cref="IDataRequestStore"/> on the ambient Identity context and transaction, confined to the announced group.
/// </summary>
public sealed class DataRequestStore(IdentityDbContext db, IUnitOfWork unit, ProducerExecution producer, ITenantContext tenant)
    : IDataRequestStore
{
    private static readonly Type[] JobRoots =
    [
        typeof(RunTenantDataExportCommand), typeof(RunOrganizationDataExportCommand),
        typeof(PurgeTenantDataExportCommand), typeof(PurgeOrganizationDataExportCommand),
    ];

    private const string ActiveIndex = "ux_data_subject_requests_active";

    private NpgsqlCommand Command(string sql) => DataRequestSql.Command(unit, tenant, sql);

    private Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> statement, CancellationToken cancellationToken) =>
        DataRequestSql.ExecuteAsync(unit, statement, cancellationToken);

    public BackgroundJobId RequireCurrentJob()
    {
        var origin = producer.CaptureJobOrigin(unit);
        if (origin.ParentJobId is not { } job || !JobRoots.Contains(origin.OperationType)
            || origin.Announcement.TenantId != tenant.TenantId || origin.Announcement.OrganizationId != tenant.OrganizationId)
        {
            throw new InvalidOperationException("Data request job owner refused.");
        }

        producer.RequireJobOrigin(origin, unit);
        return job;
    }

    /// <remarks>
    /// The row lock waits behind a running export job, which holds it for its whole transaction; a wait past the
    /// five-second budget is the retryable <see cref="InfrastructureException"/> (503), like every sibling read here.
    /// </remarks>
    public async Task<DataSubjectRequest?> FindLockedAsync(DataSubjectRequestId requestId, CancellationToken cancellationToken)
    {
        Admit();
        await using var command = Command("""
            SELECT id FROM public.data_subject_requests
            WHERE id=@id AND tenant_id=@tenant AND organization_id IS NOT DISTINCT FROM @organization FOR UPDATE
            """);
        command.Parameters.AddWithValue("id", requestId.Value);
        if (await ExecuteAsync(token => command.ExecuteScalarAsync(token), cancellationToken).ConfigureAwait(false) is null)
        {
            return null;
        }

        return await ExecuteAsync(token => db.DataSubjectRequests.SingleOrDefaultAsync(request => request.Id == requestId
            && request.TenantId == tenant.TenantId && request.OrganizationId == tenant.OrganizationId, token), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DataExportArtifactId>> FindAvailableArtifactsAsync(UserId subject, CancellationToken cancellationToken)
    {
        RequireOwner();
        // At most one by the partial unique index; the bound only keeps a corrupt state from being read unbounded.
        return await db.DataExportArtifacts.AsNoTracking()
            .Where(artifact => artifact.SubjectUserId == subject && artifact.Status == DataExportArtifactStatus.Available
                && artifact.TenantId == tenant.TenantId && artifact.OrganizationId == tenant.OrganizationId)
            .OrderBy(artifact => artifact.Id).Select(artifact => artifact.Id).Take(8)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> HoldsReadableArtifactAsync(UserId subject, CancellationToken cancellationToken)
    {
        Admit();
        // Readability by the database clock, as the guards, the receipts and the chunk read judge it; the partial
        // index of available artifacts holds at most one row per subject and group.
        await using var command = Command("""
            SELECT EXISTS (SELECT 1 FROM public.data_export_artifacts
                WHERE tenant_id=@tenant AND organization_id IS NOT DISTINCT FROM @organization AND subject_user_id=@subject
                  AND status='available' AND clock_timestamp() < expires_at)
            """);
        command.Parameters.AddWithValue("subject", subject.Value);
        return await ExecuteAsync(token => command.ExecuteScalarAsync(token), cancellationToken).ConfigureAwait(false) is true;
    }

    public async Task SaveAsync(DataSubjectRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireOwner();
        if (request.TenantId != tenant.TenantId || request.OrganizationId != tenant.OrganizationId
            || db.Entry(request).State == EntityState.Detached)
        {
            throw new InvalidOperationException("A data request is saved only from its own loaded group.");
        }

        await SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MembershipId>> ReadSubjectMembershipsAsync(UserId subject, CancellationToken cancellationToken)
    {
        Admit();
        // Exactly the creation guard's predicate (ADR-0106 § 8): every status, Revoked tombstones included, of this group only.
        await using var command = Command("""
            SELECT id FROM public.memberships
            WHERE tenant_id=@tenant AND organization_id IS NOT DISTINCT FROM @organization AND user_id=@subject
            ORDER BY id LIMIT @limit
            """);
        command.Parameters.AddWithValue("subject", subject.Value);
        command.Parameters.AddWithValue("limit", DataRequestLimits.MaximumSnapshotMemberships + 1);
        return await ExecuteAsync(async token =>
        {
            var found = new List<MembershipId>();
            await using var row = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await row.ReadAsync(token).ConfigureAwait(false))
            {
                found.Add(MembershipId.From(row.GetGuid(0)));
            }

            return (IReadOnlyList<MembershipId>)found;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DataSubjectRequest?> FindActiveLockedAsync(UserId subject, DataRequestKind kind, CancellationToken cancellationToken)
    {
        Admit();
        // The one-active partial unique index holds at most one such row (ADR-0106 § 7).
        await using var command = Command($"""
            SELECT id FROM public.data_subject_requests
            WHERE tenant_id=@tenant AND organization_id IS NOT DISTINCT FROM @organization AND subject_user_id=@subject AND kind=@kind
              AND {DataSubjectRequestConfiguration.ActiveFilter}
            FOR UPDATE
            """);
        command.Parameters.AddWithValue("subject", subject.Value);
        command.Parameters.AddWithValue("kind", DataRequestCodes.Kinds.ToCode(kind));
        var found = await ExecuteAsync(token => command.ExecuteScalarAsync(token), cancellationToken).ConfigureAwait(false);
        if (found is not Guid id)
        {
            return null;
        }

        var requestId = DataSubjectRequestId.From(id);
        return await db.DataSubjectRequests.SingleAsync(request => request.Id == requestId
            && request.TenantId == tenant.TenantId && request.OrganizationId == tenant.OrganizationId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>The aggregate port's insertion: a creation refused by the one-active index is a storage refusal here.</summary>
    async Task IAggregateWriteStore<DataSubjectRequest, DataSubjectRequestId>.AddAsync(DataSubjectRequest aggregate,
        CancellationToken cancellationToken)
    {
        if (!await TryAddAsync(aggregate, cancellationToken).ConfigureAwait(false))
        {
            throw new InfrastructureException("Data request already active.");
        }
    }

    public async Task<bool> TryAddAsync(DataSubjectRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Admit();
        if (request.TenantId != tenant.TenantId || request.OrganizationId != tenant.OrganizationId)
        {
            throw new InvalidOperationException("A data request is filed only in its own announced group.");
        }

        db.DataSubjectRequests.Add(request);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException failure) when (failure.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: ActiveIndex,
        })
        {
            // ADR-0106 § 7: two concurrent creations cannot both pass. Nothing was stored and the transaction is aborted.
            db.Entry(request).State = EntityState.Detached;
            unit.MarkRollbackOnly();
            return false;
        }
        catch (DbUpdateException)
        {
            unit.MarkRollbackOnly();
            throw new InfrastructureException("Data request storage unavailable.");
        }
    }

    private async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            unit.MarkRollbackOnly();
            throw new InfrastructureException("Data request storage unavailable.");
        }
    }

    /// <summary>
    /// The announced writable owner, with the Identity context re-enrolled on its transaction: the scoped context may
    /// have been created on an earlier transaction of the same request (the authorization read), and a clean context is
    /// re-enrolled only on the same connection's freshly announced tenant owner.
    /// </summary>
    private void Admit()
    {
        RequireOwner();
        ContextualIdentityStorageControl.EnrollCurrentTenantOwner(db, unit);
    }

    private void RequireOwner()
    {
        if (!DataRequestOwner.IsAnnounced(unit, tenant, writable: true))
        {
            throw new InvalidOperationException("Data request storage requires its announced writable owner.");
        }
    }
}
