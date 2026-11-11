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

    public Task<int> RetireAsync(CancellationToken token = default) => ExecuteAsync(async (connection, transaction, deadline) =>
    {
        int result;
        await using (var command = new NpgsqlCommand("SELECT public.fn_identity_provider_event_retire()", connection, transaction))
            result = (int)(await command.ExecuteScalarAsync(deadline).ConfigureAwait(false))!;
        return result;
    }, token);
}

public sealed record ProviderEventAcceptance(Guid ReceiptId, string Disposition, bool Accepted, int TargetCount)
{
    public override string ToString() => nameof(ProviderEventAcceptance);
}
