using LearnStack.Application.Jobs;
using LearnStack.SharedKernel.Jobs;

namespace LearnStack.Infrastructure.Jobs;

internal enum BackgroundJobRecurrenceRegistrationResult { Registered, Existing, Obsolete, Conflict, Stopped }
internal sealed record BackgroundJobMaterializedOccurrence(BackgroundJobOccurrenceId Id, string Key,
    long Revision, DateTimeOffset ScheduledAt, long SkippedSlots);

internal sealed partial class PostgresBackgroundJobControlStore
{
    /// <summary>Register a frozen manifest without overwriting a newer deployment's revision or native tick.</summary>
    internal async Task<OwnedBackgroundJobControl<BackgroundJobRecurrenceRegistrationResult>> RegisterRecurrenceAsync(
        BackgroundJobRecurrenceDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        ArgumentNullException.ThrowIfNull(descriptor);
        var resources = new BackgroundJobControlResources();
        var value = BackgroundJobRecurrenceRegistrationResult.Stopped;
        using var deadline = Deadline(cancellationToken);
        try
        {
            if (await OpenAsync(resources, deadline.Token).ConfigureAwait(false))
            {
                var facts = descriptor.Facts;
                var command = Command(resources, "SELECT pg_advisory_xact_lock(hashtextextended(@key,0))");
                command.Parameters.AddWithValue("key", facts.Key);
                await command.ExecuteNonQueryAsync(deadline.Token).ConfigureAwait(false);
                await FinishAsync(resources).ConfigureAwait(false);
                command = Command(resources, """
                    SELECT revision,manifest_hash,activates_at FROM background_job_recurrences
                    WHERE recurrence_key=@key ORDER BY revision DESC LIMIT 1 FOR UPDATE
                    """);
                command.Parameters.AddWithValue("key", facts.Key);
                resources.SetReader(await command.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false));
                var insert = true;
                if (await resources.Reader!.ReadAsync(deadline.Token).ConfigureAwait(false))
                {
                    var previousRevision = resources.Reader.GetInt64(0);
                    if (facts.Revision <= previousRevision)
                    {
                        insert = false;
                        value = facts.Revision < previousRevision ? BackgroundJobRecurrenceRegistrationResult.Obsolete
                            : descriptor.ManifestHash == resources.Reader.GetString(1)
                                ? BackgroundJobRecurrenceRegistrationResult.Existing : BackgroundJobRecurrenceRegistrationResult.Conflict;
                    }
                    else if (facts.ActivatesAt < resources.Reader.GetFieldValue<DateTimeOffset>(2))
                    {
                        insert = false;
                        value = BackgroundJobRecurrenceRegistrationResult.Conflict;
                    }
                }
                await FinishAsync(resources).ConfigureAwait(false);
                if (insert)
                {
                    command = Command(resources, """
                        INSERT INTO background_job_recurrences (recurrence_key,revision,manifest_hash,target_definition_key,
                            schedule,activates_at,anchor_at,period_microseconds,jitter_microseconds,recipient_policy,
                            misfire_policy,next_scheduled_at)
                        VALUES (@key,@revision,@hash,@target,@schedule,@activation,@anchor,@period,@jitter,@policy,
                            'coalesce_latest',date_bin(@interval,@activation,@anchor)
                                + CASE WHEN date_bin(@interval,@activation,@anchor)<@activation
                                    THEN @interval ELSE interval '0 seconds' END)
                        """);
                    command.Parameters.AddWithValue("key", facts.Key);
                    command.Parameters.AddWithValue("revision", facts.Revision);
                    command.Parameters.AddWithValue("hash", descriptor.ManifestHash);
                    command.Parameters.AddWithValue("target", facts.Target);
                    command.Parameters.AddWithValue("schedule", facts.StoredSchedule);
                    command.Parameters.AddWithValue("activation", facts.ActivatesAt);
                    command.Parameters.AddWithValue("anchor", facts.Anchor);
                    command.Parameters.AddWithValue("period", facts.Period.Ticks / TimeSpan.TicksPerMicrosecond);
                    command.Parameters.AddWithValue("jitter", facts.Jitter.Ticks / TimeSpan.TicksPerMicrosecond);
                    command.Parameters.AddWithValue("policy", facts.StoredRecipientPolicy);
                    command.Parameters.AddWithValue("interval", facts.Period);
                    await command.ExecuteNonQueryAsync(deadline.Token).ConfigureAwait(false);
                    await FinishAsync(resources).ConfigureAwait(false);
                    command = Command(resources, """
                        UPDATE background_job_recurrences SET available_after=GREATEST(clock_timestamp(),next_scheduled_at)
                        WHERE recurrence_key=@key AND revision=@revision
                        """);
                    command.Parameters.AddWithValue("key", facts.Key);
                    command.Parameters.AddWithValue("revision", facts.Revision);
                    await command.ExecuteNonQueryAsync(deadline.Token).ConfigureAwait(false);
                    await FinishAsync(resources).ConfigureAwait(false);
                    value = BackgroundJobRecurrenceRegistrationResult.Registered;
                }
                await CommitAsync(resources, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure)) { resources.RecordFailure(failure); }
        finally { await resources.CleanupAsync().ConfigureAwait(false); }
        return new(value, resources, []);
    }

