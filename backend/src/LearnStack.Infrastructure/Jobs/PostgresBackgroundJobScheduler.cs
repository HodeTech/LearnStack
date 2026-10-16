using LearnStack.Application.Messaging;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Time;
using Npgsql;
using NpgsqlTypes;

namespace LearnStack.Infrastructure.Jobs;

/// <summary>Persists module-owned intent on the existing announced producer transaction.</summary>
public sealed class PostgresBackgroundJobScheduler(
    IUnitOfWork unitOfWork, ProducerExecution producer, BackgroundJobDefinitionRegistry registry,
    IGuidFactory ids, IClock clock) : IBackgroundJobScheduler
{
    private const string InsertSql =
        """
        INSERT INTO background_jobs (
            id, tenant_id, organization_id, definition_key, profile, queue, capacity_group,
            parameters, original_due_at, traceparent, causal_actor_user_id, causation_event_id, parent_job_id)
        VALUES (
            @id, @tenant_id, @organization_id, @definition_key, @profile, @queue, @capacity_group,
            @parameters, @original_due_at, @traceparent, @causal_actor_user_id, @causation_event_id, @parent_job_id)
        """;

    /// <inheritdoc />
    public Task<BackgroundJobId> EnqueueAsync<TParameters>(
        BackgroundJobKey<TParameters> definition, TParameters parameters,
        CancellationToken cancellationToken = default) where TParameters : IBackgroundJobParameters =>
        SubmitAsync(definition, parameters, null, cancellationToken);

    /// <inheritdoc />
    public Task<BackgroundJobId> ScheduleAsync<TParameters>(
        BackgroundJobKey<TParameters> definition, TParameters parameters, DateTimeOffset dueAt,
        CancellationToken cancellationToken = default) where TParameters : IBackgroundJobParameters =>
        SubmitAsync(definition, parameters, dueAt, cancellationToken);

    private async Task<BackgroundJobId> SubmitAsync<TParameters>(
        BackgroundJobKey<TParameters> key, TParameters parameters, DateTimeOffset? dueAt,
        CancellationToken cancellationToken) where TParameters : IBackgroundJobParameters
    {
        ArgumentNullException.ThrowIfNull(parameters);
        cancellationToken.ThrowIfCancellationRequested();
        // Check enrollment before payload access or Connection's lazy-open getter.
        var origin = producer.CaptureJobOrigin(unitOfWork);
        if (!BackgroundJobNames.Definition(key.Value) || !registry.TryGet(key.Value, out var definition)
            || definition.ParameterType != typeof(TParameters)
            || parameters.GetType() != definition.ParameterType)
            throw new ArgumentException("Job submission requires its exact declared key and parameter type.", nameof(key));

        var operationAssembly = origin.OperationType.Assembly;
        if (operationAssembly != definition.ProfileRegistration.RootRequestType.Assembly
            && operationAssembly != definition.ParameterType.Assembly)
            throw new InvalidOperationException("Only an enrolled operation of the owning module may submit this job.");

        var announcement = origin.Announcement;
        if (announcement.TenantId.Value == Guid.Empty || announcement.TenantId == TenantId.PlatformSentinel
            || (definition.Scope == BackgroundJobScope.TenantWide ? announcement.OrganizationId is not null
                : announcement.OrganizationId is null))
            throw new InvalidOperationException("The job scope must exactly match the live tenant and organization announcement.");

        // A declared Pending definition may accumulate intent; it cannot be started.
        var json = BackgroundJobPayloadContract.Serialize(parameters, definition.ParameterType,
            BackgroundJobPayloadKind.Parameters);
        producer.RequireJobOrigin(origin, unitOfWork);
        var due = (dueAt ?? clock.UtcNow).ToUniversalTime();
        producer.RequireJobOrigin(origin, unitOfWork);
        // PostgreSQL stores microseconds. Normalize before insertion so the same
        // immutable due fact is reconstructed by admission and later handoffs.
        due = new DateTimeOffset(due.Ticks - due.Ticks % TimeSpan.TicksPerMicrosecond, TimeSpan.Zero);
        var rawId = ids.NewUuidV7();
        producer.RequireJobOrigin(origin, unitOfWork);
        if (rawId == Guid.Empty || rawId.Version != 7)
            throw new InvalidOperationException("The job identity factory must return a UUIDv7.");
        var id = BackgroundJobId.From(rawId);

        if (unitOfWork.Transaction is not NpgsqlTransaction transaction
            || unitOfWork.Connection is not NpgsqlConnection connection
            || !ReferenceEquals(transaction.Connection, connection))
            throw new InvalidOperationException("Job persistence requires the ambient Npgsql connection and transaction.");

        await using var command = new NpgsqlCommand(InsertSql, connection, transaction);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, id.Value);
        command.Parameters.AddWithValue("tenant_id", NpgsqlDbType.Uuid, announcement.TenantId.Value);
        command.Parameters.AddWithValue("organization_id", NpgsqlDbType.Uuid,
            (object?)announcement.OrganizationId?.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("definition_key", NpgsqlDbType.Text, definition.Key);
        command.Parameters.AddWithValue("profile", NpgsqlDbType.Text,
            definition.Profile == BackgroundJobExecutionProfile.Transactional ? "transactional" : "external");
        command.Parameters.AddWithValue("queue", NpgsqlDbType.Text, definition.Queue);
        command.Parameters.AddWithValue("capacity_group", NpgsqlDbType.Text, (object?)definition.CapacityGroup ?? DBNull.Value);
        command.Parameters.AddWithValue("parameters", NpgsqlDbType.Text, json);
        command.Parameters.AddWithValue("original_due_at", NpgsqlDbType.TimestampTz, due);
        command.Parameters.AddWithValue("traceparent", NpgsqlDbType.Text, origin.Traceparent);
        command.Parameters.AddWithValue("causal_actor_user_id", NpgsqlDbType.Uuid, (object?)origin.CausalActor?.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("causation_event_id", NpgsqlDbType.Uuid, (object?)origin.IncomingEventId ?? DBNull.Value);
        command.Parameters.AddWithValue("parent_job_id", NpgsqlDbType.Uuid, (object?)origin.ParentJobId?.Value ?? DBNull.Value);

        producer.RequireJobOrigin(origin, unitOfWork);
        if (!ReferenceEquals(unitOfWork.Transaction, transaction))
            throw new InvalidOperationException("The producer transaction changed before job INSERT.");
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // A caught statement failure must not turn into a successful owner
            // result. No successful-production receipt is recorded for this path.
            unitOfWork.MarkRollbackOnly();
            throw;
        }

        producer.RecordProducedWork();
        return id;
    }
}
