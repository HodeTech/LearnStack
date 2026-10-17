using LearnStack.SharedKernel.Jobs;

namespace LearnStack.Infrastructure.Jobs;

internal sealed partial class PostgresBackgroundJobCompletion
{
    private async Task CaptureCommitBudgetAsync(CancellationToken cancellationToken)
    {
        RequireCurrent();
        // Each caller already holds the actual job lock. This separate statement
        // charges SQL/reader cleanup and all later completion work to one interval.
        await using var command = Receipt.Stage switch
        {
            BackgroundJobExecutionStage.Transactional => CreateCommand(
                "SELECT ownership_until,attempt_deadline,clock_timestamp() FROM background_jobs WHERE " + Identity + LiveTime),
            BackgroundJobExecutionStage.Prepare => BackgroundJobExternalCheckpointSql.Command(Unit, ExternalAuthority, options,
                "SELECT j.ownership_until,j.attempt_deadline,clock_timestamp() FROM background_jobs j WHERE "
                + BackgroundJobExternalCheckpointSql.Identity(ExternalAuthority) + BackgroundJobExternalCheckpointSql.Live(ExternalAuthority)
                + " AND j.phase='preparing' AND j.plan IS NULL"),
            BackgroundJobExecutionStage.Finalize => BackgroundJobExternalCheckpointSql.Command(Unit, ExternalAuthority, options,
                "SELECT j.ownership_until,j.attempt_deadline,clock_timestamp() FROM background_jobs j WHERE "
                + BackgroundJobExternalCheckpointSql.Identity(ExternalAuthority) + BackgroundJobExternalCheckpointSql.Live(ExternalAuthority)
                + " AND j.phase='finalizing'"),
            _ => throw Refused(),
        };
        BeginCommitBudgetRead();
        await using var row = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await row.ReadAsync(cancellationToken).ConfigureAwait(false)) throw Refused();
        RecordCommitBudget(row);
    }
}
