using LearnStack.SharedKernel.Email;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;
using Npgsql;
using NpgsqlTypes;

namespace LearnStack.Infrastructure.Email;

public sealed class InvitationEmailStatusReader(IUnitOfWork unit) : IInvitationEmailStatusReader
{
    public async Task<InvitationEmailDeliveryStatus> ReadAsync(BackgroundJobId currentDeliveryJobId, CancellationToken cancellationToken = default)
    {
        if (!unit.HasActiveTransaction || unit.IsRollbackOnly || unit.GetTenantContextAnnouncement(unit.Transaction) is not { } announcement)
            throw new InvalidOperationException("Invitation status requires its existing announced owner.");
        await using var command = new NpgsqlCommand("""
            SELECT phase,finalization_status,quarantine_reason IS NOT NULL FROM public.background_jobs
            WHERE id=@job AND tenant_id=@tenant AND organization_id IS NOT DISTINCT FROM @org
              AND definition_key=CASE WHEN @org IS NULL THEN 'identity.invitation_email_tenant.v1' ELSE 'identity.invitation_email_organization.v1' END
              AND profile='external' AND capacity_group='identity.invitation_email' AND queue='external'
            """, (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("job", currentDeliveryJobId.Value);
        command.Parameters.AddWithValue("tenant", announcement.TenantId.Value);
        command.Parameters.AddWithValue("org", NpgsqlDbType.Uuid, (object?)announcement.OrganizationId?.Value ?? DBNull.Value);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await using var row = await command.ExecuteReaderAsync(budget.Token).ConfigureAwait(false);
            if (!await row.ReadAsync(budget.Token).ConfigureAwait(false)) return InvitationEmailDeliveryStatus.Unavailable;
            if (row.GetBoolean(2)) return InvitationEmailDeliveryStatus.Unresolved;
            if (!row.IsDBNull(1)) return row.GetString(1) == "succeeded" ? InvitationEmailDeliveryStatus.SubmissionAccepted : InvitationEmailDeliveryStatus.Failed;
            return row.GetString(0) switch
            {
                "executing" or "reconciling" => InvitationEmailDeliveryStatus.Unresolved,
                "terminal_pending" or "dead_lettered" => InvitationEmailDeliveryStatus.Failed,
                _ => InvitationEmailDeliveryStatus.Queued,
            };
        }
        catch (NpgsqlException) { return InvitationEmailDeliveryStatus.Unavailable; }
    }
}