    /// <summary>One short locked quantum atomically coalesces the latest due slot and advances its manifest.</summary>
    internal async Task<OwnedBackgroundJobControl<BackgroundJobMaterializedOccurrence>> MaterializeRecurrenceAsync(
        BackgroundJobRecurrenceRegistry recurrences, CancellationToken cancellationToken = default)
    {
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        ArgumentNullException.ThrowIfNull(recurrences);
        var resources = new BackgroundJobControlResources();
        BackgroundJobMaterializedOccurrence? value = null;
        using var deadline = Deadline(cancellationToken);
        try
        {
            if (await OpenAsync(resources, deadline.Token).ConfigureAwait(false))
            {
                var command = Command(resources, """
                    SELECT recurrence_key,revision,manifest_hash,target_definition_key,schedule,activates_at,anchor_at,
                        period_microseconds,jitter_microseconds,recipient_policy,next_scheduled_at,
                        date_bin(period_microseconds*interval '1 microsecond',
                            LEAST(statement_timestamp(),COALESCE(superseded_at-interval '1 microsecond',statement_timestamp())),
                            anchor_at) AS slot
                    FROM background_job_recurrences
                    WHERE quarantined_at IS NULL AND (superseded_at IS NULL OR next_scheduled_at<superseded_at)
                        AND available_after<=statement_timestamp() AND next_scheduled_at<=statement_timestamp()
                        AND (ownership_token IS NULL OR ownership_until<=statement_timestamp())
                    ORDER BY available_after,recurrence_key,revision LIMIT 1 FOR UPDATE SKIP LOCKED
                    """);
                resources.SetReader(await command.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false));
                StoredRecurrence? stored = null;
                if (await resources.Reader!.ReadAsync(deadline.Token).ConfigureAwait(false)) stored = StoredRecurrence.Read(resources.Reader);
                await FinishAsync(resources).ConfigureAwait(false);
                if (stored is not null)
                {
                    var compatible = recurrences.TryGet(stored.Facts.Key, out var local)
                        && local.Registration.Revision >= stored.Facts.Revision
                        && (local.Registration.Revision != stored.Facts.Revision || local.ManifestHash == stored.Hash)
                        && definitions.TryGet(stored.Facts.Target, out var target)
                        && target.Readiness == BackgroundJobDefinitionReadiness.Active
                        && target.ParameterType == typeof(BackgroundJobOccurrenceParameters)
                        && target.Scope == stored.Facts.Scope
                        && target.OwnerModule == stored.Facts.Key.Split('.')[0];
                    var invalid = stored.Facts.Fingerprint() != stored.Hash || stored.Slot < stored.Next;
                    if (invalid || !compatible)
                    {
                        command = Command(resources, """
                            UPDATE background_job_recurrences SET available_after=clock_timestamp()+@backoff,
                                last_error=@error,quarantined_at=CASE WHEN @invalid THEN clock_timestamp() ELSE quarantined_at END
                            WHERE recurrence_key=@key AND revision=@revision
                            """);
                        command.Parameters.AddWithValue("backoff", options.HandoffRepairGrace);
                        command.Parameters.AddWithValue("error", invalid ? "invalid_control" : "deployment_mismatch");
                        command.Parameters.AddWithValue("invalid", invalid);
                    }
                    else
                    {
                        var occurrence = BackgroundJobOccurrenceId.From(ids.NewUuidV7());
                        var skipped = (stored.Slot - stored.Next).Ticks / stored.Facts.Period.Ticks;
                        command = Command(resources, """
                            INSERT INTO background_job_occurrences (id,recurrence_key,revision,manifest_hash,
                                target_definition_key,recipient_policy,scheduled_at,jitter_microseconds,skipped_slots)
                            VALUES (@id,@key,@revision,@hash,@target,@policy,@slot,@jitter,@skipped)
                            ON CONFLICT (recurrence_key,scheduled_at) DO NOTHING
                            """);
                        command.Parameters.AddWithValue("id", occurrence.Value);
                        command.Parameters.AddWithValue("key", stored.Facts.Key);
                        command.Parameters.AddWithValue("revision", stored.Facts.Revision);
                        command.Parameters.AddWithValue("hash", stored.Hash);
                        command.Parameters.AddWithValue("target", stored.Facts.Target);
                        command.Parameters.AddWithValue("policy", stored.Facts.StoredRecipientPolicy);
                        command.Parameters.AddWithValue("slot", stored.Slot);
                        command.Parameters.AddWithValue("jitter", stored.Facts.Jitter.Ticks / TimeSpan.TicksPerMicrosecond);
                        command.Parameters.AddWithValue("skipped", skipped);
                        var inserted = await command.ExecuteNonQueryAsync(deadline.Token).ConfigureAwait(false) == 1;
                        await FinishAsync(resources).ConfigureAwait(false);
                        if (inserted) value = new(occurrence, stored.Facts.Key, stored.Facts.Revision, stored.Slot, skipped);
                        command = Command(resources, """
                            UPDATE background_job_recurrences SET next_scheduled_at=@next,available_after=GREATEST(clock_timestamp(),@next),
                                ownership_token=NULL,ownership_until=NULL,last_error=NULL
                            WHERE recurrence_key=@key AND revision=@revision
                            """);
                        command.Parameters.AddWithValue("next", stored.Slot + stored.Facts.Period);
                    }
                    command.Parameters.AddWithValue("key", stored.Facts.Key);
                    command.Parameters.AddWithValue("revision", stored.Facts.Revision);
                    await command.ExecuteNonQueryAsync(deadline.Token).ConfigureAwait(false);
                    await FinishAsync(resources).ConfigureAwait(false);
                    await CommitAsync(resources, deadline.Token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure)) { resources.RecordFailure(failure); }
        finally { await resources.CleanupAsync().ConfigureAwait(false); }
        return new(value, resources, []);
    }

    private sealed record StoredRecurrence(BackgroundJobRecurrenceFacts Facts, string Hash, DateTimeOffset Next, DateTimeOffset Slot)
    {
        internal static StoredRecurrence Read(Npgsql.NpgsqlDataReader row) =>
            new(BackgroundJobRecurrenceFacts.Read(row), row.GetString(2), row.GetFieldValue<DateTimeOffset>(10), row.GetFieldValue<DateTimeOffset>(11));
    }
}
