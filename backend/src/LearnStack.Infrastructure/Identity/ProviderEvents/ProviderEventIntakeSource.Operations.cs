using LearnStack.SharedKernel.Identity;
using Npgsql;
using NpgsqlTypes;

namespace LearnStack.Infrastructure.Identity.ProviderEvents;

public sealed partial class ProviderEventIntakeSource
{
    public Task<ProviderEventAcceptance> AcceptAsync(Guid providerEventId, DateTimeOffset occurredAt,
        string kind, IdentitySubjectKey? subjectKey, CancellationToken token = default) =>
        ExecuteAsync(async (connection, transaction, deadline) =>
        {
            ProviderEventAcceptance result;
            await using (var command = new NpgsqlCommand("""
                SELECT receipt_id,disposition,accepted,target_count FROM
                    public.fn_identity_provider_event_accept(@authority,@realm,@event,@at,@kind,@subject)
                """, connection, transaction))
            {
                command.Parameters.AddWithValue("authority", _profile!.AuthorityId.Value);
                command.Parameters.AddWithValue("realm", _profile.RealmId);
                command.Parameters.AddWithValue("event", providerEventId);
                command.Parameters.AddWithValue("at", occurredAt);
                command.Parameters.AddWithValue("kind", kind);
                command.Parameters.Add(new NpgsqlParameter("subject", NpgsqlDbType.Bytea)
                { Value = (object?)subjectKey?.ToByteArray() ?? DBNull.Value });
                await using var reader = await command.ExecuteReaderAsync(deadline).ConfigureAwait(false);
                if (!await reader.ReadAsync(deadline).ConfigureAwait(false))
                    throw new InvalidOperationException("Provider intake returned no durable disposition.");
                result = new(reader.GetGuid(0), reader.GetString(1), reader.GetBoolean(2), reader.GetInt32(3));
            }
            // No caller observes acceptance before the physical commit acknowledgement.
            return result;
        }, token);

    public async Task<ProviderEventRecipient?> ClaimAsync(Guid acquisitionToken, CancellationToken token = default)
    {
        var claimed = await ExecuteAsync(async (connection, transaction, deadline) =>
        {
            StoredClaim? result = null;
            await using (var command = new NpgsqlCommand("SELECT target_id,event_id,tenant_id,claim_token,claim_until FROM public.fn_identity_provider_event_claim(@token)", connection, transaction))
            {
                command.Parameters.AddWithValue("token", acquisitionToken);
                await using var reader = await command.ExecuteReaderAsync(deadline).ConfigureAwait(false);
                if (await reader.ReadAsync(deadline).ConfigureAwait(false))
                    result = new(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetGuid(3),
                        reader.GetFieldValue<DateTimeOffset>(4));
            }
            return result;
        }, token).ConfigureAwait(false);
        return claimed is null ? null : FromStoredClaim(claimed.Target, claimed.Event, claimed.Tenant,
            claimed.Token, claimed.Until, _lifetime.Token);
    }

    private sealed record StoredClaim(Guid Target, Guid Event, Guid Tenant, Guid Token, DateTimeOffset Until)
    {
        public override string ToString() => nameof(StoredClaim);
    }

    // Each function call keeps its original 100/100 bound. Separate commits
    // make progress durable even if a later slice fails; the finite batch count
    // yields back to source reads and target publication under sustained load.
    internal const int RetirementBatchesPerSlice = 16;

    public async Task<int> RetireAsync(CancellationToken token = default)
    {
        var removed = 0;
        for (var batch = 0; batch < RetirementBatchesPerSlice; batch++)
        {
            var progress = await ExecuteAsync(async (connection, transaction, deadline) =>
            {
                int count;
                await using (var command = new NpgsqlCommand("SELECT public.fn_identity_provider_event_retire()", connection, transaction))
                    count = (int)(await command.ExecuteScalarAsync(deadline).ConfigureAwait(false))!;
                // Receipt count alone cannot signal completion: a problem-only
                // backlog also needs retirement, including source-expiry marking.
                await using var pending = new NpgsqlCommand("""
                    SELECT EXISTS(SELECT FROM public.identity_provider_event_receipts
                            WHERE received_at<clock_timestamp()-interval '32 days')
                        OR EXISTS(SELECT FROM public.identity_provider_event_problems
                            WHERE first_seen_at<clock_timestamp()-interval '32 days')
                        OR EXISTS(SELECT FROM public.identity_provider_event_problems p
                            WHERE p.resolved_at IS NULL AND NOT p.irrecoverable
                                AND p.range_to<clock_timestamp()-make_interval(secs=>(
                                    SELECT retention_seconds FROM public.identity_provider_event_scan WHERE id=1)))
                    """, connection, transaction);
                return (Removed: count, Pending: (bool)(await pending.ExecuteScalarAsync(deadline).ConfigureAwait(false))!);
            }, token).ConfigureAwait(false);
            removed = checked(removed + progress.Removed);
            if (!progress.Pending) break;
        }
        return removed;
    }

}

public sealed record ProviderEventAcceptance(Guid ReceiptId, string Disposition, bool Accepted, int TargetCount)
{
    public override string ToString() => nameof(ProviderEventAcceptance);
}
