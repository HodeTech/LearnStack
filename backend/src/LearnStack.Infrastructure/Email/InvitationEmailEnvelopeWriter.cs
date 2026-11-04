using LearnStack.Application.Messaging;
using LearnStack.SharedKernel.Email;
using LearnStack.SharedKernel.Persistence;
using Npgsql;
using NpgsqlTypes;

namespace LearnStack.Infrastructure.Email;

/// <summary>Provider-only INSERT on the exact invitation writer's existing business transaction.</summary>
public sealed class InvitationEmailEnvelopeWriter(IUnitOfWork unit, ProducerExecution producer, InvitationEmailCipher cipher)
{
    public async Task StageAsync(InvitationEmailBinding binding, EmailSubmission message, CancellationToken cancellationToken = default)
    {
        var origin = producer.CaptureJobOrigin(unit);
        if (origin.OperationType.Assembly.GetName().Name != "LearnStack.Modules.Identity.Application.Contracts"
            || origin.OperationType.FullName is not ("LearnStack.Modules.Identity.Application.Contracts.Invitations.CreateInvitationCommand"
                or "LearnStack.Modules.Identity.Application.Contracts.Invitations.ReissueInvitationCommand")
            || origin.Announcement.TenantId.Value != binding.TenantId || origin.Announcement.OrganizationId?.Value != binding.OrganizationId
            || unit.Mode != TransactionMode.ReadWrite)
            throw new InvalidOperationException("Invitation email staging owner refused.");
        var envelope = cipher.Encrypt(binding, message);
        producer.RequireJobOrigin(origin, unit);
        await using var command = new NpgsqlCommand("""
            INSERT INTO public.invitation_email_envelopes(id,tenant_id,organization_id,invitation_id,generation_id,job_id,
                format_version,key_version,nonce,ciphertext,tag)
            VALUES(@id,@tenant,@org,@invitation,@generation,@job,1,@key,@nonce,@ciphertext,@tag)
            """, (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("id", binding.SpoolId);
        command.Parameters.AddWithValue("tenant", binding.TenantId);
        command.Parameters.AddWithValue("org", NpgsqlDbType.Uuid, (object?)binding.OrganizationId ?? DBNull.Value);
        command.Parameters.AddWithValue("invitation", binding.InvitationId);
        command.Parameters.AddWithValue("generation", binding.GenerationId);
        command.Parameters.AddWithValue("job", binding.JobId);
        command.Parameters.AddWithValue("key", envelope.KeyVersion);
        command.Parameters.AddWithValue("nonce", envelope.Nonce);
        command.Parameters.AddWithValue("ciphertext", envelope.Ciphertext);
        command.Parameters.AddWithValue("tag", envelope.Tag);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        try { await command.ExecuteNonQueryAsync(budget.Token).ConfigureAwait(false); }
        catch { unit.MarkRollbackOnly(); throw new InvalidOperationException("Invitation email staging unavailable."); }
    }
}
