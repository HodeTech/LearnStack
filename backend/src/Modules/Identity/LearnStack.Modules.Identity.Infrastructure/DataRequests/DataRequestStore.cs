using LearnStack.Application.Messaging;
using LearnStack.Modules.Identity.Application.DataRequests;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

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

    public async Task<DataSubjectRequest?> FindLockedAsync(DataSubjectRequestId requestId, CancellationToken cancellationToken)
    {
        RequireOwner();
        await using var command = new NpgsqlCommand("""
            SELECT id FROM public.data_subject_requests
            WHERE id=@id AND tenant_id=@tenant AND organization_id IS NOT DISTINCT FROM @organization FOR UPDATE
            """, (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!)
        { CommandTimeout = 5 };
        command.Parameters.AddWithValue("id", requestId.Value);
        command.Parameters.AddWithValue("tenant", tenant.TenantId.Value);
        command.Parameters.AddWithValue("organization", NpgsqlDbType.Uuid, (object?)tenant.OrganizationId?.Value ?? DBNull.Value);
        try
        {
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
            {
                return null;
            }

            return await db.DataSubjectRequests.SingleOrDefaultAsync(request => request.Id == requestId
                && request.TenantId == tenant.TenantId && request.OrganizationId == tenant.OrganizationId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (NpgsqlException)
        {
            unit.MarkRollbackOnly();
            throw new InvalidOperationException("Data request storage unavailable.");
        }
    }

    public async Task<DataExportArtifactId?> FindArtifactOfRequestAsync(DataSubjectRequestId requestId, CancellationToken cancellationToken)
    {
        RequireOwner();
        var found = await db.DataExportArtifacts.AsNoTracking()
            .Where(artifact => artifact.RequestId == requestId && artifact.TenantId == tenant.TenantId
                && artifact.OrganizationId == tenant.OrganizationId)
            .Select(artifact => artifact.Id).Take(2).ToListAsync(cancellationToken).ConfigureAwait(false);
        return found.Count switch
        {
            0 => null,
            1 => found[0],
            _ => throw new InvalidOperationException("A request holds more than one export artifact."),
        };
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

    public async Task SaveAsync(DataSubjectRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireOwner();
        if (request.TenantId != tenant.TenantId || request.OrganizationId != tenant.OrganizationId
            || db.Entry(request).State == EntityState.Detached)
        {
            throw new InvalidOperationException("A data request is saved only from its own loaded group.");
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            unit.MarkRollbackOnly();
            throw new InvalidOperationException("Data request storage unavailable.");
        }
    }

    private void RequireOwner()
    {
        if (!unit.HasActiveTransaction || unit.IsRollbackOnly || unit.Mode != TransactionMode.ReadWrite
            || unit.GetTenantContextAnnouncement(unit.Transaction) is not { } announcement
            || announcement.TenantId != tenant.TenantId || announcement.OrganizationId != tenant.OrganizationId)
        {
            throw new InvalidOperationException("Data request storage requires its announced writable owner.");
        }
    }
}
