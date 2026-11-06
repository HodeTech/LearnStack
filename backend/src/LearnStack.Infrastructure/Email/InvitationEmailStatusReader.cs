using LearnStack.SharedKernel.Email;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;
using Npgsql;
using NpgsqlTypes;

namespace LearnStack.Infrastructure.Email;

public sealed class InvitationEmailStatusReader(IUnitOfWork unit) : IInvitationEmailStatusReader
{
    internal const int MaximumBatch = 100;

    // One scope predicate for both reads, so a page can never classify a job differently from its detail.
    private const string Scope = """
        AND tenant_id=@tenant AND organization_id IS NOT DISTINCT FROM @org
        AND definition_key=CASE WHEN @org IS NULL THEN 'identity.invitation_email_tenant.v1' ELSE 'identity.invitation_email_organization.v1' END
        AND profile='external' AND capacity_group='identity.invitation_email' AND queue='external'
        """;

    public async Task<InvitationEmailDeliveryStatus> ReadAsync(BackgroundJobId currentDeliveryJobId, CancellationToken cancellationToken = default)
    {
        var announcement = Owner();
        await using var command = Command(
            "SELECT phase,finalization_status,quarantine_reason IS NOT NULL FROM public.background_jobs WHERE id=@job " + Scope, announcement);
        command.Parameters.AddWithValue("job", currentDeliveryJobId.Value);
        return await Guarded(async token =>
        {
            await using var row = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await row.ReadAsync(token).ConfigureAwait(false)) return InvitationEmailDeliveryStatus.Unavailable;
            return Classify(row.GetString(0), row.IsDBNull(1) ? null : row.GetString(1), row.GetBoolean(2));
        }, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<BackgroundJobId, InvitationEmailDeliveryStatus>> ReadManyAsync(
        IReadOnlyCollection<BackgroundJobId> currentDeliveryJobIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentDeliveryJobIds);
        if (currentDeliveryJobIds.Count > MaximumBatch) throw new ArgumentOutOfRangeException(nameof(currentDeliveryJobIds));
        var announcement = Owner();
        var result = new Dictionary<BackgroundJobId, InvitationEmailDeliveryStatus>(currentDeliveryJobIds.Count);
        foreach (var id in currentDeliveryJobIds) result[id] = InvitationEmailDeliveryStatus.Unavailable;
        if (result.Count == 0) return result;
        await using var command = Command(
            "SELECT id,phase,finalization_status,quarantine_reason IS NOT NULL FROM public.background_jobs WHERE id=ANY(@jobs) " + Scope, announcement);
        command.Parameters.Add(new NpgsqlParameter("jobs", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        { Value = result.Keys.Select(id => id.Value).ToArray() });
        return await Guarded(async token =>
        {
            await using var rows = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await rows.ReadAsync(token).ConfigureAwait(false))
            {
                var id = BackgroundJobId.From(rows.GetGuid(0));
                if (result.ContainsKey(id))
                    result[id] = Classify(rows.GetString(1), rows.IsDBNull(2) ? null : rows.GetString(2), rows.GetBoolean(3));
            }
            return (IReadOnlyDictionary<BackgroundJobId, InvitationEmailDeliveryStatus>)result;
        }, cancellationToken);
    }

    internal static InvitationEmailDeliveryStatus Classify(string phase, string? finalizationStatus, bool quarantined)
    {
        if (quarantined) return InvitationEmailDeliveryStatus.Unresolved;
        if (finalizationStatus is not null)
            return finalizationStatus == "succeeded" ? InvitationEmailDeliveryStatus.SubmissionAccepted : InvitationEmailDeliveryStatus.Failed;
        return phase switch
        {
            "executing" or "reconciling" => InvitationEmailDeliveryStatus.Unresolved,
            "terminal_pending" or "dead_lettered" => InvitationEmailDeliveryStatus.Failed,
            _ => InvitationEmailDeliveryStatus.Queued,
        };
    }

    private TenantContextAnnouncement Owner()
    {
        if (!unit.HasActiveTransaction || unit.IsRollbackOnly || unit.GetTenantContextAnnouncement(unit.Transaction) is not { } announcement)
            throw new InvalidOperationException("Invitation status requires its existing announced owner.");
        return announcement;
    }

    private NpgsqlCommand Command(string sql, TenantContextAnnouncement announcement)
    {
        var command = new NpgsqlCommand(sql, (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("tenant", announcement.TenantId.Value);
        command.Parameters.AddWithValue("org", NpgsqlDbType.Uuid, (object?)announcement.OrganizationId?.Value ?? DBNull.Value);
        return command;
    }

    private async Task<T> Guarded<T>(Func<CancellationToken, Task<T>> read, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        try { return await read(budget.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && budget.IsCancellationRequested)
        { unit.MarkRollbackOnly(); throw new InfrastructureException("Invitation delivery status unavailable."); }
        catch (NpgsqlException) when (cancellationToken.IsCancellationRequested)
        { unit.MarkRollbackOnly(); throw new OperationCanceledException(cancellationToken); }
        catch (NpgsqlException) { unit.MarkRollbackOnly(); throw new InfrastructureException("Invitation delivery status unavailable."); }
    }
}
