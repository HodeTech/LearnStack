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
            SELECT t.claim_token=@token AND t.claim_until>clock_timestamp()
                AND t.enqueued_at IS NOT NULL AND EXISTS (
                    SELECT FROM public.outbox_messages o
                    WHERE o.id=t.event_id AND o.tenant_id=t.tenant_id AND o.organization_id IS NULL
                        AND o.topic='learnstack.identity.user' AND o.payload->>'TargetId'=t.id::text
                        AND o.payload->>'EventId'=t.event_id::text AND o.payload->>'TenantId'=t.tenant_id::text
                        AND split_part(o.type,',',1)='LearnStack.Modules.Identity.Application.Contracts.IntegrationEvents.UserProviderEventObservedIntegrationEventV1'
                        AND trim(split_part(o.type,',',2))='LearnStack.Modules.Identity.Application.Contracts')
            FROM public.identity_provider_event_targets t
            WHERE t.id=@target AND t.event_id=@event AND t.tenant_id=@tenant FOR UPDATE
            """, _connection, (NpgsqlTransaction)_transaction) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("token", recipient.ClaimToken);
        command.Parameters.AddWithValue("target", recipient.TargetId);
        command.Parameters.AddWithValue("event", recipient.EventId);
        command.Parameters.AddWithValue("tenant", recipient.TenantId.Value);
        if (await command.ExecuteScalarAsync(token).ConfigureAwait(false) is not true
            || !recipient.IsBoundTo(this, _transaction, _generation))
            throw new InvalidOperationException("Provider target production is incomplete or its claim expired before commit.");
        token.ThrowIfCancellationRequested();
    }
}
