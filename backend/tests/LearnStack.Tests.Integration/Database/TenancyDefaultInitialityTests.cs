using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Raw app-role guard proofs; these do not establish human application authorization.</summary>
/// <remarks>Each case owns a disposable database with the shipped guards and grants unchanged.</remarks>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class TenancyDefaultInitialityTests(SchemaFixture schema)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retained_NULL_default_cannot_manufacture_initiality_without_the_tenant_fence(bool resetRetainedStamps)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        var graph = await CreateRetainedGraphAsync(database, resetRetainedStamps);
        var before = await ReadAsync(database, graph);
        Assert.Null(before.DefaultOrganizationId);
        Assert.Equal(2L, before.ActiveOrganizations);
        Assert.Equal(resetRetainedStamps ? 7L : 0L, before.Version);
        Assert.Equal(resetRetainedStamps, before.UpdatedAt is not null);
        Assert.Equal(resetRetainedStamps, before.UpdatedBy is not null);

        await using var app = await OpenAppAsync(database);
        await using var transaction = await app.BeginTransactionAsync();
        await AnnounceAsync(app, transaction, graph.Tenant);
        var preliminaryUpdateSucceeded = false;
        var failure = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await PrepareInitialityAsync(app, transaction, graph, resetRetainedStamps);
            preliminaryUpdateSucceeded = true;
            await AssignDefaultAsync(app, transaction, graph);
        });
        Assert.Equal("P0311", failure.SqlState);
        Assert.False(preliminaryUpdateSucceeded);
        await transaction.RollbackAsync();
        Assert.Equal(before, await ReadAsync(database, graph));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_same_retained_NULL_default_completes_under_the_canonical_exclusive_tenant_fence(bool resetRetainedStamps)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        var graph = await CreateRetainedGraphAsync(database, resetRetainedStamps);
        await using var app = await OpenAppAsync(database);
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await AnnounceAsync(app, transaction, graph.Tenant);
            await LockTenantAsync(app, transaction, graph.Tenant);
            await PrepareInitialityAsync(app, transaction, graph, resetRetainedStamps);
            await AssignDefaultAsync(app, transaction, graph);
            await ExecuteAsync(app, transaction, "SET CONSTRAINTS ALL IMMEDIATE");
            await transaction.CommitAsync();
        }

        var after = await ReadAsync(database, graph);
        Assert.Equal(graph.First, after.DefaultOrganizationId);
        Assert.Equal(1L, after.Version);
        Assert.NotNull(after.UpdatedAt);
        Assert.Equal(UserId.SystemActor.Value, after.UpdatedBy);
        Assert.Equal("Active", after.FirstOrganizationStatus);
        Assert.Equal(2L, after.ActiveOrganizations);
    }

    [Fact]
    public async Task Untouched_same_transaction_INSERT_provisioning_keeps_its_unfenced_default_completion()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        var graph = new Graph(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        await using var app = await OpenAppAsync(database);
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await AnnounceAsync(app, transaction, graph.Tenant);
            await InsertGraphAsync(app, transaction, graph, edited: false);
            // No preliminary UPDATE or advisory acquisition: this is the actual INSERT-first shape.
            await AssignDefaultAsync(app, transaction, graph);
            await ExecuteAsync(app, transaction, "SET CONSTRAINTS ALL IMMEDIATE");
            await transaction.CommitAsync();
        }

        var after = await ReadAsync(database, graph);
        Assert.Equal(graph.First, after.DefaultOrganizationId);
        Assert.Equal(1L, after.Version);
        Assert.Equal("Active", after.FirstOrganizationStatus);
        Assert.Equal(2L, after.ActiveOrganizations);
    }

    [Fact]
    public async Task Retained_NULL_initiality_refusal_prevents_an_unfenced_default_and_fenced_archive_from_both_committing()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        var graph = await CreateRetainedGraphAsync(database, edited: false);
        var before = await ReadAsync(database, graph);
        await using var selection = await OpenAppAsync(database);
        await using var selectionTransaction = await selection.BeginTransactionAsync();
        await AnnounceAsync(selection, selectionTransaction, graph.Tenant);
        var refused = false;
        var preliminaryUpdateSucceeded = false;
        try
        {
            await PrepareInitialityAsync(selection, selectionTransaction, graph, resetRetainedStamps: false);
            preliminaryUpdateSucceeded = true;
            await AssignDefaultAsync(selection, selectionTransaction, graph);
            await ExecuteAsync(selection, selectionTransaction, "SET CONSTRAINTS ALL IMMEDIATE");
        }
        catch (PostgresException failure) when (failure.SqlState == "P0311")
        {
            refused = true;
            await selectionTransaction.RollbackAsync();
        }

        // On the defective guard the selection transaction remains live with its default
        // validated, but no advisory fence. This independent app owner therefore archives
        // its selected Organization while seeing the retained, committed NULL default.
        // The second Active Organization keeps the archive's last-Active check meaningful.
        await using var archive = await OpenAppAsync(database);
        Assert.NotEqual(selection.ProcessID, archive.ProcessID);
        await using (var archiveTransaction = await archive.BeginTransactionAsync())
        {
            await AnnounceAsync(archive, archiveTransaction, graph.Tenant);
            await LockTenantAsync(archive, archiveTransaction, graph.Tenant);
            await ExecuteAsync(archive, archiveTransaction, """
                UPDATE public.organizations SET status='Archived',row_version=row_version+1
                    WHERE tenant_id=@tenant AND id=@first
                """, ("tenant", graph.Tenant), ("first", graph.First));
            await ExecuteAsync(archive, archiveTransaction, "SET CONSTRAINTS ALL IMMEDIATE");
            await archiveTransaction.CommitAsync();
        }
        if (!refused) await selectionTransaction.CommitAsync();

        var after = await ReadAsync(database, graph);
        Assert.Equal("Archived", after.FirstOrganizationStatus);
        Assert.Equal(1L, after.ActiveOrganizations);
        // Before the correction both real transactions commit and this is the Archived
        // Organization's id. No sleep, cancelled competitor or mocked guard can satisfy it.
        Assert.Null(after.DefaultOrganizationId);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.Equal(before.UpdatedBy, after.UpdatedBy);
        Assert.True(refused);
        Assert.False(preliminaryUpdateSucceeded);
    }

    private static async Task<Graph> CreateRetainedGraphAsync(DisposableSchemaDatabase database, bool edited)
    {
        var graph = new Graph(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        await using var app = await OpenAppAsync(database);
        await using var transaction = await app.BeginTransactionAsync();
        await AnnounceAsync(app, transaction, graph.Tenant);
        await InsertGraphAsync(app, transaction, graph, edited);
        await ExecuteAsync(app, transaction, "SET CONSTRAINTS ALL IMMEDIATE");
        await transaction.CommitAsync();
        return graph;
    }

    private static Task InsertGraphAsync(NpgsqlConnection app, NpgsqlTransaction transaction, Graph graph, bool edited) =>
        ExecuteAsync(app, transaction, """
            INSERT INTO public.tenants(id,slug,display_name,status,created_at,created_by,row_version,updated_at,updated_by)
                VALUES(@tenant,'initiality-fixture','Initiality fixture','Trial',clock_timestamp(),@actor,
                    CASE WHEN @edited THEN 7 ELSE 0 END,
                    CASE WHEN @edited THEN clock_timestamp() ELSE NULL END,
                    CASE WHEN @edited THEN @actor ELSE NULL END);
            INSERT INTO public.organizations(id,tenant_id,slug,display_name,status,created_at,created_by,row_version)
                VALUES(@first,@tenant,'first','First','Active',clock_timestamp(),@actor,0),
                      (@second,@tenant,'second','Second','Active',clock_timestamp(),@actor,0);
            """, ("tenant", graph.Tenant), ("first", graph.First), ("second", graph.Second),
            ("actor", UserId.SystemActor.Value), ("edited", edited));

    private static Task PrepareInitialityAsync(NpgsqlConnection app, NpgsqlTransaction transaction, Graph graph, bool resetRetainedStamps) =>
        ExecuteAsync(app, transaction, resetRetainedStamps
            ? "UPDATE public.tenants SET row_version=0,updated_at=NULL,updated_by=NULL WHERE id=@tenant"
            : "UPDATE public.tenants SET display_name=display_name WHERE id=@tenant", ("tenant", graph.Tenant));

    private static Task AssignDefaultAsync(NpgsqlConnection app, NpgsqlTransaction transaction, Graph graph) =>
        ExecuteAsync(app, transaction, """
            UPDATE public.tenants SET default_organization_id=@first,row_version=row_version+1,
                updated_at=clock_timestamp(),updated_by=@actor WHERE id=@tenant
            """, ("tenant", graph.Tenant), ("first", graph.First), ("actor", UserId.SystemActor.Value));

    private static Task LockTenantAsync(NpgsqlConnection app, NpgsqlTransaction transaction, Guid tenant) =>
        ExecuteAsync(app, transaction, "SELECT pg_catalog.pg_advisory_xact_lock(@key)",
            ("key", IdentityAdvisoryLockKey.ForTenant(TenantId.From(tenant))));

    private static Task AnnounceAsync(NpgsqlConnection app, NpgsqlTransaction transaction, Guid tenant) =>
        ExecuteAsync(app, transaction, """
            SELECT set_config('app.tenant_id',@tenant,true),set_config('app.organization_id','',true),set_config('app.scope','',true)
            """, ("tenant", tenant.ToString("D")));

    private static async Task<NpgsqlConnection> OpenAppAsync(DisposableSchemaDatabase database)
    {
        var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
        try
        {
            await SchemaQueries.AssertApplicationRoleAsync(app);
            return app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    private static async Task<State> ReadAsync(DisposableSchemaDatabase database, Graph graph)
    {
        await using var app = await OpenAppAsync(database);
        await using var transaction = await app.BeginTransactionAsync();
        await AnnounceAsync(app, transaction, graph.Tenant);
        await using var command = new NpgsqlCommand("""
            SELECT tenant.default_organization_id,tenant.row_version,tenant.updated_at,tenant.updated_by,
                organization.status,
                (SELECT count(*) FROM public.organizations WHERE tenant_id=@tenant AND status='Active'
                    AND deleted_at IS NULL AND deleted_by IS NULL)
            FROM public.tenants AS tenant JOIN public.organizations AS organization
                ON organization.tenant_id=tenant.id AND organization.id=@first
            WHERE tenant.id=@tenant
            """, app, transaction) { CommandTimeout = 10 };
        command.Parameters.AddWithValue("tenant", graph.Tenant);
        command.Parameters.AddWithValue("first", graph.First);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var state = new State(reader.IsDBNull(0) ? null : reader.GetGuid(0), reader.GetInt64(1),
            reader.IsDBNull(2) ? null : reader.GetDateTime(2), reader.IsDBNull(3) ? null : reader.GetGuid(3),
            reader.GetString(4), reader.GetInt64(5));
        Assert.False(await reader.ReadAsync());
        return state;
    }

    private static async Task ExecuteAsync(NpgsqlConnection app, NpgsqlTransaction transaction, string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, app, transaction) { CommandTimeout = 10 };
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private sealed record Graph(Guid Tenant, Guid First, Guid Second);
    private sealed record State(Guid? DefaultOrganizationId, long Version, DateTime? UpdatedAt, Guid? UpdatedBy,
        string FirstOrganizationStatus, long ActiveOrganizations);
}
