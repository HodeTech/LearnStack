using LearnStack.SharedKernel.Identity;
using Npgsql;

namespace LearnStack.Infrastructure.Persistence;

public sealed partial class NpgsqlUnitOfWork
{
    private ProviderEventRecipient? _providerRecipient;
    private CancellationTokenRegistration _providerCancellation;

    private async Task ValidateProviderRecipientCommitAsync(ProviderEventRecipient recipient, CancellationToken token)
    {
        if (_transaction is null || !recipient.IsBoundTo(this, _transaction, _generation))
            throw new InvalidOperationException("Provider target owner expired before commit.");
        // Lock the exact claimed row through COMMIT. A concurrent reclaimer must
        // wait; database time, rather than the process clock, decides lease life.
        await using var command = new NpgsqlCommand("""
            SELECT claim_token=@token AND claim_until>clock_timestamp()
            FROM public.identity_provider_event_targets
            WHERE id=@target AND event_id=@event AND tenant_id=@tenant FOR UPDATE
            """, _connection, (NpgsqlTransaction)_transaction) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("token", recipient.ClaimToken);
        command.Parameters.AddWithValue("target", recipient.TargetId);
        command.Parameters.AddWithValue("event", recipient.EventId);
        command.Parameters.AddWithValue("tenant", recipient.TenantId.Value);
        if (await command.ExecuteScalarAsync(token).ConfigureAwait(false) is not true
            || !recipient.IsBoundTo(this, _transaction, _generation))
            throw new InvalidOperationException("Provider target claim expired before commit.");
        token.ThrowIfCancellationRequested();
    }
}
