using Npgsql;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// An isolated database for proofs that deliberately remove a control or reverse a
/// migration. Setup commits before a separate authenticated application connection
/// exercises the remaining controls; the shared schema and its grants stay intact.
/// </summary>
internal sealed class DisposableSchemaDatabase : IAsyncDisposable
{
    private readonly PostgresFixture _postgres;
    private readonly string _database;

    private DisposableSchemaDatabase(PostgresFixture postgres)
    {
        _postgres = postgres;
        // Generated here, never caller input. The closed alphabet makes this safe as
        // a PostgreSQL identifier, which CREATE/DROP DATABASE cannot parameterize.
        _database = "scope_proof_" + Guid.NewGuid().ToString("N");
        MigrationConnectionString = For(postgres.MigrationConnectionString);
        AppConnectionString = For(postgres.AppConnectionString);
        PlatformConnectionString = For(postgres.PlatformConnectionString);
    }

    public string MigrationConnectionString { get; }
    public string AppConnectionString { get; }
    public string PlatformConnectionString { get; }

    public static async Task<DisposableSchemaDatabase> CreateAsync(
        PostgresFixture postgres, bool applyMigrations = true)
    {
        var database = new DisposableSchemaDatabase(postgres);
        await postgres.ExecuteAsSuperuserAsync(
            $"CREATE DATABASE {database._database} OWNER learnstack_migration");

        try
        {
            if (applyMigrations)
            {
                await MigrationChains.ApplyAllAsync(database.MigrationConnectionString);
            }

            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync() =>
        await _postgres.ExecuteAsSuperuserAsync($"DROP DATABASE {_database} WITH (FORCE)");

    private string For(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = _database,
            Pooling = false,
        }.ConnectionString;
}
