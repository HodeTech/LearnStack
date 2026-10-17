using System.Data.Common;
using System.Diagnostics;
using LearnStack.Application.Jobs;
using LearnStack.SharedKernel.Jobs;
using NpgsqlTypes;

namespace LearnStack.Infrastructure.Jobs;

internal sealed partial class PostgresBackgroundJobControlStore
{
    private const string RecipientClaimSelectionSql = """
        SELECT m.recurrence_key,m.revision,m.manifest_hash,m.target_definition_key,m.schedule,m.activates_at,
            m.anchor_at,m.period_microseconds,m.jitter_microseconds,m.recipient_policy,
            r.id,r.occurrence_id,r.tenant_id,r.organization_id,r.recurrence_key,r.revision,r.manifest_hash,
            r.target_definition_key,r.recipient_policy,r.scheduled_at,r.due_at
        FROM background_job_recipients r JOIN background_job_recurrences m
            ON m.recurrence_key=r.recurrence_key AND m.revision=r.revision
        WHERE r.accepted_job_id IS NULL AND r.skipped_at IS NULL AND r.quarantined_at IS NULL
            AND r.id<>ALL(@retained)
            AND r.available_after<=statement_timestamp() AND r.due_at<=statement_timestamp()
            AND (r.ownership_token IS NULL OR r.ownership_until<=statement_timestamp())
        ORDER BY r.available_after,r.id LIMIT 1 FOR UPDATE OF r SKIP LOCKED
        """;

