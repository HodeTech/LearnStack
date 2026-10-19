using LearnStack.Application.Jobs;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using MediatR;
using Npgsql;
using NpgsqlTypes;

namespace LearnStack.Infrastructure.Jobs;

public sealed partial class RecordBackgroundJobOutcomeCommandHandler(IUnitOfWork unit,
    BackgroundJobDefinitionRegistry definitions, BackgroundJobRuntimeOptions options)
    : IRequestHandler<RecordBackgroundJobOutcomeCommand, Result<None>>
{
    public async Task<Result<None>> Handle(RecordBackgroundJobOutcomeCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.RequireOwner(unit);
        var authority = request.Authority;
        if (request.IsTerminalNoStart)
            return await HandleTerminalNoStartAsync(request, cancellationToken).ConfigureAwait(false);
        if (!definitions.TryGet(authority.DefinitionKey, out var definition)
            || definition.Profile != BackgroundJobExecutionProfile.External || definition.Readiness != BackgroundJobDefinitionReadiness.Active
            || definition.Queue != authority.Queue || definition.CapacityGroup != authority.CapacityGroup
            || (definition.Scope == BackgroundJobScope.TenantWide) != (authority.OrganizationId is null)
            || (request.IsNoStart || request.IsStoredCompleted) && !authority.IsRecovery)
            throw BackgroundJobExternalCheckpointSql.Refused();
        await using (var locked = Command(request, "SELECT TRUE FROM background_jobs j WHERE "
            + BackgroundJobExternalCheckpointSql.Identity(authority) + " FOR UPDATE"))
            if (await locked.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
                throw BackgroundJobExternalCheckpointSql.Refused();
        request.RequireOwner(unit);
        var stored = await ReadStoredAsync(request, definition, cancellationToken).ConfigureAwait(false);
        var budget = await BackgroundJobExternalCheckpointSql.CaptureCommitBudgetAsync(unit,
            authority, options, cancellationToken).ConfigureAwait(false);
        request.RequireOwner(unit);
        if (request.IsStoredCompleted)
        {
            // This mode can only select from the fresh, validated stored facts.
            // It never supplies or rewrites an outcome or reservation release.
            if (stored.OutcomeState is not { } state || stored.OutcomeJson is null
                || stored.NoStartRecorded || !stored.ExecuteIntentRecorded
                || stored.SelectedNormalAttemptId is not null
                || stored.PlanFingerprint != request.PlanFingerprint || stored.OperationId != request.OperationId
                || BackgroundJobExternalPolicy.Select(state, stored.FailurePolicy, authority.NormalAttemptOrdinal)
                    .Disposition != BackgroundJobKnownOutcomeDisposition.Finalize)
                throw BackgroundJobExternalCheckpointSql.Refused();
        }
        else if (request.IsNoStart)
        {
            if (stored.ExecuteIntentRecorded || stored.OutcomeState is not null) throw BackgroundJobExternalCheckpointSql.Refused();
            // Reused-plan admissions can stop before Arm. Bind this exact
            // attempt to the already validated job plan without inventing an
            // ExecuteIntent, including retained no-start history from before
            // that binding was written.
            await WriteNoStartAsync(request, cancellationToken).ConfigureAwait(false);
            if (!stored.NoStartRecorded)
                await ReleaseAsync(request, "never_started", cancellationToken).ConfigureAwait(false);
            stored = await ReadStoredAsync(request, definition, cancellationToken).ConfigureAwait(false);
        }
        else if (stored.OutcomeState is null)
        {
            // Valid persisted facts dominate a later conflicting report. Validate
            // new report facts only when this is the first actual outcome write.
            if (stored.NoStartRecorded || !stored.ExecuteIntentRecorded || request.QuiescenceId == Guid.Empty
                || !Enum.IsDefined(request.QuiescenceKind) || stored.PlanFingerprint != request.PlanFingerprint
                || stored.OperationId != request.OperationId)
                throw BackgroundJobExternalCheckpointSql.Refused();
            _ = BackgroundJobExternalPolicy.Select(request.State, request.Policy, authority.NormalAttemptOrdinal);
            var outcomeType = definition.ProfileRegistration.OutcomeType!;
            if (BackgroundJobPayloadContract.Serialize(BackgroundJobPayloadContract.Read(request.OutcomeJson, outcomeType,
                BackgroundJobPayloadKind.Outcome), outcomeType, BackgroundJobPayloadKind.Outcome) != request.OutcomeJson)
                throw BackgroundJobExternalCheckpointSql.Refused();
            await WriteOutcomeAsync(request, cancellationToken).ConfigureAwait(false);
            await ReleaseAsync(request, "quiescent", cancellationToken).ConfigureAwait(false);
            stored = await ReadStoredAsync(request, definition, cancellationToken).ConfigureAwait(false);
        }
        await SelectFinalizationAsync(request, stored, cancellationToken).ConfigureAwait(false);
        request.RecordCheckpoint(unit, budget.ObservedAt, budget.Remaining);
        return Result<None>.Ok(None.Value);
    }

    private async Task<BackgroundJobExternalSnapshot> ReadStoredAsync(RecordBackgroundJobOutcomeCommand request,
        BackgroundJobDefinitionDescriptor definition, CancellationToken token)
    {
        var authority = request.Authority;
        var predicate = BackgroundJobExternalCheckpointSql.Identity(authority)
            + BackgroundJobExternalCheckpointSql.Live(authority) + BackgroundJobExternalCheckpointSql.Phase(authority);
        BackgroundJobEnvelope envelope;
        await using (var command = Command(request, "SELECT " + BackgroundJobEnvelopeSql.Projection
            + " FROM background_jobs j WHERE " + predicate))
        await using (var row = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            var found = await row.ReadAsync(token).ConfigureAwait(false);
            request.RequireOwner(unit);
            if (!found) throw BackgroundJobExternalCheckpointSql.Refused();
            envelope = BackgroundJobEnvelopeSql.Read(row);
            if (!authority.MatchesEnvelope(envelope)
                || !ReferenceEquals(BackgroundJobEnvelopeSql.ExternalDefinition(envelope, definitions), definition))
                throw BackgroundJobExternalCheckpointSql.Refused();
        }
        await using var facts = Command(request,
            "SELECT " + BackgroundJobStoredExternalFacts.Projection + BackgroundJobStoredExternalFacts.From + " " + predicate);
        facts.Parameters.AddWithValue("normal_attempt", authority.AttemptId.Value);
        await using var stored = await facts.ExecuteReaderAsync(token).ConfigureAwait(false);
        var exists = await stored.ReadAsync(token).ConfigureAwait(false);
        request.RequireOwner(unit);
        if (!exists) throw BackgroundJobExternalCheckpointSql.Refused();
        return BackgroundJobStoredExternalFacts.Read(stored, definition, envelope, authority.NormalAttemptOrdinal, authority.AttemptId);
    }

    private async Task WriteOutcomeAsync(RecordBackgroundJobOutcomeCommand request, CancellationToken token)
    {
        var authority = request.Authority;
        await using var write = Command(request, """
            UPDATE background_job_attempts a SET outcome=@outcome,outcome_kind=@state,outcome_at=clock_timestamp(),
                failure_policy=@policy,quiescence_id=@evidence,quiescence_kind=@kind,quiescent_at=clock_timestamp()
            FROM background_jobs j WHERE a.id=@attempt AND a.tenant_id=@tenant AND a.job_id=j.id
                AND a.execute_intent_at IS NOT NULL AND a.no_start_at IS NULL AND a.outcome IS NULL
                AND a.plan_fingerprint=@fingerprint AND a.operation_id=@operation
                AND j.phase IN ('executing','reconciling') AND j.plan_fingerprint=@fingerprint AND j.operation_id=@operation AND
            """ + " " + BackgroundJobExternalCheckpointSql.Identity(authority) + BackgroundJobExternalCheckpointSql.Live(authority) + " RETURNING TRUE");
        write.Parameters.AddWithValue("fingerprint", request.PlanFingerprint);
        write.Parameters.AddWithValue("operation", request.OperationId);
        write.Parameters.AddWithValue("outcome", NpgsqlDbType.Text, request.OutcomeJson);
        write.Parameters.AddWithValue("state", request.State switch
        {
            BackgroundJobExternalState.Succeeded => "succeeded",
            BackgroundJobExternalState.Failed => "failed",
            BackgroundJobExternalState.Cancelled => "cancelled",
            _ => throw BackgroundJobExternalCheckpointSql.Refused(),
        });
        write.Parameters.AddWithValue("policy", NpgsqlDbType.Text, request.Policy switch
        {
            null => DBNull.Value,
            BackgroundJobExternalFailurePolicy.Permanent => "permanent",
            BackgroundJobExternalFailurePolicy.TransientRepeatable => "transient_repeatable",
            BackgroundJobExternalFailurePolicy.TransientNotRepeatable => "transient_not_repeatable",
            _ => throw BackgroundJobExternalCheckpointSql.Refused(),
        });
        write.Parameters.AddWithValue("evidence", request.QuiescenceId);
        write.Parameters.AddWithValue("kind", request.QuiescenceKind == BackgroundJobQuiescenceKind.ProviderFenced
            ? "provider_fenced" : "execution_terminated");
        if (await write.ExecuteScalarAsync(token).ConfigureAwait(false) is not true) throw BackgroundJobExternalCheckpointSql.Refused();
        request.RequireOwner(unit);
    }

    private async Task WriteNoStartAsync(RecordBackgroundJobOutcomeCommand request, CancellationToken token)
    {
        var authority = request.Authority;
        await using var write = Command(request, """
            UPDATE background_job_attempts a SET no_start_at=COALESCE(a.no_start_at,clock_timestamp()),
                plan_fingerprint=j.plan_fingerprint,operation_id=j.operation_id
            FROM background_jobs j WHERE a.id=@attempt AND a.tenant_id=@tenant AND a.job_id=j.id
                AND a.execute_intent_at IS NULL AND a.outcome IS NULL
                AND j.phase='reconciling' AND j.ownership_kind='recovery' AND j.ownership_token<>a.admission_token AND
            """ + " " + BackgroundJobExternalCheckpointSql.Identity(authority) + BackgroundJobExternalCheckpointSql.Live(authority) + " RETURNING TRUE");
        if (await write.ExecuteScalarAsync(token).ConfigureAwait(false) is not true) throw BackgroundJobExternalCheckpointSql.Refused();
        request.RequireOwner(unit);
    }

    private async Task ReleaseAsync(RecordBackgroundJobOutcomeCommand request, string reason, CancellationToken token)
    {
        await using var release = Command(request, """
            UPDATE background_job_reservations SET released_at=clock_timestamp(),release_reason=@release
            WHERE tenant_id=@tenant AND capacity_group=@capacity AND job_id=@id AND attempt_id=@attempt
                AND released_at IS NULL RETURNING tenant_id,capacity_group,job_id,attempt_id
            """);
        release.Parameters.AddWithValue("release", reason);
        await using var row = await release.ExecuteReaderAsync(token).ConfigureAwait(false);
        var found = await row.ReadAsync(token).ConfigureAwait(false);
        request.RequireOwner(unit);
        var authority = request.Authority;
        if (!found || row.GetGuid(0) != authority.TenantId.Value || row.GetString(1) != authority.CapacityGroup
            || row.GetGuid(2) != authority.JobId.Value || row.GetGuid(3) != authority.AttemptId.Value)
            throw BackgroundJobExternalCheckpointSql.Refused();
    }

    private async Task SelectFinalizationAsync(RecordBackgroundJobOutcomeCommand request,
        BackgroundJobExternalSnapshot stored, CancellationToken token)
    {
        var authority = request.Authority;
        BackgroundJobKnownOutcomeDecision? decision = stored.OutcomeState is { } state
            ? BackgroundJobExternalPolicy.Select(state, stored.FailurePolicy, authority.NormalAttemptOrdinal)
            : stored.NoStartRecorded && stored.PlanJson is not null && authority.NormalAttemptOrdinal == 6
                ? new(BackgroundJobKnownOutcomeDisposition.Finalize, BackgroundJobFinalizationStatus.Failed,
                    BackgroundJobFinalizationReason.NormalAttemptsExhausted, "retry_exhausted") : null;
        if (decision?.Disposition != BackgroundJobKnownOutcomeDisposition.Finalize) return;
        await using var select = Command(request, """
            UPDATE background_jobs j SET final_outcome_attempt_id=@attempt,finalization_status=@status,
                finalization_reason=@reason,phase='finalize_pending'
            WHERE
            """ + " " + BackgroundJobExternalCheckpointSql.Identity(authority) + BackgroundJobExternalCheckpointSql.Live(authority)
            + " AND j.phase IN ('executing','reconciling') AND j.finalization_status IS NULL RETURNING TRUE");
        select.Parameters.AddWithValue("status", decision.FinalizationStatus == BackgroundJobFinalizationStatus.Succeeded ? "succeeded" : "failed");
        select.Parameters.AddWithValue("reason", decision.FinalizationReason switch
        {
            BackgroundJobFinalizationReason.None => "none",
            BackgroundJobFinalizationReason.ExternalFailed => "external_failed",
            BackgroundJobFinalizationReason.ExternalCancelled => "external_cancelled",
            BackgroundJobFinalizationReason.NormalAttemptsExhausted => "normal_attempts_exhausted",
            _ => throw BackgroundJobExternalCheckpointSql.Refused(),
        });
        if (await select.ExecuteScalarAsync(token).ConfigureAwait(false) is not true) throw BackgroundJobExternalCheckpointSql.Refused();
        request.RequireOwner(unit);
    }

    private NpgsqlCommand Command(RecordBackgroundJobOutcomeCommand request, string sql)
    {
        request.RequireOwner(unit);
        return BackgroundJobExternalCheckpointSql.Command(unit, request.Authority, options, sql);
    }
}
