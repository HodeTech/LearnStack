using FluentAssertions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Nonempty, application-role controls for the new definition table and immutable history.</summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class CustomFieldDefinitionIsolationTests(SchemaFixture schema)
{
    [Fact]
    public async Task Tenant_A_cannot_read_Tenant_B_custom_field_definitions()
    {
        await using var app = await OpenAsync();
        await using var transaction = await app.BeginTransactionAsync();
        var a = await InsertAsync(app, transaction, SchemaFixture.TenantA);
        var b = await InsertAsync(app, transaction, SchemaFixture.TenantB);
        await SchemaQueries.SetTenantAsync(app, transaction, SchemaFixture.TenantA);
        await using var read = new NpgsqlCommand("SELECT id FROM tenant_custom_field_defs WHERE id = ANY(@ids)", app, transaction);
        read.Parameters.AddWithValue("ids", new[] { a, b });
        await using var rows = await read.ExecuteReaderAsync();
        var found = new List<Guid>(); while (await rows.ReadAsync()) found.Add(rows.GetGuid(0));
        found.Should().Equal(a);
    }

    [Fact]
    public async Task Unsetting_tenant_context_returns_zero_nonempty_definition_rows_through_RLS()
    {
        await using var app = await OpenAsync();
        await using var transaction = await app.BeginTransactionAsync();
        var a = await InsertAsync(app, transaction, SchemaFixture.TenantA);
        var b = await InsertAsync(app, transaction, SchemaFixture.TenantB);
        await using (var clear = new NpgsqlCommand("SELECT set_config('app.tenant_id','',true)", app, transaction))
            await clear.ExecuteNonQueryAsync();
        await using var read = new NpgsqlCommand("SELECT count(*) FROM tenant_custom_field_defs WHERE id = ANY(@ids)", app, transaction);
        read.Parameters.AddWithValue("ids", new[] { a, b });
        (await read.ExecuteScalarAsync()).Should().Be(0L);
    }

    [Fact]
    public async Task Raw_definition_write_without_the_exclusive_Tenant_fence_refuses()
    {
        await using var app = await OpenAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await SchemaQueries.SetTenantAsync(app, transaction, SchemaFixture.TenantA);
        var act = async () => await InsertAsync(app, transaction, SchemaFixture.TenantA, acquireFence: false);
        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("P0301");
    }

    [Fact]
    public async Task Published_schema_classification_labels_and_ordering_are_immutable_in_the_database()
    {
        await using var app = await OpenAsync();
        await using var transaction = await app.BeginTransactionAsync();
        var id = await InsertAsync(app, transaction, SchemaFixture.TenantA);
        await using (var publish = new NpgsqlCommand("UPDATE tenant_custom_field_defs SET status='Active',row_version=1,updated_at=now(),updated_by=created_by WHERE id=@id", app, transaction))
        { publish.Parameters.AddWithValue("id", id); (await publish.ExecuteNonQueryAsync()).Should().Be(1); }
        await using var mutate = new NpgsqlCommand("UPDATE tenant_custom_field_defs SET labels_json='{\"en\":\"Changed\"}',row_version=2 WHERE id=@id", app, transaction);
        mutate.Parameters.AddWithValue("id", id);
        var act = async () => await mutate.ExecuteNonQueryAsync();
        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("P0304");
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var app = new NpgsqlConnection(schema.Postgres.AppConnectionString);
        await app.OpenAsync();
        return app;
    }

    private static async Task<Guid> InsertAsync(NpgsqlConnection app, NpgsqlTransaction transaction, Guid tenant, bool acquireFence = true)
    {
        await SchemaQueries.SetTenantAsync(app, transaction, tenant);
        if (acquireFence)
        {
            await using var fence = new NpgsqlCommand("SELECT pg_advisory_xact_lock(public.fn_identity_contextual_lock_key('tenant',@tenant))", app, transaction);
            fence.Parameters.AddWithValue("tenant", tenant);
            await fence.ExecuteNonQueryAsync();
        }
        var id = Guid.CreateVersion7();
        await using var insert = new NpgsqlCommand("""
            INSERT INTO tenant_custom_field_defs
                (id,tenant_id,target_entity,key,schema_version,status,json_schema,labels_json,pii_category,is_required,sort,
                 maximum_value_bytes,created_at,created_by,row_version)
            VALUES (@id,@tenant,'Membership',@key,1,'Draft',
                '{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","properties":{"value":{"type":"boolean"}},"required":["value"],"additionalProperties":false}',
                '{"en":"Label"}','None',false,0,5,now(),@actor,0)
            """, app, transaction);
        insert.Parameters.AddWithValue("id", id); insert.Parameters.AddWithValue("tenant", tenant);
        insert.Parameters.AddWithValue("key", "probe-" + id.ToString("N")); insert.Parameters.AddWithValue("actor", Guid.Parse("00000000-0000-7000-8000-000000000001"));
        await insert.ExecuteNonQueryAsync();
        return id;
    }
}
