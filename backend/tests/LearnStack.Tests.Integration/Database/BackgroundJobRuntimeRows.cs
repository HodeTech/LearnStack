using System.Data.Common;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;
using Npgsql;

namespace LearnStack.Tests.Integration.Database;

internal sealed record BackgroundJobRuntimeRow(Guid Id, Guid TenantId, Guid? OrganizationId,
    string Definition, string Profile, string Queue, string Parameters, DateTimeOffset OriginalDue,
    DateTimeOffset AvailableAfter, string Traceparent, Guid? CausalActor, Guid? IncomingEvent,
    Guid? ParentJob, string Phase, int NormalAttempts, Guid? CurrentAttempt, Guid? OwnershipToken,
    string? OwnershipKind, DateTimeOffset? OwnershipUntil);

/// <summary>Application-role observations; independent reads deliberately open another physical connection.</summary>
internal static class BackgroundJobRuntimeRows
{
    private const string ReadSql = """
        SELECT id, tenant_id, organization_id, definition_key, profile, queue, parameters,
            original_due_at, available_after, traceparent, causal_actor_user_id, causation_event_id,
            parent_job_id, phase, normal_attempts, current_attempt_id, ownership_token, ownership_kind, ownership_until
        FROM background_jobs WHERE id = @id
        """;

    internal static async Task<BackgroundJobRuntimeRow?> ReadAsync(string connectionString, BackgroundJobId id,
        Guid? organization = null, Guid? tenant = null)
    {
        await using var connection = await PostgresFixture.OpenAsync(connectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(connection, transaction, tenant ?? SchemaFixture.TenantA, organization);
        return await ReadAsync(connection, transaction, id);
    }

    internal static Task<BackgroundJobRuntimeRow?> ReadInUnitAsync(IUnitOfWork unit, BackgroundJobId id) =>
        ReadAsync(unit.Connection, unit.Transaction, id);

    private static async Task<BackgroundJobRuntimeRow?> ReadAsync(DbConnection connection, DbTransaction? transaction, BackgroundJobId id)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = ReadSql;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "id";
        parameter.Value = id.Value;
        command.Parameters.Add(parameter);
        await using var row = await command.ExecuteReaderAsync();
        if (!await row.ReadAsync()) return null;
        return new(row.GetGuid(0), row.GetGuid(1), NullableGuid(row, 2), row.GetString(3), row.GetString(4),
            row.GetString(5), row.GetString(6), row.GetFieldValue<DateTimeOffset>(7), row.GetFieldValue<DateTimeOffset>(8),
            row.GetString(9), NullableGuid(row, 10), NullableGuid(row, 11), NullableGuid(row, 12), row.GetString(13),
            row.GetInt32(14), NullableGuid(row, 15), NullableGuid(row, 16), row.IsDBNull(17) ? null : row.GetString(17),
            row.IsDBNull(18) ? null : row.GetFieldValue<DateTimeOffset>(18));
    }

    internal static async Task<BackgroundJobId> InsertAsync(string appConnectionString,
        string definition = BackgroundJobRuntimeFixture.TenantKey, Guid? organization = null,
        Guid? tenant = null, DateTimeOffset? due = null, string? parameters = null, string? capacityGroup = null,
        string? traceparent = null, Guid? causalActor = null, Guid? incomingEvent = null, string profile = "transactional",
        Guid? parentJob = null)
    {
        var id = BackgroundJobId.From(Guid.CreateVersion7());
        await using var connection = await PostgresFixture.OpenAsync(appConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(connection, transaction, tenant ?? SchemaFixture.TenantA, organization);
        await SchemaQueries.ExecuteAsync(connection, transaction, """
            INSERT INTO background_jobs (id, tenant_id, organization_id, definition_key, profile, queue,
                capacity_group, parameters, original_due_at, traceparent, causal_actor_user_id, causation_event_id, parent_job_id)
            VALUES (@id, @tenant, @organization, @definition, @profile, @queue,
                @capacity, @parameters, @due, @trace, @actor, @incoming, @parent)
            """, ("id", id.Value), ("tenant", tenant ?? SchemaFixture.TenantA),
            ("organization", (object?)organization ?? DBNull.Value), ("definition", definition),
            ("capacity", (object?)capacityGroup ?? DBNull.Value),
            ("parameters", parameters ?? "{\"ResourceId\":\"12345678-1234-7123-8123-123456789abc\"}"),
            ("due", due ?? BackgroundJobSchemaSeed.Slot),
            ("trace", traceparent ?? BackgroundJobRuntimeFixture.Traceparent),
            ("actor", (object?)causalActor ?? DBNull.Value), ("incoming", (object?)incomingEvent ?? DBNull.Value),
            ("parent", (object?)parentJob ?? DBNull.Value),
            ("profile", profile), ("queue", profile == "transactional" ? "transactional" : "external"));
        await transaction.CommitAsync();
        return id;
    }

    internal static async Task<long> CountAttemptsAsync(string connectionString, BackgroundJobId id, Guid? organization = null)
    {
        await using var connection = await PostgresFixture.OpenAsync(connectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(connection, transaction, SchemaFixture.TenantA, organization);
        await using var count = new NpgsqlCommand("SELECT count(*) FROM background_job_attempts WHERE job_id = @job",
            (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);
        count.Parameters.AddWithValue("job", id.Value);
        return (long)(await count.ExecuteScalarAsync())!;
    }

    internal static async Task<object?> ScalarInUnitAsync(IUnitOfWork unit, string sql)
    {
        await using var command = unit.Connection.CreateCommand();
        command.Transaction = unit.Transaction;
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    internal static async Task<long> CountOrganizationAsync(string appConnectionString, Guid organization)
    {
        await using var connection = await PostgresFixture.OpenAsync(appConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(connection, transaction, SchemaFixture.TenantA, null);
        await using var count = new NpgsqlCommand("SELECT count(*) FROM organizations WHERE id = @id",
            (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);
        count.Parameters.AddWithValue("id", organization);
        return (long)(await count.ExecuteScalarAsync())!;
    }

    private static Guid? NullableGuid(DbDataReader row, int ordinal) => row.IsDBNull(ordinal) ? null : row.GetGuid(ordinal);
}
