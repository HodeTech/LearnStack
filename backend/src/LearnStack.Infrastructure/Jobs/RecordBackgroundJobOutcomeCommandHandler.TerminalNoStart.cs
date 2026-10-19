using LearnStack.Application.Jobs;
using LearnStack.SharedKernel.Results;

namespace LearnStack.Infrastructure.Jobs;

public sealed partial class RecordBackgroundJobOutcomeCommandHandler
{
    private async Task<Result<None>> HandleTerminalNoStartAsync(RecordBackgroundJobOutcomeCommand request,
        CancellationToken token)
    {
        if (!request.Authority.IsTerminalNoStart || !request.IsNoStart)
            throw BackgroundJobExternalCheckpointSql.Refused();
        await using (var locked = Command(request, "SELECT TRUE FROM background_jobs j WHERE "
            + BackgroundJobExternalCheckpointSql.Identity(request.Authority) + " FOR UPDATE"))
            if (await locked.ExecuteScalarAsync(token).ConfigureAwait(false) is not true)
                throw BackgroundJobExternalCheckpointSql.Refused();
        request.RequireOwner(unit);
        await RequireTerminalNoStartHistoryAsync(request, token).ConfigureAwait(false);
        var stored = await BackgroundJobStoredExternalFacts.ReadTerminalNoStartAsync(unit, request.Authority, options, token)
            .ConfigureAwait(false);
        request.RequireOwner(unit);
        var budget = await BackgroundJobExternalCheckpointSql.CaptureCommitBudgetAsync(unit, request.Authority, options, token)
            .ConfigureAwait(false);
        if (!stored.NoStartRecorded)
        {
            await using var write = Command(request, """
                UPDATE background_job_attempts a SET no_start_at=clock_timestamp()
                FROM background_jobs j WHERE a.id=@attempt AND a.tenant_id=@tenant AND a.job_id=j.id
                    AND a.no_start_at IS NULL
                    AND ((a.plan_fingerprint IS NULL AND a.operation_id IS NULL)
                        OR (a.plan_fingerprint=j.plan_fingerprint AND a.operation_id=j.operation_id))
                    AND a.execute_intent_at IS NULL AND a.outcome IS NULL AND j.ownership_kind='recovery'
                    AND j.ownership_token<>a.admission_token AND
                """ + " " + BackgroundJobExternalCheckpointSql.Identity(request.Authority)
                + BackgroundJobExternalCheckpointSql.Live(request.Authority) + " RETURNING TRUE");
            if (await write.ExecuteScalarAsync(token).ConfigureAwait(false) is not true)
                throw BackgroundJobExternalCheckpointSql.Refused();
            request.RequireOwner(unit);
            await ReleaseAsync(request, "never_started", token).ConfigureAwait(false);
        }
        await RequireTerminalNoStartHistoryAsync(request, token).ConfigureAwait(false);
        stored = await BackgroundJobStoredExternalFacts.ReadTerminalNoStartAsync(unit, request.Authority, options, token)
            .ConfigureAwait(false);
        request.RequireOwner(unit);
        if (!stored.NoStartRecorded) throw BackgroundJobExternalCheckpointSql.Refused();
        request.RecordCheckpoint(unit, budget.ObservedAt, budget.Remaining);
        return Result<None>.Ok(None.Value);
    }

    private async Task RequireTerminalNoStartHistoryAsync(RecordBackgroundJobOutcomeCommand request, CancellationToken token)
    {
        await using var command = Command(request, "SELECT TRUE FROM background_jobs j WHERE "
            + BackgroundJobExternalCheckpointSql.Identity(request.Authority) + BackgroundJobExternalCheckpointSql.Live(request.Authority));
        if (await command.ExecuteScalarAsync(token).ConfigureAwait(false) is not true)
            throw BackgroundJobExternalCheckpointSql.Refused();
        request.RequireOwner(unit);
    }
}
