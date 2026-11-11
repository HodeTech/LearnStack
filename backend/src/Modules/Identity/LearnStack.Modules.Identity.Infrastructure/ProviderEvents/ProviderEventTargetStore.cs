using LearnStack.Infrastructure.Identity.ProviderEvents;
using LearnStack.Modules.Identity.Application.Contracts.ProviderEvents;
using LearnStack.Modules.Identity.Application.ProviderEvents;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Npgsql;

namespace LearnStack.Modules.Identity.Infrastructure.ProviderEvents;

/// <summary>Fixed tenant SQL only; it cannot acquire a connection, accept a global fact or announce scope.</summary>
public sealed class ProviderEventTargetStore(IUnitOfWork unit, ITenantContext context)
    : IProviderEventTargetStore, IProviderEventAuditFactReader
{
    public async Task RequireClaimAsync(ProviderEventRecipient recipient, CancellationToken cancellationToken = default)
    {
        RequireProvider(recipient);
        try
        {
            await using var command = new NpgsqlCommand("""
                SELECT 1 FROM public.identity_provider_event_targets
                WHERE id=@target AND event_id=@event AND tenant_id=@tenant AND claim_token=@token
                    AND claim_until>clock_timestamp() AND enqueued_at IS NULL FOR UPDATE
                """, (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!) { CommandTimeout = 5 };
            Bind(command, recipient);
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not 1)
                throw new InvalidOperationException("Provider production has no live exact target claim.");
            RequireProvider(recipient);
        }
        catch { unit.MarkRollbackOnly(); throw; }
    }

    public async Task MarkEnqueuedAsync(ProviderEventRecipient recipient, CancellationToken cancellationToken = default)
    {
        RequireProvider(recipient);
        try
        {
            await using var command = new NpgsqlCommand("""
                UPDATE public.identity_provider_event_targets SET enqueued_at=clock_timestamp()
                WHERE id=@target AND event_id=@event AND tenant_id=@tenant AND claim_token=@token
                    AND claim_until>clock_timestamp() AND enqueued_at IS NULL
                """, (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!) { CommandTimeout = 5 };
            Bind(command, recipient);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("Provider production could not complete its exact target.");
            RequireProvider(recipient);
        }
        catch { unit.MarkRollbackOnly(); throw; }
    }

    public async Task<ProviderEventAuditFact> ReadAsync(Guid targetId, CancellationToken cancellationToken = default)
    {
        if (targetId == Guid.Empty || targetId.Version != 7 || context is not EventTenantContext delivery
            || delivery.OrganizationId is not null || delivery.CausalActorUserId is not null
            || delivery.UserId != UserId.SystemActor || unit.Mode != TransactionMode.ReadWrite
            || !unit.IsTenantContextIssuedOn(unit.Transaction))
        {
            unit.MarkRollbackOnly();
            throw new InvalidOperationException("Provider observation requires its actual tenant delivery.");
        }
        try
        {
            await using var command = new NpgsqlCommand("""
                SELECT id,event_id,tenant_id,authority_id,realm_id,provider_event_id,affected_user_id,source_occurred_at,source_kind,receipt_id
                FROM public.identity_provider_event_targets
                WHERE id=@target AND event_id=@event AND tenant_id=@tenant AND enqueued_at IS NOT NULL
                """, (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!) { CommandTimeout = 5 };
            command.Parameters.AddWithValue("target", targetId);
            command.Parameters.AddWithValue("event", delivery.EventId);
            command.Parameters.AddWithValue("tenant", delivery.TenantId.Value);
            await using var row = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await row.ReadAsync(cancellationToken).ConfigureAwait(false) || !IdentityEventKindCodes.TryKind(row.GetString(8), out var kind))
                throw new InvalidOperationException("Provider observation has no exact retained target.");
            var fact = new ProviderEventAuditFact(row.GetGuid(0), row.GetGuid(9), row.GetGuid(1), TenantId.From(row.GetGuid(2)),
                IdentityAuthorityId.From(row.GetGuid(3)), row.GetString(4), row.GetGuid(5), UserId.From(row.GetGuid(6)),
                row.GetFieldValue<DateTimeOffset>(7), kind);
            cancellationToken.ThrowIfCancellationRequested();
            if (!unit.IsTenantContextIssuedOn(unit.Transaction) || unit.IsRollbackOnly)
                throw new InvalidOperationException("Provider observation lost its delivery owner.");
            return fact;
        }
        catch { unit.MarkRollbackOnly(); throw; }
    }

    private void RequireProvider(ProviderEventRecipient recipient)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        if (!ReferenceEquals(context, recipient) || !recipient.Matches(unit))
        {
            unit.MarkRollbackOnly();
            throw new InvalidOperationException("Provider target SQL requires its captured production owner.");
        }
    }

    private static void Bind(NpgsqlCommand command, ProviderEventRecipient recipient)
    {
        command.Parameters.AddWithValue("target", recipient.TargetId);
        command.Parameters.AddWithValue("event", recipient.EventId);
        command.Parameters.AddWithValue("tenant", recipient.TenantId.Value);
        command.Parameters.AddWithValue("token", recipient.ClaimToken);
    }
}
