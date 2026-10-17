using LearnStack.Application.Jobs;
using LearnStack.SharedKernel.Jobs;
using Npgsql;
using NpgsqlTypes;

namespace LearnStack.Infrastructure.Jobs;

internal enum BackgroundJobRecipientResolutionStatus { Accepted, Skipped, Deferred, LostClaim, Unavailable, Quarantined }
internal sealed record BackgroundJobRecipientResolution(BackgroundJobRecipientResolutionStatus Status, BackgroundJobId? Child = null);

internal sealed partial class PostgresBackgroundJobControlStore
{
    private const string RecipientHistoryRoute = """
        id=@recipient AND tenant_id=@tenant AND organization_id IS NOT DISTINCT FROM @organization
        AND occurrence_id=@occurrence AND recurrence_key=@key AND revision=@revision AND manifest_hash=@hash
        AND target_definition_key=@target AND recipient_policy=@policy AND scheduled_at=@slot AND due_at=@due
        """;

    /// <summary>A locked durable observation never recreates entry authority or the original COMMIT acknowledgement.</summary>
    internal async Task<OwnedBackgroundJobControl<BackgroundJobRecipientResolution>> ResolveRecipientAsync(
        JobRecipientSubmissionResult original, BackgroundJobLifecycleProjection projection, CancellationToken cancellationToken = default)
    {
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(projection);
        // A finished caller wait, a later row lock or a new connection cannot
        // clean the original application task's retained resources.
        if (original.Command is not { } request || original.CleanupFailed || original.RetainedCleanup.Count != 0
            || !original.SourceOwner.CleanupConfirmed || !ReferenceEquals(original.SourceOwner, request.SourceOwner))
            throw new InvalidOperationException("Recipient resolution requires its original completed application cleanup.");
        var claim = request.Claim;
        var resources = new BackgroundJobControlResources();
        BackgroundJobRecipientResolution? candidate = null;
        using var deadline = Deadline(cancellationToken, probe: true);
        try
        {
            if (await OpenAsync(resources, deadline.Token).ConfigureAwait(false))
            {
                var command = RecipientHistoryCommand(resources, claim,
                    "SELECT id FROM background_job_recipients WHERE id=@recipient AND tenant_id=@tenant FOR UPDATE SKIP LOCKED");
                resources.SetReader(await command.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false));
                var locked = await resources.Reader!.ReadAsync(deadline.Token).ConfigureAwait(false);
                await FinishAsync(resources).ConfigureAwait(false);
                if (locked)
                {
                    candidate = await ResolveLockedRecipientAsync(resources, claim, projection, deadline.Token).ConfigureAwait(false);
                    await CommitAsync(resources, deadline.Token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure)) { resources.RecordFailure(failure); }
        finally { await resources.CleanupAsync().ConfigureAwait(false); }
        var published = resources.CommitAcknowledged && resources.CleanupConfirmed && resources.Failure is null ? candidate : null;
        return new(published, resources, []);
    }

    private async Task<BackgroundJobRecipientResolution> ResolveLockedRecipientAsync(BackgroundJobControlResources resources,
        BackgroundJobRecipientClaim claim, BackgroundJobLifecycleProjection projection, CancellationToken token)
    {
        var command = RecipientHistoryCommand(resources, claim, """
            SELECT accepted_job_id,skipped_at,quarantined_at,ownership_token,ownership_until,clock_timestamp(),
                EXISTS(SELECT 1 FROM background_jobs j WHERE j.tenant_id=background_job_recipients.tenant_id
                    AND j.id=accepted_job_id AND j.recipient_id=background_job_recipients.id
                    AND j.organization_id IS NOT DISTINCT FROM background_job_recipients.organization_id
                    AND j.definition_key=target_definition_key AND j.original_due_at=due_at)
            FROM background_job_recipients WHERE
            """ + " " + RecipientHistoryRoute);
        resources.SetReader(await command.ExecuteReaderAsync(token).ConfigureAwait(false));
        BackgroundJobRecipientResolution? observation = null;
        if (!await resources.Reader!.ReadAsync(token).ConfigureAwait(false))
            observation = new(BackgroundJobRecipientResolutionStatus.Unavailable);
        else if (!resources.Reader.IsDBNull(0))
        {
            if (!resources.Reader.GetBoolean(6)) throw new InvalidOperationException("The stored recipient child receipt is inconsistent.");
            observation = new(BackgroundJobRecipientResolutionStatus.Accepted, BackgroundJobId.From(resources.Reader.GetGuid(0)));
        }
        else if (!resources.Reader.IsDBNull(1)) observation = new(BackgroundJobRecipientResolutionStatus.Skipped);
        else if (!resources.Reader.IsDBNull(2)) observation = new(BackgroundJobRecipientResolutionStatus.Quarantined);
        else if (resources.Reader.IsDBNull(3) || resources.Reader.GetGuid(3) != claim.Token || resources.Reader.IsDBNull(4)
            || resources.Reader.GetFieldValue<DateTimeOffset>(4) <= resources.Reader.GetFieldValue<DateTimeOffset>(5))
            observation = new(BackgroundJobRecipientResolutionStatus.LostClaim);
        await FinishAsync(resources).ConfigureAwait(false);
        // Final receipt precedes token checks: acceptance deliberately clears
        // the token, and a later acquisition may have committed the same route.
        if (observation is not null) return observation;

        var eligible = false;
        var deleted = false;
        command = RecipientHistoryCommand(resources, claim,
            "SELECT status,deleted_at,default_organization_id FROM tenants WHERE id=@tenant");
        resources.SetReader(await command.ExecuteReaderAsync(token).ConfigureAwait(false));
        if (await resources.Reader!.ReadAsync(token).ConfigureAwait(false))
        {
            deleted = !resources.Reader.IsDBNull(1);
            eligible = projection.AllowsTenant(resources.Reader.GetString(0), deleted,
                resources.Reader.IsDBNull(2) ? null : resources.Reader.GetGuid(2), claim.RecipientPolicy);
        }
        await FinishAsync(resources).ConfigureAwait(false);
        if (eligible && claim.OrganizationId is not null)
        {
            eligible = false;
            command = RecipientHistoryCommand(resources, claim,
                "SELECT status,deleted_at FROM organizations WHERE tenant_id=@tenant AND id=@organization");
            resources.SetReader(await command.ExecuteReaderAsync(token).ConfigureAwait(false));
            if (await resources.Reader!.ReadAsync(token).ConfigureAwait(false))
            {
                deleted = !resources.Reader.IsDBNull(1);
                eligible = projection.AllowsOrganization(resources.Reader.GetString(0), deleted);
            }
            await FinishAsync(resources).ConfigureAwait(false);
        }
        command = RecipientHistoryCommand(resources, claim, (eligible
            ? "UPDATE background_job_recipients SET available_after=clock_timestamp()+@backoff,ownership_token=NULL,ownership_until=NULL,last_error=NULL WHERE "
            : "UPDATE background_job_recipients SET skipped_at=clock_timestamp(),skipped_reason=@reason,ownership_token=NULL,ownership_until=NULL,last_error=NULL WHERE ")
            + RecipientHistoryRoute
            + " AND ownership_token=@token AND ownership_until>clock_timestamp() AND accepted_job_id IS NULL AND skipped_at IS NULL AND quarantined_at IS NULL");
        command.Parameters.AddWithValue("backoff", options.HandoffRepairGrace);
        command.Parameters.AddWithValue("reason", deleted ? "deleted" : "ineligible");
        var changed = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
        await FinishAsync(resources).ConfigureAwait(false);
        return new(!changed ? BackgroundJobRecipientResolutionStatus.LostClaim
            : eligible ? BackgroundJobRecipientResolutionStatus.Deferred : BackgroundJobRecipientResolutionStatus.Skipped);
    }

    private NpgsqlCommand RecipientHistoryCommand(BackgroundJobControlResources resources, BackgroundJobRecipientClaim claim, string sql)
    {
        var command = Command(resources, sql);
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
            _ => throw new InvalidOperationException("Recipient resolution requires its stored finite policy."),
        });
        command.Parameters.AddWithValue("slot", claim.ScheduledAt);
        command.Parameters.AddWithValue("due", claim.DueAt);
        command.Parameters.AddWithValue("token", claim.Token);
        return command;
    }
}
