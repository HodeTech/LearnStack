using LearnStack.Application.Jobs;

namespace LearnStack.Infrastructure.Jobs;

internal sealed record BackgroundJobQuarantineObservation(bool HasQuarantinedJobs, bool HasQuarantinedControl);

internal sealed partial class PostgresBackgroundJobControlStore
{
    internal const string QuarantinePresenceSql = """
        SELECT EXISTS (SELECT 1 FROM background_jobs WHERE phase='quarantined') AS has_quarantined_jobs
            , (EXISTS (SELECT 1 FROM background_job_recurrences WHERE quarantined_at IS NOT NULL)
               OR EXISTS (SELECT 1 FROM background_job_occurrences WHERE quarantined_at IS NOT NULL)
               OR EXISTS (SELECT 1 FROM background_job_recipients WHERE quarantined_at IS NOT NULL)) AS has_quarantined_control
        """;

    internal async Task<OwnedBackgroundJobControl<BackgroundJobQuarantineObservation>> ReadQuarantinePresenceAsync(
        CancellationToken cancellationToken = default)
    {
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        var resources = new BackgroundJobControlResources { IsProbe = true };
        BackgroundJobQuarantineObservation? value = null;
        using var deadline = Deadline(cancellationToken);
        try
        {
            if (await OpenAsync(resources, deadline.Token).ConfigureAwait(false))
            {
                var command = Command(resources, QuarantinePresenceSql);
                resources.SetReader(await command.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false));
                if (!await resources.Reader!.ReadAsync(deadline.Token).ConfigureAwait(false))
                    throw new InvalidOperationException("The quarantine observation returned no row.");
                var observed = new BackgroundJobQuarantineObservation(resources.Reader.GetBoolean(0), resources.Reader.GetBoolean(1));
                await FinishAsync(resources).ConfigureAwait(false);
                await resources.Transaction!.RollbackAsync(deadline.Token).ConfigureAwait(false);
                resources.ProbeRollbackAcknowledged = true;
                value = observed;
            }
        }
        catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure)) { resources.RecordFailure(failure); }
        finally { await resources.CleanupAsync().ConfigureAwait(false); }
        if (!resources.ProbeRollbackAcknowledged || !resources.CleanupConfirmed || resources.Failure is not null) value = null;
        return new(value, resources, Array.Empty<BackgroundJobCommitProbe>());
    }
}
