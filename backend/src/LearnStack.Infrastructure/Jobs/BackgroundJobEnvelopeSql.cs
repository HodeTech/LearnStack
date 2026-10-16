using System.Data.Common;
using System.Diagnostics;
using LearnStack.Application.Jobs;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;
using Npgsql;

namespace LearnStack.Infrastructure.Jobs;

internal static class BackgroundJobEnvelopeSql
{
    internal const string Projection = "id, tenant_id, organization_id, definition_key, profile, queue, capacity_group,"
        + " parameters, original_due_at, traceparent, causal_actor_user_id, causation_event_id, parent_job_id, recipient_id";

    internal static NpgsqlCommand Command(IUnitOfWork unit, string sql, BackgroundJobRoutingClaim claim,
        BackgroundJobRuntimeOptions options)
    {
        if (unit.Connection is not NpgsqlConnection connection || unit.Transaction is not NpgsqlTransaction transaction
            || !unit.HasActiveTransaction || unit.IsRollbackOnly
            || unit.GetTenantContextAnnouncement(transaction) is not { } announcement
            || announcement.TenantId != claim.TenantId || announcement.OrganizationId != claim.OrganizationId)
            throw new InvalidOperationException("Job storage requires its exact announced app-role transaction.");
        var command = new NpgsqlCommand(sql, connection, transaction)
        { CommandTimeout = (int)Math.Ceiling(options.SqlTimeout.TotalSeconds) };
        command.Parameters.AddWithValue("id", claim.JobId.Value);
        command.Parameters.AddWithValue("tenant", claim.TenantId.Value);
        return command;
    }

    internal static BackgroundJobEnvelope Read(DbDataReader row) => new(
        BackgroundJobId.From(row.GetGuid(0)), TenantId.From(row.GetGuid(1)),
        row.IsDBNull(2) ? null : OrganizationId.From(row.GetGuid(2)), row.GetString(3), row.GetString(4), row.GetString(5),
        row.IsDBNull(6) ? null : row.GetString(6), row.GetString(7), row.GetFieldValue<DateTimeOffset>(8), row.GetString(9),
        row.IsDBNull(10) ? null : UserId.From(row.GetGuid(10)), row.IsDBNull(11) ? null : row.GetGuid(11),
        row.IsDBNull(12) ? null : BackgroundJobId.From(row.GetGuid(12)),
        row.IsDBNull(13) ? null : BackgroundJobRecipientId.From(row.GetGuid(13)));

    internal static bool Matches(BackgroundJobEnvelope envelope, BackgroundJobRoutingClaim claim) =>
        envelope.JobId == claim.JobId && envelope.TenantId == claim.TenantId
        && envelope.OrganizationId == claim.OrganizationId && envelope.DefinitionKey == claim.DefinitionKey
        && envelope.Queue == claim.Queue && envelope.OriginalDueAt == claim.OriginalDueAt;

    internal static BackgroundJobDefinitionDescriptor? Definition(BackgroundJobEnvelope envelope,
        BackgroundJobDefinitionRegistry definitions)
    {
        if (!definitions.TryGet(envelope.DefinitionKey, out var definition)
            || definition.Readiness != BackgroundJobDefinitionReadiness.Active
            || definition.Profile != BackgroundJobExecutionProfile.Transactional || definition.CapacityGroup is not null
            || envelope.Profile != "transactional" || envelope.CapacityGroup is not null
            || definition.Queue != envelope.Queue
            || (definition.Scope == BackgroundJobScope.TenantWide) != (envelope.OrganizationId is null)
            || !ActivityContext.TryParse(envelope.Traceparent, null, out var trace)
            || envelope.Traceparent != $"00-{trace.TraceId}-{trace.SpanId}-{(byte)trace.TraceFlags:x2}"
            || envelope.CausalActor?.Value == Guid.Empty || envelope.IncomingEventId == Guid.Empty
            || envelope.ParentJobId == envelope.JobId
            || envelope.ParentJobId is { } parent && parent.Value.Version != 7
            || envelope.RecipientId is { } recipient && recipient.Value.Version != 7)
            return null;
        return definition;
    }
}
