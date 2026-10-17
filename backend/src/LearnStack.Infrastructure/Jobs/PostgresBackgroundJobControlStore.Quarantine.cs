using LearnStack.Application.Jobs;

namespace LearnStack.Infrastructure.Jobs;

internal sealed record BackgroundJobQuarantineObservation(bool HasQuarantinedJobs);

internal sealed partial class PostgresBackgroundJobControlStore
{
    internal const string QuarantinePresenceSql = """
        SELECT EXISTS (SELECT 1 FROM background_jobs WHERE phase='quarantined') AS has_quarantined_jobs
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
                var observed = (bool)(await command.ExecuteScalarAsync(deadline.Token).ConfigureAwait(false))!;
                await resources.FinishCommandAsync().ConfigureAwait(false);
                await resources.Transaction!.RollbackAsync(deadline.Token).ConfigureAwait(false);
                resources.ProbeRollbackAcknowledged = true;
                value = new(observed);
            }
        }
        catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure)) { resources.RecordFailure(failure); }
        finally { await resources.CleanupAsync().ConfigureAwait(false); }
        if (!resources.ProbeRollbackAcknowledged || !resources.CleanupConfirmed || resources.Failure is not null) value = null;
        return new(value, resources, Array.Empty<BackgroundJobCommitProbe>());
    }
}
