using LearnStack.Application.Jobs;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Infrastructure.Jobs;

public sealed class RecordBackgroundJobArmCommandHandler(IUnitOfWork unit, BackgroundJobRuntimeOptions options)
    : IRequestHandler<RecordBackgroundJobArmCommand, Result<None>>
{
    public async Task<Result<None>> Handle(RecordBackgroundJobArmCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.RequireOwner(unit);
        if (request.OperationId != BackgroundJobExternalIdentity.OperationId(request.Receipt.Envelope.JobId, request.PlanFingerprint))
            throw BackgroundJobExternalCheckpointSql.Refused();
        await using (var locked = Command("SELECT TRUE FROM background_jobs j WHERE "
            + BackgroundJobExternalCheckpointSql.Identity(request.Authority) + " FOR UPDATE"))
        {
            if (await locked.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
                throw BackgroundJobExternalCheckpointSql.Refused();
        }
        request.RequireOwner(unit);
        var budget = await BackgroundJobExternalCheckpointSql.CaptureCommitBudgetAsync(unit,
            request.Authority, options, cancellationToken).ConfigureAwait(false);
        request.RequireOwner(unit);
        await using (var intent = Command("""
            UPDATE background_job_attempts a SET plan_fingerprint=@fingerprint,operation_id=@operation,
                execute_intent_at=clock_timestamp()
            FROM background_jobs j WHERE a.id=@attempt AND a.tenant_id=@tenant AND a.job_id=j.id
                AND a.execute_intent_at IS NULL AND a.outcome IS NULL AND a.no_start_at IS NULL
                AND j.phase='prepared' AND j.plan_fingerprint=@fingerprint AND j.operation_id=@operation AND
            """ + " " + BackgroundJobExternalCheckpointSql.Identity(request.Authority) + BackgroundJobExternalCheckpointSql.Live(request.Authority) + " RETURNING TRUE"))
        {
            if (await intent.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
                throw BackgroundJobExternalCheckpointSql.Refused();
        }
        request.RequireOwner(unit);
        await using (var executing = Command("UPDATE background_jobs j SET phase='executing' WHERE "
            + BackgroundJobExternalCheckpointSql.Identity(request.Authority) + BackgroundJobExternalCheckpointSql.Live(request.Authority)
            + " AND j.phase='prepared' AND j.plan_fingerprint=@fingerprint AND j.operation_id=@operation RETURNING TRUE"))
        {
            if (await executing.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
                throw BackgroundJobExternalCheckpointSql.Refused();
        }
        request.RecordCheckpoint(unit, budget.ObservedAt, budget.Remaining);
        return Result<None>.Ok(None.Value);

        Npgsql.NpgsqlCommand Command(string sql)
        {
            request.RequireOwner(unit);
            var command = BackgroundJobExternalCheckpointSql.Command(unit, request.Authority, options, sql);
            command.Parameters.AddWithValue("fingerprint", request.PlanFingerprint);
            command.Parameters.AddWithValue("operation", request.OperationId);
            return command;
        }
    }
}
