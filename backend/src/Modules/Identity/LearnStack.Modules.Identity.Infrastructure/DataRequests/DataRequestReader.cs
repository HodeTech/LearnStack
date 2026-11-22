using LearnStack.Modules.Identity.Application.DataRequests;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LearnStack.Modules.Identity.Infrastructure.DataRequests;

/// <summary>
/// <see cref="IDataRequestReader"/> on the ambient Identity context and transaction: untracked, confined to the
/// announced tenant and exactly the announced organization or none.
/// </summary>
/// <remarks>
/// The predicate <c>organization_id IS NOT DISTINCT FROM</c> the announced organization is what keeps the
/// tenant-wide group's requests out of an organization's pages and the reverse: the canonical row policy shows an
/// organization context the tenant-wide rows as well (ADR-0106 § 2).
/// </remarks>
public sealed class DataRequestReader(IdentityDbContext db, IUnitOfWork unit, ITenantContext tenant) : IDataRequestReader
{
    public async Task<IReadOnlyList<DataRequestReceiptRow>> ListAsync(UserId? subject, Guid? before, int take,
        CancellationToken cancellationToken)
    {
        Admit();
        if (take is < 1 or > 101)
        {
            throw new ArgumentOutOfRangeException(nameof(take), "A data request page reads between one and 101 rows.");
        }

        // A UUIDv7 is time-ordered and unique, so newest first is id descending and the seek is id < anchor: the
        // subject's page walks the subject index, the administrator's the group's (tenant, organization, id) index.
        var source = before is { } anchor
            ? db.DataSubjectRequests.FromSqlInterpolated($"SELECT * FROM public.data_subject_requests WHERE id < {anchor}")
            : db.DataSubjectRequests;
        var query = Group(source.AsNoTracking());
        query = subject is { } person
            ? query.Where(request => request.SubjectUserId == person)
            : query.Where(request => request.Kind != DataRequestKind.Closure);
        var page = query.OrderByDescending(request => request.Id).Take(take);
        // The receipt's columns only: the progress document never, the Membership snapshot only on the administrator's
        // page, the one view that shows it.
        var rows = subject is null
            ? page.Select(request => new DataRequestReceiptRow(request.Id, request.Kind, request.Origin, request.Status,
                request.SubjectUserId, request.RequesterUserId, request.CreatedAt, request.UpdatedAt, request.CompletedAt,
                request.ExecuteAt, request.LastRefusal, request.FailureReason, request.ResultJson, request.MembershipSnapshotJson,
                request.Version))
            : page.Select(request => new DataRequestReceiptRow(request.Id, request.Kind, request.Origin, request.Status,
                request.SubjectUserId, request.RequesterUserId, request.CreatedAt, request.UpdatedAt, request.CompletedAt,
                request.ExecuteAt, request.LastRefusal, request.FailureReason, request.ResultJson, null, request.Version));
        return await Guard(() => rows.ToListAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public async Task<DataSubjectRequest?> FindAsync(DataSubjectRequestId requestId, CancellationToken cancellationToken)
    {
        Admit();
        return await Guard(() => Group(db.DataSubjectRequests.AsNoTracking())
            .SingleOrDefaultAsync(request => request.Id == requestId, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<DataSubjectRequestId, DataRequestArtifactRow>> ReadArtifactsAsync(
        IReadOnlyCollection<DataSubjectRequestId> requestIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requestIds);
        Admit();
        if (requestIds.Count == 0)
        {
            return new Dictionary<DataSubjectRequestId, DataRequestArtifactRow>();
        }

        if (requestIds.Count > 101)
        {
            throw new ArgumentOutOfRangeException(nameof(requestIds), "A data request page reads at most 101 artifacts.");
        }

        var ids = requestIds.Distinct().ToArray();
        var artifacts = await Guard(() => db.DataExportArtifacts.AsNoTracking()
            .Where(artifact => artifact.TenantId == tenant.TenantId && artifact.OrganizationId == tenant.OrganizationId
                && ids.Contains(artifact.RequestId))
            .ToListAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
        if (artifacts.Count == 0)
        {
            return new Dictionary<DataSubjectRequestId, DataRequestArtifactRow>();
        }

        // Expiry is a time check by the database clock, as the guards and the chunk read make it (ADR-0107 § 4).
        var now = await DatabaseNowAsync(cancellationToken).ConfigureAwait(false);
        return artifacts.ToDictionary(artifact => artifact.RequestId,
            artifact => new DataRequestArtifactRow(artifact, artifact.Status == DataExportArtifactStatus.Available && now < artifact.ExpiresAt));
    }

    public async Task<UserId?> FindMembershipSubjectAsync(MembershipId membership, CancellationToken cancellationToken)
    {
        Admit();
        await using var command = DataRequestSql.Command(unit, tenant, """
            SELECT user_id FROM public.memberships
            WHERE id=@membership AND tenant_id=@tenant AND organization_id IS NOT DISTINCT FROM @organization
            """);
        command.Parameters.AddWithValue("membership", membership.Value);
        var found = await DataRequestSql.ExecuteAsync(unit, token => command.ExecuteScalarAsync(token), cancellationToken).ConfigureAwait(false);
        return found is Guid user ? UserId.From(user) : null;
    }

    public async Task<DataRequestArtifactRow?> FindArtifactLockedAsync(DataSubjectRequestId requestId, CancellationToken cancellationToken)
    {
        Admit();
        await using var command = DataRequestSql.Command(unit, tenant, """
            SELECT id, status = 'available' AND clock_timestamp() < expires_at FROM public.data_export_artifacts
            WHERE tenant_id=@tenant AND organization_id IS NOT DISTINCT FROM @organization AND request_id=@request
            FOR UPDATE
            """);
        command.Parameters.AddWithValue("request", requestId.Value);
        var found = await DataRequestSql.ExecuteAsync(unit, async token =>
        {
            await using var row = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await row.ReadAsync(token).ConfigureAwait(false))
            {
                return ((Guid Id, bool Readable)?)null;
            }

            var value = (row.GetGuid(0), row.GetBoolean(1));
            return await row.ReadAsync(token).ConfigureAwait(false)
                ? throw new InvalidOperationException("A request holds more than one export artifact.")
                : value;
        }, cancellationToken).ConfigureAwait(false);
        if (found is not { } artifact)
        {
            return null;
        }

        var artifactId = DataExportArtifactId.From(artifact.Id);
        var tracked = await db.DataExportArtifacts.SingleAsync(candidate => candidate.Id == artifactId
            && candidate.TenantId == tenant.TenantId && candidate.OrganizationId == tenant.OrganizationId, cancellationToken)
            .ConfigureAwait(false);
        return new DataRequestArtifactRow(tracked, artifact.Readable);
    }

    private IQueryable<DataSubjectRequest> Group(IQueryable<DataSubjectRequest> requests) =>
        requests.Where(request => request.TenantId == tenant.TenantId && request.OrganizationId == tenant.OrganizationId);

    private async Task<DateTimeOffset> DatabaseNowAsync(CancellationToken cancellationToken)
    {
        await using var command = DataRequestSql.Statement(unit, "SELECT clock_timestamp()");
        var value = await Guard(() => command.ExecuteScalarAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
        return value switch
        {
            DateTime utc when utc.Kind == DateTimeKind.Utc => new DateTimeOffset(utc, TimeSpan.Zero),
            DateTimeOffset instant => instant.ToUniversalTime(),
            _ => throw new InfrastructureException("Data request storage unavailable."),
        };
    }

    private async Task<T> Guard<T>(Func<Task<T>> read, CancellationToken cancellationToken)
    {
        try
        {
            return await read().ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is NpgsqlException or DbUpdateException
            || failure is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            unit.MarkRollbackOnly();
            throw new InfrastructureException("Data request storage unavailable.");
        }
    }

    /// <summary>
    /// The announced owner, with the scoped Identity context re-enrolled on its transaction when the context was created on
    /// an earlier transaction of the same request.
    /// </summary>
    private void Admit()
    {
        if (!DataRequestOwner.IsAnnounced(unit, tenant, writable: false))
        {
            throw new InvalidOperationException("Data request reads require their announced owner.");
        }

        ContextualIdentityStorageControl.EnrollCurrentTenantOwner(db, unit);
    }
}
