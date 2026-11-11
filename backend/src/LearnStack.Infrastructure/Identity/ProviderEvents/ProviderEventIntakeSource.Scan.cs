using LearnStack.SharedKernel.Identity;
using System.Collections.Immutable;
using Npgsql;
using NpgsqlTypes;

namespace LearnStack.Infrastructure.Identity.ProviderEvents;

public sealed partial class ProviderEventIntakeSource
{
    public Task<ProviderEventScanState> ReadScanAsync(CancellationToken token = default) =>
        ExecuteAsync(async (connection, transaction, deadline) =>
        {
            await using var command = new NpgsqlCommand(ScanColumns + " FROM public.identity_provider_event_scan WHERE id=1", connection, transaction);
            return await ReadScanRowAsync(command, deadline).ConfigureAwait(false);
        }, token);

    public Task<ProviderEventScanState> BeginSweepAsync(CancellationToken token = default) =>
        ExecuteAsync(async (connection, transaction, deadline) =>
        {
            await using var command = new NpgsqlCommand("""
                WITH timing AS (SELECT date_trunc('milliseconds',clock_timestamp()) AS at), bounds AS (
                    SELECT t.at,GREATEST(s.activation_lower,t.at-make_interval(secs=>s.retention_seconds)) AS low
                    FROM public.identity_provider_event_scan s CROSS JOIN timing t WHERE s.id=1), continuity AS (
                    SELECT b.*,LEAST(s.recovery_anchor_at,COALESCE(s.cursor_to,s.recovery_anchor_at)) AS frontier
                    FROM bounds b CROSS JOIN public.identity_provider_event_scan s WHERE s.id=1)
                UPDATE public.identity_provider_event_scan s SET
                    historical_gap=s.historical_gap OR b.frontier<b.at-make_interval(secs=>s.retention_seconds),
                    earliest_gap_at=CASE WHEN b.frontier<b.at-make_interval(secs=>s.retention_seconds)
                        THEN LEAST(s.earliest_gap_at,b.frontier) ELSE s.earliest_gap_at END,
                    cursor_from=CASE WHEN s.cursor_from IS NULL OR s.cursor_to<b.at-make_interval(secs=>s.retention_seconds)
                        THEN date_trunc('milliseconds',b.low)+CASE WHEN date_trunc('milliseconds',b.low)<b.low THEN interval '1 millisecond' ELSE interval '0' END
                        ELSE s.cursor_from END,
                    cursor_to=CASE WHEN s.cursor_to IS NULL OR s.cursor_to<b.at-make_interval(secs=>s.retention_seconds) THEN b.at ELSE s.cursor_to END,
                    cursor_offset=CASE WHEN s.cursor_from IS NULL OR s.cursor_to<b.at-make_interval(secs=>s.retention_seconds) THEN 0 ELSE s.cursor_offset END,
                    last_attempted_at=b.at FROM continuity b WHERE s.id=1;
                """ + ScanColumns + " FROM public.identity_provider_event_scan WHERE id=1", connection, transaction);
            return await ReadScanRowAsync(command, deadline).ConfigureAwait(false);
        }, token);

    public Task<bool> AdvanceSweepAsync(IdentityEventRead page, int returned, bool finished, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (returned < 0 || returned > page.Maximum || finished != (returned < page.Maximum))
            throw new ArgumentException("Provider sweep progress is invalid.");
        return ExecuteAsync(async (connection, transaction, deadline) =>
        {
            await using var command = new NpgsqlCommand("""
                UPDATE public.identity_provider_event_scan s SET
                    cursor_offset=CASE WHEN @finished THEN 0 ELSE cursor_offset+@count END,
                    cursor_from=CASE WHEN @finished THEN NULL ELSE cursor_from END,
                    cursor_to=CASE WHEN @finished THEN NULL ELSE cursor_to END,
                    last_traversed_at=CASE WHEN @finished THEN clock_timestamp() ELSE last_traversed_at END,
                    recovery_anchor_at=CASE WHEN @finished THEN GREATEST(recovery_anchor_at,@to) ELSE recovery_anchor_at END,
                    last_completed_at=CASE WHEN @finished AND NOT historical_gap AND NOT EXISTS (
                        SELECT FROM public.identity_provider_event_problems p WHERE p.resolved_at IS NULL
                            AND p.range_from<=@to AND p.range_to>=@from)
                        THEN clock_timestamp() ELSE last_completed_at END
                WHERE id=1 AND cursor_from=@from AND cursor_to=@to AND cursor_offset=@offset
                """, connection, transaction);
            command.Parameters.AddWithValue("finished", finished);
            command.Parameters.AddWithValue("count", returned);
            BindRange(command, page);
            return await command.ExecuteNonQueryAsync(deadline).ConfigureAwait(false) == 1;
        }, token);
    }

