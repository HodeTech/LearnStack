using LearnStack.Application.Jobs;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Npgsql;
using NpgsqlTypes;

namespace LearnStack.Infrastructure.Jobs;

internal enum BackgroundJobRecipientRead { Ready, Existing, Ineligible, Unavailable }

/// <summary>Locked app-role route validation and the final reciprocal receipt, on the existing owner.</summary>
public sealed class PostgresBackgroundJobRecipientCompletion(IUnitOfWork unit, ITenantContextAccessor accessor,
    BackgroundJobRuntimeOptions options) : BackgroundJobRecipientCompletionParticipant(unit, accessor)
{
    private const string RouteFence = """
        id=@recipient AND tenant_id=@tenant AND organization_id IS NOT DISTINCT FROM @organization
        AND occurrence_id=@occurrence AND recurrence_key=@key AND revision=@revision AND manifest_hash=@hash
        AND target_definition_key=@target AND recipient_policy=@policy AND scheduled_at=@slot AND due_at=@due
        """;

    internal async Task<BackgroundJobRecipientRead> ReadAndLockAsync(CancellationToken cancellationToken)
    {
        RequireOwner(Unit);
        await using (var rowLock = CreateCommand("SELECT id FROM background_job_recipients WHERE id=@recipient AND tenant_id=@tenant FOR UPDATE"))
            if (await rowLock.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
                return BackgroundJobRecipientRead.Unavailable;
        RequireOwner(Unit);
        // Evaluate the clock only after acquiring the real row lock. Waiting
        // for another transaction cannot lend us its older statement snapshot.
        await using (var read = CreateCommand("""
            SELECT ownership_token,ownership_until,accepted_job_id,skipped_at,quarantined_at,clock_timestamp(),
                EXISTS(SELECT 1 FROM background_jobs j WHERE j.tenant_id=background_job_recipients.tenant_id
                    AND j.id=accepted_job_id AND j.recipient_id=background_job_recipients.id
                    AND j.organization_id IS NOT DISTINCT FROM background_job_recipients.organization_id
                    AND j.definition_key=target_definition_key AND j.original_due_at=due_at)
            FROM background_job_recipients WHERE
            """ + " " + RouteFence))
        await using (var row = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await row.ReadAsync(cancellationToken).ConfigureAwait(false)) return BackgroundJobRecipientRead.Unavailable;
            if (!row.IsDBNull(2))
            {
                if (!row.GetBoolean(6)) throw new InvalidOperationException("The stored recipient child receipt is inconsistent.");
                RecordChild(Unit, BackgroundJobId.From(row.GetGuid(2)), existing: true);
                return BackgroundJobRecipientRead.Existing;
            }
            if (row.IsDBNull(0) || row.GetGuid(0) != Command.Claim.Token || row.IsDBNull(1)
                || row.GetFieldValue<DateTimeOffset>(1) <= row.GetFieldValue<DateTimeOffset>(5)
                || Command.Claim.DueAt > row.GetFieldValue<DateTimeOffset>(5) || !row.IsDBNull(3) || !row.IsDBNull(4))
                return BackgroundJobRecipientRead.Unavailable;
        }
        RequireOwner(Unit);
        var eligible = false;
        await using (var tenant = CreateCommand("SELECT status,deleted_at,default_organization_id FROM tenants WHERE id=@tenant"))
        await using (var row = await tenant.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            if (await row.ReadAsync(cancellationToken).ConfigureAwait(false))
                eligible = Command.Projection.AllowsTenant(row.GetString(0), !row.IsDBNull(1),
                    row.IsDBNull(2) ? null : row.GetGuid(2), Command.Claim.RecipientPolicy);
        RequireOwner(Unit);
        if (eligible && Command.Claim.OrganizationId is not null)
        {
            eligible = false;
            await using var organization = CreateCommand("SELECT status,deleted_at FROM organizations WHERE tenant_id=@tenant AND id=@organization");
            await using var row = await organization.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await row.ReadAsync(cancellationToken).ConfigureAwait(false))
                eligible = Command.Projection.AllowsOrganization(row.GetString(0), !row.IsDBNull(1));
        }
        RequireOwner(Unit);
        if (eligible) return BackgroundJobRecipientRead.Ready;
        RecordIneligible();
        return BackgroundJobRecipientRead.Ineligible;
    }

    protected override async Task FenceReceiptAsync(CancellationToken cancellationToken)
    {
        BeginCommitBudgetRead();
        var predicate = Existing
            ? " AND accepted_job_id=@child AND skipped_at IS NULL AND quarantined_at IS NULL"
            : " AND ownership_token=@token AND ownership_until>clock_timestamp() AND accepted_job_id IS NULL AND skipped_at IS NULL AND quarantined_at IS NULL";
        // Acceptance clears the lease columns. Preserve the exact original
        // interval before that write, and charge the entire SQL/cleanup path.
        await using (var budget = CreateCommand((Existing ? "SELECT @original_until,clock_timestamp()" : "SELECT ownership_until,clock_timestamp()")
            + " FROM background_job_recipients WHERE " + RouteFence + predicate))
        await using (var row = await budget.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await row.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("Recipient completion lost its exact live acquisition.");
            RecordCommitBudget(row);
        }
        RequireOwner(Unit);
        if (Existing) return;
        await using var complete = CreateCommand("UPDATE background_job_recipients SET accepted_job_id=@child,accepted_at=clock_timestamp() WHERE "
            + RouteFence + predicate);
        if (await complete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("Recipient completion lost its lease before the reciprocal receipt.");
        RequireOwner(Unit);
    }

    private NpgsqlCommand CreateCommand(string sql)
    {
        RequireOwner(Unit);
        if (Unit.Connection is not NpgsqlConnection connection || Unit.Transaction is not NpgsqlTransaction transaction
            || !ReferenceEquals(transaction.Connection, connection))
            throw new InvalidOperationException("Recipient completion requires its owned Npgsql pair.");
        var claim = Command.Claim;
        var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = (int)Math.Ceiling(options.SqlTimeout.TotalSeconds) };
        command.Parameters.AddWithValue("recipient", claim.RecipientId.Value);
        command.Parameters.AddWithValue("tenant", claim.TenantId.Value);
        command.Parameters.AddWithValue("organization", NpgsqlDbType.Uuid, (object?)claim.OrganizationId?.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("occurrence", claim.OccurrenceId.Value);
        command.Parameters.AddWithValue("key", claim.RecurrenceKey);
        command.Parameters.AddWithValue("revision", claim.Revision);
        command.Parameters.AddWithValue("hash", claim.ManifestHash);
        command.Parameters.AddWithValue("target", claim.DefinitionKey);
        command.Parameters.AddWithValue("policy", claim.RecipientPolicy switch
        {
            BackgroundJobRecipientPolicy.ProvisionedNonArchivedTenants => "provisioned_non_archived_tenants",
            BackgroundJobRecipientPolicy.ServingTenants => "serving_tenants",
            BackgroundJobRecipientPolicy.ServingOrganizations => "serving_organizations",
            _ => throw new InvalidOperationException("Recipient submission requires its closed stored policy."),
        });
        command.Parameters.AddWithValue("slot", claim.ScheduledAt);
        command.Parameters.AddWithValue("due", claim.DueAt);
        command.Parameters.AddWithValue("token", claim.Token);
        command.Parameters.AddWithValue("child", NpgsqlDbType.Uuid, (object?)Child?.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("original_until", claim.OwnershipUntil);
        return command;
    }
}
