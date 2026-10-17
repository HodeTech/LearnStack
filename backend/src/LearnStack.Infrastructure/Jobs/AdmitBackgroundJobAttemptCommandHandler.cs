using LearnStack.Application.Jobs;
using LearnStack.SharedKernel.Entitlements;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Time;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
using NpgsqlTypes;

namespace LearnStack.Infrastructure.Jobs;

public sealed class AdmitBackgroundJobAttemptCommandHandler(IUnitOfWork unit,
    BackgroundJobDefinitionRegistry definitions, BackgroundJobRuntimeOptions options, IGuidFactory ids,
    IFeatureFlags features, ITenantContextAccessor accessor)
    : IRequestHandler<AdmitBackgroundJobAttemptCommand, Result<None>>
{
    public async Task<Result<None>> Handle(AdmitBackgroundJobAttemptCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        BackgroundJobExecutionAdmission.RequireStart(request);
        options.Validate();
        if (unit.Mode != TransactionMode.ReadWrite)
            throw new InvalidOperationException("Attempt admission requires a writable owner.");
        var transaction = unit.Transaction;
        var connection = unit.Connection;
        var announcement = unit.GetTenantContextAnnouncement(transaction);
        var context = accessor.Current ?? throw new InvalidOperationException("Admission requires its trusted context.");
        var sourceOwner = JobApplicationSourceOwner.Current
            ?? throw new InvalidOperationException("Admission requires its original application-source owner.");
        void RequireCurrent()
        {
            cancellationToken.ThrowIfCancellationRequested();
            BackgroundJobExecutionAdmission.RequireStart(request);
            BackgroundJobExecutionAdmission.RequireContext(context);
            sourceOwner.RequireOwnedMainUnit(unit);
            if (!ReferenceEquals(sourceOwner, JobApplicationSourceOwner.Current) || !ReferenceEquals(context, accessor.Current)
                || !ReferenceEquals(connection, unit.Connection) || !ReferenceEquals(transaction, unit.Transaction)
                || !ReferenceEquals(transaction?.Connection, connection) || !unit.HasActiveTransaction || unit.IsRollbackOnly
                || request.Gate.IsClosed
                || unit.Mode != TransactionMode.ReadWrite || announcement is not { Generation: > 0 }
                || announcement != unit.GetTenantContextAnnouncement(transaction)
                || announcement.TenantId != request.Envelope.TenantId || announcement.OrganizationId != request.Envelope.OrganizationId)
                throw new InvalidOperationException("Attempt admission lost its exact announced owner.");
        }
        RequireCurrent();
        // Lock first. All lease/due time predicates below execute after this wait has ended.
        await using (var locked = BackgroundJobEnvelopeSql.Command(unit,
            "SELECT " + BackgroundJobEnvelopeSql.Projection
                + " FROM background_jobs WHERE id = @id AND tenant_id = @tenant FOR UPDATE", request.Claim, options))
        await using (var reader = await locked.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            var found = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            RequireCurrent();
            if (!found
                || BackgroundJobEnvelopeSql.Read(reader) != request.Envelope
                || BackgroundJobEnvelopeSql.RoutedDefinition(request.Envelope, request.Claim, definitions) is null)
                return Result<None>.Fail(BackgroundJobExecutionAdmission.Refused);
        }
        RequireCurrent();
        if (request.Claim.LedgerStage == BackgroundJobLedgerStage.Finalize)
        {
            // Finalize has its own short counted ledger, and never reserves
            // external capacity a second time or consults an occupancy limit.
            var finalizedAttempt = BackgroundJobAttemptId.From(ids.NewUuidV7());
            int finalizeOrdinal;
            BackgroundJobAttemptId selected;
            DateTimeOffset finalizedAdmittedAt, finalizedDeadline;
            await using (var insertFinalize = BackgroundJobEnvelopeSql.Command(unit, """
                INSERT INTO background_job_attempts
                    (id,tenant_id,organization_id,job_id,stage,ordinal,admission_token,deadline,final_outcome_attempt_id)
                SELECT @attempt,tenant_id,organization_id,id,'finalize',finalize_attempts+1,ownership_token,
                    clock_timestamp()+@duration,final_outcome_attempt_id
                FROM background_jobs WHERE id=@id AND tenant_id=@tenant AND wake_generation=@generation
                    AND ownership_token=@token AND ownership_kind='routing' AND ownership_until>clock_timestamp()
                    AND available_after<=clock_timestamp() AND original_due_at<=clock_timestamp()
                    AND profile='external' AND phase='finalize_pending' AND finalize_attempts<6
                    AND normal_attempts BETWEEN 1 AND 6 AND final_outcome_attempt_id IS NOT NULL
                    AND finalization_status IS NOT NULL AND finalization_reason IS NOT NULL AND plan IS NOT NULL
                    AND processed_at IS NULL AND dead_lettered_at IS NULL AND terminal_reason IS NULL
                RETURNING ordinal,admitted_at,deadline,final_outcome_attempt_id
                """, request.Claim, options))
            {
                insertFinalize.Parameters.AddWithValue("attempt", finalizedAttempt.Value);
                insertFinalize.Parameters.AddWithValue("duration", options.TransactionTimeout);
                insertFinalize.Parameters.AddWithValue("generation", request.Claim.WakeGeneration);
                insertFinalize.Parameters.AddWithValue("token", request.Claim.Token);
                await using var row = await insertFinalize.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                var found = await row.ReadAsync(cancellationToken).ConfigureAwait(false);
                RequireCurrent();
                if (!found) return Result<None>.Fail(BackgroundJobExecutionAdmission.Refused);
                finalizeOrdinal = row.GetInt32(0);
                finalizedAdmittedAt = row.GetFieldValue<DateTimeOffset>(1);
                finalizedDeadline = row.GetFieldValue<DateTimeOffset>(2);
                selected = BackgroundJobAttemptId.From(row.GetGuid(3));
            }
            RequireCurrent();
            await using var updateFinalize = BackgroundJobEnvelopeSql.Command(unit, """
                UPDATE background_jobs SET finalize_attempts=finalize_attempts+1,current_attempt_id=@attempt,
                    phase='finalizing',attempt_deadline=@deadline
                WHERE id=@id AND tenant_id=@tenant AND wake_generation=@generation
                    AND ownership_token=@token AND ownership_kind='routing' AND ownership_until>clock_timestamp()
                    AND available_after<=clock_timestamp() AND original_due_at<=clock_timestamp()
                    AND profile='external' AND phase='finalize_pending' AND finalize_attempts<6
                    AND final_outcome_attempt_id=@selected AND plan IS NOT NULL
                    AND processed_at IS NULL AND dead_lettered_at IS NULL AND terminal_reason IS NULL
                RETURNING normal_attempts,finalize_attempts,final_outcome_attempt_id,attempt_deadline,ownership_until
                """, request.Claim, options);
            updateFinalize.Parameters.AddWithValue("attempt", finalizedAttempt.Value);
            updateFinalize.Parameters.AddWithValue("deadline", finalizedDeadline);
            updateFinalize.Parameters.AddWithValue("selected", selected.Value);
            updateFinalize.Parameters.AddWithValue("generation", request.Claim.WakeGeneration);
            updateFinalize.Parameters.AddWithValue("token", request.Claim.Token);
            await using var updatedFinalize = await updateFinalize.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var updated = await updatedFinalize.ReadAsync(cancellationToken).ConfigureAwait(false);
            RequireCurrent();
            if (!updated || updatedFinalize.GetInt32(1) != finalizeOrdinal || updatedFinalize.GetGuid(2) != selected.Value
                || updatedFinalize.GetFieldValue<DateTimeOffset>(3) != finalizedDeadline)
            {
                unit.MarkRollbackOnly();
                return Result<None>.Fail(BackgroundJobExecutionAdmission.Refused);
            }
            request.RecordAttempt(unit, finalizedAttempt, updatedFinalize.GetInt32(0), finalizeOrdinal,
                BackgroundJobExecutionStage.Finalize, selected, finalizedAdmittedAt, finalizedDeadline,
                updatedFinalize.GetFieldValue<DateTimeOffset>(4));
            return Result<None>.Ok(None.Value);
        }
        if (request.Envelope.CapacityGroup is { } groupKey)
        {
            var group = definitions.Infrastructure.CapacityGroups[groupKey];
            await using (var ensure = BackgroundJobEnvelopeSql.Command(unit, """
                INSERT INTO background_job_capacity (tenant_id, capacity_group)
                VALUES (@tenant, @group) ON CONFLICT DO NOTHING
                """, request.Claim, options))
            {
                ensure.Parameters.AddWithValue("group", groupKey);
                await ensure.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            RequireCurrent();
            await using (var mutex = BackgroundJobEnvelopeSql.Command(unit, """
                SELECT TRUE FROM background_job_capacity
                WHERE tenant_id=@tenant AND capacity_group=@group FOR UPDATE
                """, request.Claim, options))
            {
                mutex.Parameters.AddWithValue("group", groupKey);
                if (await mutex.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
                    throw new InvalidOperationException("The declared capacity mutex is unavailable.");
            }
            RequireCurrent();
            long ceiling = group.OperationalCeiling;
            if (group.PlanLimit is { } limit)
            {
                if (!LimitKeys.All.TryGetValue(limit, out var descriptor) || descriptor.Enforcement != LimitEnforcement.Hard)
                    ceiling = 0;
                else
                {
                    try
                    {
                        var projected = await features.GetLimitAsync(limit, cancellationToken).ConfigureAwait(false);
                        RequireCurrent();
                        ceiling = projected == LimitKeys.Unlimited ? ceiling : projected > 0 ? Math.Min(projected, ceiling) : 0;
                    }
                    catch (Exception failure) when (failure.GetType() != typeof(OutOfMemoryException)
                        && failure.GetType() != typeof(StackOverflowException) && !cancellationToken.IsCancellationRequested)
                    {
                        // A failed projection cannot manufacture unlimited admission.
                        RequireCurrent();
                        ceiling = 0;
                    }
                }
            }
            RequireCurrent();
            await using var occupied = BackgroundJobEnvelopeSql.Command(unit, """
                SELECT count(*) FROM background_job_reservations
                WHERE tenant_id=@tenant AND capacity_group=@group AND released_at IS NULL
                """, request.Claim, options);
            occupied.Parameters.AddWithValue("group", groupKey);
            // Separate statement AFTER the mutex wait: count every unreleased row,
            // including sibling organizations and unknown/expired old attempts.
            var count = (long)(await occupied.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
            RequireCurrent();
            if (ceiling <= 0 || count >= ceiling)
            {
                request.RecordCapacityDeferred(unit);
                return Result<None>.Fail(BackgroundJobExecutionAdmission.Refused);
            }
        }
        var attempt = BackgroundJobAttemptId.From(ids.NewUuidV7());
        RequireCurrent();
        await using var insert = BackgroundJobEnvelopeSql.Command(unit, """
            INSERT INTO background_job_attempts
                (id, tenant_id, organization_id, job_id, stage, ordinal, admission_token, deadline)
            SELECT @attempt, tenant_id, organization_id, id, 'normal', normal_attempts + 1,
                ownership_token, clock_timestamp() + @duration
            FROM background_jobs WHERE id = @id AND tenant_id = @tenant
                AND wake_generation = @generation AND ownership_token = @token AND ownership_kind = 'routing'
                AND ownership_until > clock_timestamp() AND available_after <= clock_timestamp()
                AND profile = @profile AND capacity_group IS NOT DISTINCT FROM @group AND phase = 'pending' AND normal_attempts < 6
                AND (profile='transactional' OR (profile='external' AND finalization_status IS NULL
                    AND NOT EXISTS (SELECT 1 FROM background_job_attempts a
                        WHERE a.tenant_id=background_jobs.tenant_id AND a.job_id=background_jobs.id AND a.stage='normal'
                            AND (a.no_start_at IS NULL AND a.outcome IS NULL OR a.outcome IS NOT NULL
                                AND (a.quiescent_at IS NULL OR a.outcome_kind NOT IN ('failed','cancelled')
                                    OR a.failure_policy IS DISTINCT FROM 'transient_repeatable')))))
                AND NOT EXISTS (SELECT 1 FROM background_job_reservations r WHERE r.tenant_id=background_jobs.tenant_id
                    AND r.job_id=background_jobs.id AND r.released_at IS NULL)
                AND processed_at IS NULL AND dead_lettered_at IS NULL AND terminal_reason IS NULL
            RETURNING ordinal, admitted_at, deadline
            """, request.Claim, options);
        insert.Parameters.AddWithValue("attempt", attempt.Value);
        insert.Parameters.AddWithValue("generation", request.Claim.WakeGeneration);
        insert.Parameters.AddWithValue("token", request.Claim.Token);
        insert.Parameters.AddWithValue("duration", request.Envelope.Profile == "external" ? options.ExternalWaitTimeout : options.TransactionTimeout);
        insert.Parameters.AddWithValue("profile", request.Envelope.Profile);
        insert.Parameters.AddWithValue("group", NpgsqlDbType.Text, (object?)request.Envelope.CapacityGroup ?? DBNull.Value);
        int ordinal;
        DateTimeOffset admittedAt;
        DateTimeOffset deadline;
        await using (var inserted = await insert.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            var found = await inserted.ReadAsync(cancellationToken).ConfigureAwait(false);
            RequireCurrent();
            if (!found)
                return Result<None>.Fail(BackgroundJobExecutionAdmission.Refused);
            ordinal = inserted.GetInt32(0);
            admittedAt = inserted.GetFieldValue<DateTimeOffset>(1);
            deadline = inserted.GetFieldValue<DateTimeOffset>(2);
        }
        RequireCurrent();
        await using var update = BackgroundJobEnvelopeSql.Command(unit, """
            UPDATE background_jobs SET normal_attempts = normal_attempts + 1, current_attempt_id = @attempt,
                phase = CASE WHEN profile='transactional' THEN 'executing' WHEN plan IS NULL THEN 'preparing' ELSE 'prepared' END,
                attempt_deadline =
                    (SELECT deadline FROM background_job_attempts WHERE id = @attempt AND tenant_id = @tenant)
            WHERE id = @id AND tenant_id = @tenant AND wake_generation = @generation
                AND ownership_token = @token AND ownership_kind = 'routing' AND ownership_until > clock_timestamp()
                AND available_after <= clock_timestamp() AND phase = 'pending' AND normal_attempts < 6
                AND profile = @profile AND capacity_group IS NOT DISTINCT FROM @group
                AND processed_at IS NULL AND dead_lettered_at IS NULL AND terminal_reason IS NULL
            RETURNING normal_attempts, attempt_deadline, ownership_until, phase
            """, request.Claim, options);
        update.Parameters.AddWithValue("attempt", attempt.Value);
        update.Parameters.AddWithValue("generation", request.Claim.WakeGeneration);
        update.Parameters.AddWithValue("token", request.Claim.Token);
        update.Parameters.AddWithValue("profile", request.Envelope.Profile);
        update.Parameters.AddWithValue("group", NpgsqlDbType.Text, (object?)request.Envelope.CapacityGroup ?? DBNull.Value);
        DateTimeOffset ownershipUntil;
        BackgroundJobExecutionStage? stage;
        await using (var updated = await update.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            var found = await updated.ReadAsync(cancellationToken).ConfigureAwait(false);
            RequireCurrent();
            if (!found || updated.GetInt32(0) != ordinal || updated.GetFieldValue<DateTimeOffset>(1) != deadline)
            {
                unit.MarkRollbackOnly();
                return Result<None>.Fail(BackgroundJobExecutionAdmission.Refused);
            }
            ownershipUntil = updated.GetFieldValue<DateTimeOffset>(2);
            stage = updated.GetString(3) switch
            {
                "executing" when request.Envelope.Profile == "transactional" => BackgroundJobExecutionStage.Transactional,
                "preparing" when request.Envelope.Profile == "external" => BackgroundJobExecutionStage.Prepare,
                "prepared" when request.Envelope.Profile == "external" => null,
                _ => throw new InvalidOperationException("Admission returned an invalid profile/stage pair."),
            };
        }
        RequireCurrent();
        if (request.Envelope.CapacityGroup is { } reservedGroup)
        {
            await using var reserve = BackgroundJobEnvelopeSql.Command(unit, """
                INSERT INTO background_job_reservations (tenant_id, capacity_group, job_id, attempt_id)
                VALUES (@tenant, @group, @id, @attempt)
                RETURNING tenant_id, capacity_group, job_id, attempt_id
                """, request.Claim, options);
            reserve.Parameters.AddWithValue("group", reservedGroup);
            reserve.Parameters.AddWithValue("attempt", attempt.Value);
            await using var row = await reserve.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var found = await row.ReadAsync(cancellationToken).ConfigureAwait(false);
            RequireCurrent();
            if (!found || row.GetGuid(0) != request.Envelope.TenantId.Value || row.GetString(1) != reservedGroup
                || row.GetGuid(2) != request.Envelope.JobId.Value || row.GetGuid(3) != attempt.Value)
                throw new InvalidOperationException("The counted admission did not reserve its exact capacity identity.");
        }
        RequireCurrent();
        request.RecordAttempt(unit, attempt, ordinal, 0, stage, null,
            admittedAt, deadline, ownershipUntil);
        return Result<None>.Ok(None.Value);
    }
}