    public Task RecordPageProblemAsync(IdentityEventRead singleton, IdentityEventPageIssue issue, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(singleton);
        if (singleton.Maximum != 1 || !Enum.IsDefined(issue)) throw new ArgumentException("Provider problem locator is invalid.");
        var reason = issue == IdentityEventPageIssue.Oversized ? "oversized" : "malformed";
        return ExecuteAsync(async (connection, transaction, deadline) =>
        {
            var key = "page:" + _profile!.AuthorityId.Value.ToString("D") + ":" + _profile.RealmId + ":"
                + singleton.From.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture) + ":"
                + singleton.To.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture) + ":"
                + singleton.Offset.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await using var command = new NpgsqlCommand("""
                INSERT INTO public.identity_provider_event_problems(problem_key,authority_id,realm_id,range_from,range_to,source_offset,reason,next_retry_at)
                VALUES(@key,@authority,@realm,@from,@to,@offset,@reason,clock_timestamp()+interval '5 minutes')
                ON CONFLICT(problem_key) DO UPDATE SET last_seen_at=clock_timestamp(),next_retry_at=clock_timestamp()+interval '5 minutes',
                    attempts=identity_provider_event_problems.attempts+1
                """, connection, transaction);
            BindProfile(command, _profile!); BindRange(command, singleton);
            command.Parameters.AddWithValue("key", key);
            command.Parameters.AddWithValue("reason", reason);
            await command.ExecuteNonQueryAsync(deadline).ConfigureAwait(false);
            return true;
        }, token);
    }

    public Task<ProviderEventProblem?> TakeProblemAsync(CancellationToken token = default) =>
        ExecuteAsync(async (connection, transaction, deadline) =>
        {
            await using var command = new NpgsqlCommand("""
                WITH candidate AS (SELECT problem_key FROM public.identity_provider_event_problems
                    WHERE resolved_at IS NULL AND NOT irrecoverable AND next_retry_at<=clock_timestamp()
                        AND range_to>=clock_timestamp()-make_interval(secs=>(SELECT retention_seconds FROM public.identity_provider_event_scan WHERE id=1))
                        AND reason IN ('fanout_exceeded','malformed','oversized')
                    ORDER BY next_retry_at,first_seen_at,problem_key FOR UPDATE SKIP LOCKED LIMIT 1)
                UPDATE public.identity_provider_event_problems p SET next_retry_at=clock_timestamp()+interval '5 minutes',attempts=attempts+1
                FROM candidate c WHERE p.problem_key=c.problem_key
                RETURNING p.problem_key,p.reason,p.range_from,p.range_to,p.source_offset,p.provider_event_id
                """, connection, transaction);
            await using var row = await command.ExecuteReaderAsync(deadline).ConfigureAwait(false);
            return !await row.ReadAsync(deadline).ConfigureAwait(false) ? null : new ProviderEventProblem(row.GetString(0), row.GetString(1),
                row.GetFieldValue<DateTimeOffset>(2), row.GetFieldValue<DateTimeOffset>(3), row.IsDBNull(4) ? null : row.GetInt32(4), row.IsDBNull(5) ? null : row.GetGuid(5));
        }, token);

    public Task<ProviderPendingFact?> ReadPendingFactAsync(Guid providerEventId, CancellationToken token = default) =>
        ExecuteAsync(async (connection, transaction, deadline) =>
        {
            await using var command = new NpgsqlCommand("""
                SELECT provider_event_id,source_occurred_at,source_kind,subject_key FROM public.identity_provider_event_receipts
                WHERE authority_id=@authority AND realm_id=@realm AND provider_event_id=@event
                    AND accepted_at IS NULL AND disposition='fanout_exceeded'
                """, connection, transaction);
            BindProfile(command, _profile!); command.Parameters.AddWithValue("event", providerEventId);
            await using var row = await command.ExecuteReaderAsync(deadline).ConfigureAwait(false);
            return !await row.ReadAsync(deadline).ConfigureAwait(false) ? null : new ProviderPendingFact(row.GetGuid(0),
                row.GetFieldValue<DateTimeOffset>(1), row.GetString(2), row.IsDBNull(3) ? null : IdentitySubjectKey.FromBytes(row.GetFieldValue<byte[]>(3)));
        }, token);

    public Task ResolvePageProblemAsync(ProviderEventProblem problem, CancellationToken token = default) =>
        ExecuteAsync(async (connection, transaction, deadline) =>
        {
            await using var command = new NpgsqlCommand("""
                UPDATE public.identity_provider_event_problems SET resolved_at=clock_timestamp()
                WHERE problem_key=@key AND authority_id=@authority AND realm_id=@realm AND reason IN ('malformed','oversized')
                    AND NOT irrecoverable AND range_to>=clock_timestamp()-make_interval(secs=>(SELECT retention_seconds FROM public.identity_provider_event_scan WHERE id=1))
                """, connection, transaction);
            BindProfile(command, _profile!); command.Parameters.AddWithValue("key", problem.Key);
            await command.ExecuteNonQueryAsync(deadline).ConfigureAwait(false);
            return true;
        }, token);

    public Task UpdateSourcePolicyAsync(int retentionSeconds, CancellationToken token = default)
    {
        if (retentionSeconds is < 604800 or > 2592000) throw new ArgumentOutOfRangeException(nameof(retentionSeconds));
        return ExecuteAsync(async (connection, transaction, deadline) =>
        {
            await using var command = new NpgsqlCommand(UpdatePolicySql, connection, transaction);
            command.Parameters.AddWithValue("retention", retentionSeconds);
            await command.ExecuteNonQueryAsync(deadline).ConfigureAwait(false);
            return true;
        }, token);
    }

    public Task<ProviderEventHealthState> ReadHealthAsync(CancellationToken token = default) =>
        ExecuteAsync(async (connection, transaction, deadline) =>
        {
            await using var command = new NpgsqlCommand("""
                SELECT s.historical_gap OR COALESCE(s.last_completed_at,s.activation_lower+make_interval(secs=>s.retention_seconds))
                        <=clock_timestamp()-make_interval(secs=>s.retention_seconds-86400)
                    OR EXISTS(SELECT FROM public.identity_provider_event_problems p
                        WHERE p.resolved_at IS NULL AND (p.irrecoverable OR p.range_from<=clock_timestamp()-make_interval(secs=>s.retention_seconds-86400))),
                    s.last_completed_at IS NULL OR s.last_completed_at<clock_timestamp()-interval '2 hours',
                    s.last_attempted_at,s.last_completed_at
                FROM public.identity_provider_event_scan s WHERE s.id=1
                """, connection, transaction);
            bool critical, degraded;
            DateTimeOffset? attempted, completed;
            await using (var row = await command.ExecuteReaderAsync(deadline).ConfigureAwait(false))
            {
                if (!await row.ReadAsync(deadline).ConfigureAwait(false)) throw new InvalidOperationException("Provider source health is unavailable.");
                critical = row.GetBoolean(0); degraded = row.GetBoolean(1);
                attempted = row.IsDBNull(2) ? null : row.GetFieldValue<DateTimeOffset>(2);
                completed = row.IsDBNull(3) ? null : row.GetFieldValue<DateTimeOffset>(3);
            }
            await using var problems = new NpgsqlCommand("""
                SELECT reason,count(*),GREATEST(0,EXTRACT(epoch FROM clock_timestamp()-min(range_from)))::double precision
                FROM public.identity_provider_event_problems WHERE resolved_at IS NULL GROUP BY reason ORDER BY reason
                """, connection, transaction);
            var counts = ImmutableArray.CreateBuilder<ProviderEventProblemHealth>();
            await using (var row = await problems.ExecuteReaderAsync(deadline).ConfigureAwait(false))
                while (await row.ReadAsync(deadline).ConfigureAwait(false))
                {
                    var reason = row.GetString(0);
                    if (counts.Count >= 5 || reason is not ("fanout_exceeded" or "source_collision" or "malformed" or "oversized" or "out_of_window"))
                        throw new InvalidOperationException("Provider source health category is invalid.");
                    counts.Add(new(reason, row.GetInt64(1), row.GetDouble(2)));
                }
            return new ProviderEventHealthState(critical, degraded, attempted, completed, counts.ToImmutable());
        }, token);

    // Detect expiry under the previously qualified retention before replacing
    // it. Increasing retention cannot resurrect source history already lost.
    private const string UpdatePolicySql = """
        WITH timing AS (SELECT clock_timestamp() AS at)
        UPDATE public.identity_provider_event_scan SET
            historical_gap=historical_gap OR LEAST(recovery_anchor_at,COALESCE(cursor_to,recovery_anchor_at))<t.at-make_interval(secs=>retention_seconds),
            earliest_gap_at=CASE WHEN LEAST(recovery_anchor_at,COALESCE(cursor_to,recovery_anchor_at))<t.at-make_interval(secs=>retention_seconds)
                THEN LEAST(earliest_gap_at,recovery_anchor_at,COALESCE(cursor_to,recovery_anchor_at)) ELSE earliest_gap_at END,
            retention_seconds=@retention FROM timing t WHERE id=1
        """;

    private const string ScanColumns = "SELECT activation_lower,retention_seconds,cursor_from,cursor_to,cursor_offset,last_traversed_at,last_completed_at,historical_gap,earliest_gap_at";

    private static async Task<ProviderEventScanState> ReadScanRowAsync(NpgsqlCommand command, CancellationToken token)
    {
        await using var row = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await row.ReadAsync(token).ConfigureAwait(false)) throw new InvalidOperationException("Provider source ledger is unavailable.");
        return new(row.GetFieldValue<DateTimeOffset>(0), row.GetInt32(1), Time(2), Time(3), row.GetInt32(4), Time(5), Time(6), row.GetBoolean(7), Time(8));
        DateTimeOffset? Time(int index) => row.IsDBNull(index) ? null : row.GetFieldValue<DateTimeOffset>(index);
    }

    private static void BindRange(NpgsqlCommand command, IdentityEventRead page)
    {
        command.Parameters.AddWithValue("from", NpgsqlDbType.TimestampTz, page.From);
        command.Parameters.AddWithValue("to", NpgsqlDbType.TimestampTz, page.To);
        command.Parameters.AddWithValue("offset", page.Offset);
    }
}

public sealed record ProviderEventScanState(DateTimeOffset ActivationLower, int RetentionSeconds, DateTimeOffset? From,
    DateTimeOffset? To, int Offset, DateTimeOffset? LastTraversedAt, DateTimeOffset? LastCompletedAt, bool HistoricalGap, DateTimeOffset? EarliestGapAt)
{
    public override string ToString() => nameof(ProviderEventScanState);
}
public sealed record ProviderEventProblem(string Key, string Reason, DateTimeOffset From, DateTimeOffset To, int? Offset, Guid? EventId)
{
    public override string ToString() => nameof(ProviderEventProblem);
}
public sealed record ProviderPendingFact(Guid EventId, DateTimeOffset OccurredAt, string Kind, IdentitySubjectKey? SubjectKey)
{
    public override string ToString() => nameof(ProviderPendingFact);
}
public sealed record ProviderEventProblemHealth(string Reason, long Count, double OldestAgeSeconds);
public sealed record ProviderEventHealthState(bool Critical, bool Degraded, DateTimeOffset? LastAttemptedAt,
    DateTimeOffset? LastCompletedAt, ImmutableArray<ProviderEventProblemHealth> Problems);
