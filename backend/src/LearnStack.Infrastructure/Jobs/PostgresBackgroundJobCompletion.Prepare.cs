using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Results;
using NpgsqlTypes;

namespace LearnStack.Infrastructure.Jobs;

internal sealed partial class PostgresBackgroundJobCompletion
{
    private async Task PreflightPrepareAsync(CancellationToken cancellationToken)
    {
        RequireCurrent();
        _ = PrepareDefinition();
        // Preflight never holds a row lock across the module's business handler.
        await using var command = BackgroundJobExternalCheckpointSql.Command(Unit, ExternalAuthority, options,
            "SELECT " + BackgroundJobEnvelopeSql.Projection
            + " FROM background_jobs j WHERE " + BackgroundJobExternalCheckpointSql.Identity(ExternalAuthority) + BackgroundJobExternalCheckpointSql.Live(ExternalAuthority)
            + " AND j.phase='preparing' AND j.plan IS NULL");
        await using var row = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var found = await row.ReadAsync(cancellationToken).ConfigureAwait(false);
        RequireCurrent();
        if (!found || BackgroundJobEnvelopeSql.Read(row) != Receipt.Envelope) throw Refused();
    }

    private async Task FencePrepareAsync(IResultBase response, CancellationToken cancellationToken)
    {
        RequireCurrent();
        var definition = PrepareDefinition();
        var plan = BackgroundJobPreparedPlan.Capture(response, definition.ProfileRegistration.PlanType!, Receipt.Envelope.JobId);
        RequireCurrent();
        await using (var locked = BackgroundJobExternalCheckpointSql.Command(Unit, ExternalAuthority, options,
            "SELECT TRUE FROM background_jobs j WHERE " + BackgroundJobExternalCheckpointSql.Identity(ExternalAuthority) + " FOR UPDATE"))
        {
            if (await locked.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true) throw Refused();
        }
        RequireCurrent();
        await CaptureCommitBudgetAsync(cancellationToken).ConfigureAwait(false);
        // This statement evaluates database time after the actual row lock wait.
        await using (var prepared = BackgroundJobExternalCheckpointSql.Command(Unit, ExternalAuthority, options,
            "UPDATE background_jobs j SET phase='prepared',plan=@plan,plan_fingerprint=@fingerprint,"
            + " operation_id=@operation,prepared_at=clock_timestamp() WHERE "
            + BackgroundJobExternalCheckpointSql.Identity(ExternalAuthority) + BackgroundJobExternalCheckpointSql.Live(ExternalAuthority)
            + " AND j.phase='preparing' AND j.plan IS NULL RETURNING plan_fingerprint,operation_id"))
        {
            prepared.Parameters.AddWithValue("plan", NpgsqlDbType.Text, plan.Json);
            prepared.Parameters.AddWithValue("fingerprint", plan.Fingerprint);
            prepared.Parameters.AddWithValue("operation", plan.OperationId);
            await using var row = await prepared.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var found = await row.ReadAsync(cancellationToken).ConfigureAwait(false);
            RequireCurrent();
            if (!found || row.GetString(0) != plan.Fingerprint || row.GetGuid(1) != plan.OperationId) throw Refused();
        }
        RequireCurrent();
        // Bind the original Prepare attempt to its plan in this same transaction.
        // Job prepared_at alone cannot confirm which earlier attempt prepared it
        // after a later retry changes current_attempt_id. No ExecuteIntent is set.
        await using (var stamp = BackgroundJobExternalCheckpointSql.Command(Unit, ExternalAuthority, options, """
            UPDATE background_job_attempts a SET plan_fingerprint=@fingerprint,operation_id=@operation
            FROM background_jobs j WHERE a.id=@attempt AND a.tenant_id=@tenant AND a.job_id=j.id
                AND a.plan_fingerprint IS NULL AND a.operation_id IS NULL AND a.execute_intent_at IS NULL
                AND a.outcome IS NULL AND a.no_start_at IS NULL AND j.phase='prepared' AND
            """ + " " + BackgroundJobExternalCheckpointSql.Identity(ExternalAuthority) + BackgroundJobExternalCheckpointSql.Live(ExternalAuthority) + " RETURNING TRUE"))
        {
            stamp.Parameters.AddWithValue("fingerprint", plan.Fingerprint);
            stamp.Parameters.AddWithValue("operation", plan.OperationId);
            if (await stamp.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true) throw Refused();
        }
        RequireCurrent();
    }

    private BackgroundJobDefinitionDescriptor PrepareDefinition()
    {
        RequireCurrent();
        if (!definitions.TryGet(Receipt.Envelope.DefinitionKey, out var definition)
            || definition.Readiness != BackgroundJobDefinitionReadiness.Active
            || definition.Profile != BackgroundJobExecutionProfile.External || Receipt.Envelope.Profile != "external"
            || definition.CapacityGroup != Receipt.Envelope.CapacityGroup || definition.CapacityGroup is null
            || definition.Queue != Receipt.Envelope.Queue
            || (definition.Scope == BackgroundJobScope.TenantWide) != (Receipt.Envelope.OrganizationId is null)) throw Refused();
        return definition;
    }
}
