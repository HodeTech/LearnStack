using FluentAssertions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// What the Customization schema guarantees about a versioned definition's key,
/// measured as <c>learnstack_app</c> against a real database.
/// </summary>
/// <remarks>
/// <para>
/// The catalogue-level sweeps — row security, the permissive-policy rule, the
/// grant matrix, snake_case, foreign-key indexing, the <c>WITH CHECK</c> census —
/// enumerate the catalogue and therefore already cover these four tables through
/// <see cref="SchemaFixture"/>. What they cannot cover is a <b>predicate</b>: a
/// partial index's <c>WHERE</c> clause is invisible to every one of them, and the
/// index goes on existing whatever it says.
/// </para>
/// <para>
/// These cases are for the predicate. The two definition tables carry
/// <c>UNIQUE (tenant_id, key) WHERE status = 'Active' AND deleted_at IS NULL</c>,
/// and each of the three terms fails differently: drop <c>status</c> and a second
/// revision of any key becomes unpublishable; drop <c>deleted_at</c> and a
/// soft-deleted definition holds its key against the tenant forever; drop the
/// uniqueness itself and two live definitions answer to one name. Only the middle
/// one is silent — the other two announce themselves the first time anyone
/// publishes.
/// </para>
/// </remarks>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class CustomizationSchemaTests
{
    private readonly SchemaFixture _schema;

    public CustomizationSchemaTests(SchemaFixture schema) => _schema = schema;

    /// <summary>
    /// The <c>INSERT</c> each definition table takes, by table name.
    /// </summary>
    /// <remarks>
    /// Hand-written per table because the column lists genuinely differ —
    /// <c>tenant_content_types</c> carries the document and its renderer — and
    /// both go through the same <c>MapDefinition</c> predicate, which is what
    /// makes one theory over the pair the right shape.
    /// </remarks>
    private static readonly Dictionary<string, string> Insert = new(StringComparer.Ordinal)
    {
        ["tenant_content_types"] =
            """
            INSERT INTO tenant_content_types
                (id, tenant_id, key, schema_version, schema_revision, status, display_name,
                 json_schema, renderer_key, created_at, created_by, row_version)
            VALUES (uuidv7(), @tenant, @key, @version, 0, @status, '{"en":"probe"}',
                    '{"type":"object"}', 'default-card', now(), @actor, 0)
            """,
        ["tenant_level_taxonomies"] =
            """
            INSERT INTO tenant_level_taxonomies
                (id, tenant_id, key, schema_version, schema_revision, status, display_name,
                 created_at, created_by, row_version)
            VALUES (uuidv7(), @tenant, @key, @version, 0, @status, '{"en":"probe"}',
                    now(), @actor, 0)
            """,
    };

    public static TheoryData<string> DefinitionTables()
    {
        var data = new TheoryData<string>();

        foreach (var table in Insert.Keys)
        {
            data.Add(table);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(DefinitionTables))]
    public async Task ASoftDeletedDefinitionReleasesItsKeyToItsSuccessor(string table)
    {
        // `SoftDelete` stamps deleted_at and deleted_by and does NOT touch Status —
        // measured, and it is the whole reason the index needs the second term. A
        // definition retired while live therefore stays 'Active' forever, so
        // without `deleted_at IS NULL` it holds (tenant_id, key) against its own
        // tenant and the successor it was retired to make room for can never be
        // published. Nothing else in the suite can see that: the index exists
        // either way, so every structural sweep stays green.
        //
        // Each revision takes its own schema_version, because
        // ux_<table>_tenant_id_key_schema_version is a TOTAL unique constraint —
        // a version number is never re-issued, soft delete or not (ADR-0013).
        //
        // One transaction, rolled back: the GUC assignment is transaction-local and
        // the fixture's row counts are left as the other cases expect them.
        const string Key = "probe-successor";

        await using var connection = await PostgresFixture.OpenAsync(_schema.Postgres.AppConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();

        await SchemaQueries.SetTenantAsync(connection, transaction, SchemaFixture.TenantA);

        await SchemaQueries.ExecuteAsync(connection, transaction, Insert[table],
            ("tenant", SchemaFixture.TenantA), ("key", Key), ("version", 1),
            ("status", "Active"), ("actor", SchemaFixture.Actor));

        await SchemaQueries.ExecuteAsync(connection, transaction,
            $"UPDATE {table} SET deleted_at = now(), deleted_by = @actor WHERE key = @key",
            ("actor", SchemaFixture.Actor), ("key", Key));

        var republish = async () => await SchemaQueries.ExecuteAsync(connection, transaction, Insert[table],
            ("tenant", SchemaFixture.TenantA), ("key", Key), ("version", 2),
            ("status", "Active"), ("actor", SchemaFixture.Actor));

        await republish.Should().NotThrowAsync(
            "a soft-deleted definition has released its key, and its successor is what "
            + "the retirement was for");

        // The other half, and without it this case passes with the uniqueness
        // dropped entirely: nothing above ever asks for a CONFLICT. A LIVE
        // definition must still block a second one under the same key — that index
        // is the only thing holding one-live-revision-per-concept across two
        // concurrent transactions, where an in-memory check on two aggregates both
        // pass.
        var second = async () => await SchemaQueries.ExecuteAsync(connection, transaction, Insert[table],
            ("tenant", SchemaFixture.TenantA), ("key", Key), ("version", 3),
            ("status", "Active"), ("actor", SchemaFixture.Actor));

        (await second.Should().ThrowAsync<PostgresException>(
            "a key that is live for a tenant admits exactly one Active definition"))
            .Which.SqlState.Should().Be("23505");
    }

    [Theory]
    [MemberData(nameof(DefinitionTables))]
    public async Task ADeprecatedDefinitionDoesNotHoldTheKeyAgainstALiveOne(string table)
    {
        // The first term of the same predicate. `UNIQUE (tenant_id, key)` without
        // `status = 'Active'` would reject the second revision of any key at all,
        // which is the constraint that made the first breaking change ADR-0013
        // requires impossible — so this is the case that fails if someone
        // "simplifies" the index by dropping its filter.
        const string Key = "probe-deprecated";

        await using var connection = await PostgresFixture.OpenAsync(_schema.Postgres.AppConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();

        await SchemaQueries.SetTenantAsync(connection, transaction, SchemaFixture.TenantA);

        await SchemaQueries.ExecuteAsync(connection, transaction, Insert[table],
            ("tenant", SchemaFixture.TenantA), ("key", Key), ("version", 1),
            ("status", "Deprecated"), ("actor", SchemaFixture.Actor));

        var succeed = async () => await SchemaQueries.ExecuteAsync(connection, transaction, Insert[table],
            ("tenant", SchemaFixture.TenantA), ("key", Key), ("version", 2),
            ("status", "Active"), ("actor", SchemaFixture.Actor));

        await succeed.Should().NotThrowAsync(
            "a deprecated revision is history, not a claim on the key");
    }
}