    /// <summary>Claims one due immutable route, publishing authority only after real ACK and cleanup.</summary>
    internal async Task<OwnedBackgroundJobControl<BackgroundJobRecipientClaim>> TryClaimRecipientAsync(
        BackgroundJobRecurrenceRegistry recurrences, IReadOnlyCollection<Guid>? retainedRecipientIds = null,
        CancellationToken cancellationToken = default)
    {
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        ArgumentNullException.ThrowIfNull(recurrences);
        var retained = retainedRecipientIds?.ToArray() ?? [];
        if (retained.Length > BackgroundJobRecurrenceControl.ApplicationOwnerLimit || retained.Any(id => id == Guid.Empty)
            || retained.Distinct().Count() != retained.Length)
            throw new ArgumentException("Recipient exclusions require the bounded actual owner identities.", nameof(retainedRecipientIds));
        options.Validate();
        var resources = new BackgroundJobControlResources();
        BackgroundJobRecipientClaim? candidate = null;
        using var deadline = Deadline(cancellationToken);
        try
        {
            if (await OpenAsync(resources, deadline.Token).ConfigureAwait(false))
            {
                var command = Command(resources, RecipientClaimSelectionSql);
                command.Parameters.AddWithValue("retained", NpgsqlDbType.Array | NpgsqlDbType.Uuid, retained);
                resources.SetReader(await command.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false));
                RecipientRoute? route = null;
                if (await resources.Reader!.ReadAsync(deadline.Token).ConfigureAwait(false)) route = RecipientRoute.Read(resources.Reader);
                await FinishAsync(resources).ConfigureAwait(false);
                if (route is not null)
                {
                    var compatible = recurrences.TryGet(route.Manifest.Key, out var local)
                        && local.Registration.Revision >= route.Manifest.Revision
                        && (local.Registration.Revision != route.Manifest.Revision || local.ManifestHash == route.Hash)
                        && definitions.TryGet(route.Manifest.Target, out var target)
                        && target.Readiness == BackgroundJobDefinitionReadiness.Active
                        && target.ParameterType == typeof(BackgroundJobOccurrenceParameters)
                        && target.Scope == route.Manifest.Scope
                        && target.OwnerModule == route.Manifest.Key.Split('.')[0];
                    if (route.Invalid || !compatible)
                    {
                        command = Command(resources, """
                            UPDATE background_job_recipients SET available_after=clock_timestamp()+@backoff,
                                ownership_token=NULL,ownership_until=NULL,last_error=@error,
                                quarantined_at=CASE WHEN @invalid THEN clock_timestamp() ELSE quarantined_at END
                            WHERE id=@id AND accepted_job_id IS NULL AND skipped_at IS NULL AND quarantined_at IS NULL
                            """);
                        command.Parameters.AddWithValue("id", route.Id);
                        command.Parameters.AddWithValue("backoff", options.HandoffRepairGrace);
                        command.Parameters.AddWithValue("error", route.Invalid ? "invalid_control" : "deployment_mismatch");
                        command.Parameters.AddWithValue("invalid", route.Invalid);
                        if (await command.ExecuteNonQueryAsync(deadline.Token).ConfigureAwait(false) != 1)
                            throw new InvalidOperationException("Recipient deferral lost its locked row.");
                        await FinishAsync(resources).ConfigureAwait(false);
                    }
                    else
                    {
                        command = Command(resources, """
                            UPDATE background_job_recipients SET ownership_token=@token,
                                ownership_until=clock_timestamp()+@lease,last_error=NULL
                            WHERE id=@id AND accepted_job_id IS NULL AND skipped_at IS NULL AND quarantined_at IS NULL
                                AND due_at<=clock_timestamp()
                                AND (ownership_token IS NULL OR ownership_until<=clock_timestamp())
                            RETURNING id,occurrence_id,tenant_id,organization_id,recurrence_key,revision,manifest_hash,
                                target_definition_key,recipient_policy,scheduled_at,due_at,ownership_token,ownership_until,
                                clock_timestamp()
                            """);
                        command.Parameters.AddWithValue("id", route.Id);
                        command.Parameters.AddWithValue("token", ids.NewUuidV4());
                        command.Parameters.AddWithValue("lease", options.LeaseDuration);
                        // Observe before the wire call. Time spent in SQL, ACK and
                        // cleanup cannot lend the claim an extra local lease window.
                        var observedAt = Stopwatch.GetTimestamp();
                        resources.SetReader(await command.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false));
                        if (await resources.Reader!.ReadAsync(deadline.Token).ConfigureAwait(false))
                            candidate = RecipientClaims.FromRow(resources.Reader, observedAt, local!.Registration.DeclaringType);
                        await FinishAsync(resources).ConfigureAwait(false);
                        if (candidate is null) throw new InvalidOperationException("Recipient acquisition lost its locked due row.");
                    }
                    await CommitAsync(resources, deadline.Token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure)) { resources.RecordFailure(failure); }
        finally { await resources.CleanupAsync().ConfigureAwait(false); }
        // A candidate observed before COMMIT is not permission to execute. An
        // uncertain ACK or retained cleanup never publishes an executable claim.
        var published = resources.CommitAcknowledged && resources.CleanupConfirmed && resources.Failure is null
            && candidate?.HasLiveLease == true ? candidate : null;
        return new(published, resources, []);
    }

    private sealed class RecipientClaims : BackgroundJobRecipientClaim.MintingSource
    {
        internal static BackgroundJobRecipientClaim FromRow(DbDataReader row, long observedAt, Type declarationType) =>
            MintRecipient(row, observedAt, declarationType);
    }

    private sealed record RecipientRoute(Guid Id, BackgroundJobRecurrenceFacts Manifest, string Hash, bool Invalid)
    {
        internal static RecipientRoute Read(Npgsql.NpgsqlDataReader row)
        {
            var manifest = BackgroundJobRecurrenceFacts.Read(row);
            var hash = row.GetString(2);
            var invalid = manifest.Fingerprint() != hash || row.GetString(14) != manifest.Key
                || row.GetInt64(15) != manifest.Revision || row.GetString(16) != hash
                || row.GetString(17) != manifest.Target || row.GetString(18) != manifest.StoredRecipientPolicy;
            return new(row.GetGuid(10), manifest, hash, invalid);
        }
    }
}
