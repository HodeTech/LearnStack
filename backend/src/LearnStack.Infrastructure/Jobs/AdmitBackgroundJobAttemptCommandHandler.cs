using LearnStack.Application.Jobs;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Time;
using MediatR;

namespace LearnStack.Infrastructure.Jobs;

public sealed class AdmitBackgroundJobAttemptCommandHandler(IUnitOfWork unit,
    BackgroundJobDefinitionRegistry definitions, BackgroundJobRuntimeOptions options, IGuidFactory ids)
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
        var announcement = unit.GetTenantContextAnnouncement(transaction);
        // Lock first. All lease/due time predicates below execute after this wait has ended.
        await using (var locked = BackgroundJobEnvelopeSql.Command(unit,
            "SELECT " + BackgroundJobEnvelopeSql.Projection
                + " FROM background_jobs WHERE id = @id AND tenant_id = @tenant FOR UPDATE", request.Claim, options))
        await using (var reader = await locked.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                || BackgroundJobEnvelopeSql.Read(reader) != request.Envelope
                || BackgroundJobEnvelopeSql.Definition(request.Envelope, definitions) is null)
                return Result<None>.Fail(BackgroundJobExecutionAdmission.Refused);
        }
        var attempt = BackgroundJobAttemptId.From(ids.NewUuidV7());
        BackgroundJobExecutionAdmission.RequireStart(request);
        if (!ReferenceEquals(transaction, unit.Transaction) || announcement != unit.GetTenantContextAnnouncement(transaction))
            throw new InvalidOperationException("Attempt admission lost its announced transaction.");
        await using var insert = BackgroundJobEnvelopeSql.Command(unit, """
            INSERT INTO background_job_attempts
                (id, tenant_id, organization_id, job_id, stage, ordinal, admission_token, deadline)
            SELECT @attempt, tenant_id, organization_id, id, 'normal', normal_attempts + 1,
                ownership_token, clock_timestamp() + @duration
            FROM background_jobs WHERE id = @id AND tenant_id = @tenant
                AND wake_generation = @generation AND ownership_token = @token AND ownership_kind = 'routing'
                AND ownership_until > clock_timestamp() AND available_after <= clock_timestamp()
                AND profile = 'transactional' AND capacity_group IS NULL AND phase = 'pending' AND normal_attempts < 6
                AND processed_at IS NULL AND dead_lettered_at IS NULL AND terminal_reason IS NULL
            RETURNING ordinal, admitted_at, deadline
            """, request.Claim, options);
        insert.Parameters.AddWithValue("attempt", attempt.Value);
        insert.Parameters.AddWithValue("generation", request.Claim.WakeGeneration);
        insert.Parameters.AddWithValue("token", request.Claim.Token);
        insert.Parameters.AddWithValue("duration", options.TransactionTimeout);
        int ordinal;
        DateTimeOffset admittedAt;
        DateTimeOffset deadline;
        await using (var inserted = await insert.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await inserted.ReadAsync(cancellationToken).ConfigureAwait(false))
                return Result<None>.Fail(BackgroundJobExecutionAdmission.Refused);
            ordinal = inserted.GetInt32(0);
            admittedAt = inserted.GetFieldValue<DateTimeOffset>(1);
            deadline = inserted.GetFieldValue<DateTimeOffset>(2);
        }
        await using var update = BackgroundJobEnvelopeSql.Command(unit, """
            UPDATE background_jobs SET normal_attempts = normal_attempts + 1, current_attempt_id = @attempt,
                phase = 'executing', attempt_deadline =
                    (SELECT deadline FROM background_job_attempts WHERE id = @attempt AND tenant_id = @tenant)
            WHERE id = @id AND tenant_id = @tenant AND wake_generation = @generation
                AND ownership_token = @token AND ownership_kind = 'routing' AND ownership_until > clock_timestamp()
                AND available_after <= clock_timestamp() AND phase = 'pending' AND normal_attempts < 6
                AND processed_at IS NULL AND dead_lettered_at IS NULL AND terminal_reason IS NULL
            RETURNING normal_attempts, attempt_deadline, ownership_until
            """, request.Claim, options);
        update.Parameters.AddWithValue("attempt", attempt.Value);
        update.Parameters.AddWithValue("generation", request.Claim.WakeGeneration);
        update.Parameters.AddWithValue("token", request.Claim.Token);
        await using var updated = await update.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await updated.ReadAsync(cancellationToken).ConfigureAwait(false)
            || updated.GetInt32(0) != ordinal || updated.GetFieldValue<DateTimeOffset>(1) != deadline)
        {
            unit.MarkRollbackOnly();
            return Result<None>.Fail(BackgroundJobExecutionAdmission.Refused);
        }
        request.RecordAttempt(unit, attempt, ordinal, admittedAt, deadline, updated.GetFieldValue<DateTimeOffset>(2));
        return Result<None>.Ok(None.Value);
    }
}
