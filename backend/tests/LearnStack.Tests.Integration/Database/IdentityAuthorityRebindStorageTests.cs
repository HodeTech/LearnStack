using FluentAssertions;
using LearnStack.SharedKernel.Identifiers;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// The storage contract of <c>learnstack_identity_rebind</c> against real PostgreSQL (ADR-0104 § 2, § 7, § 8,
/// Database Standards § Controlled issuer rebind storage).
/// </summary>
/// <remarks>
/// Table-level views do not show column grants, so every privilege is read from the column view, and every
/// refusal is exercised as the role itself rather than asserted from the grant text.
/// </remarks>
[Collection(PublicReadTestGroup.Name)]
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class IdentityAuthorityRebindStorageTests(PublicReadFixture parent)
{
    private const string Sentinel = "00000000-0000-7000-8000-000000000002";
    private const string NewIssuer = "https://new.example.test/realms/learnstack";

    [Fact]
    public async Task The_Role_Holds_Exactly_The_Declared_Column_Privileges_And_Nothing_Else()
    {
        await using var proof = await Proof.CreateAsync(parent);
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(proof.MigrationConnectionString);

        var columns = await ReadAsync(owner, """
            SELECT table_name || '.' || column_name || ' ' || privilege_type
            FROM information_schema.column_privileges
            WHERE grantee = 'learnstack_identity_rebind' AND table_schema = 'public'
              AND table_name NOT IN ('audit_log')
            ORDER BY 1
            """);
        columns.Should().Equal(
            "identity_authority.authority_id SELECT",
            "identity_authority.id SELECT",
            "identity_authority.issuer SELECT",
            "identity_authority.issuer UPDATE",
            "users.authority_id SELECT",
            "users.provider_subject SELECT",
            "users.status SELECT",
            "users.subject_key SELECT");

        var audit = await ReadAsync(owner, """
            SELECT DISTINCT privilege_type FROM information_schema.column_privileges
            WHERE grantee = 'learnstack_identity_rebind' AND table_schema = 'public' AND table_name = 'audit_log'
            """);
        audit.Should().Equal(["INSERT"], "the role writes audit rows and reads, changes and removes none");

        var auditColumns = await ReadAsync(owner, """
            SELECT count(*)::text FROM information_schema.column_privileges p
            JOIN information_schema.columns c USING (table_schema, table_name, column_name)
            WHERE p.grantee = 'learnstack_identity_rebind' AND p.table_name = 'audit_log' AND p.privilege_type = 'INSERT'
            """);
        var allAuditColumns = await ReadAsync(owner, """
            SELECT count(*)::text FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'audit_log'
            """);
        auditColumns.Should().Equal(allAuditColumns, "the policy, not the column list, bounds the audit row");

        var beyond = await ReadAsync(owner, """
            SELECT 'function ' || p.oid::regprocedure::text FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
              WHERE n.nspname = 'public'
                AND EXISTS (SELECT 1 FROM aclexplode(COALESCE(p.proacl, acldefault('f', p.proowner))) a
                            WHERE a.grantee = 'learnstack_identity_rebind'::regrole)
            UNION ALL SELECT 'schema create' WHERE has_schema_privilege('learnstack_identity_rebind', 'public', 'CREATE')
            UNION ALL SELECT 'database create' WHERE has_database_privilege('learnstack_identity_rebind', current_database(), 'CREATE')
            UNION ALL SELECT 'sequence ' || c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
              WHERE n.nspname = 'public' AND c.relkind = 'S'
                AND CASE WHEN c.relkind = 'S'
                         THEN has_sequence_privilege('learnstack_identity_rebind', c.oid, 'USAGE,SELECT,UPDATE') END
            """);
        beyond.Should().BeEmpty("no function, schema CREATE, database CREATE or sequence privilege is granted to the role itself");
    }

    [Fact]
    public async Task The_Role_Changes_The_Issuer_And_Nothing_Else_On_The_Authority_Row()
    {
        await using var proof = await Proof.CreateAsync(parent);

        (await proof.Rebind.ScalarAsync<string>("SELECT issuer FROM public.identity_authority WHERE id = 1"))
            .Should().Be(proof.OldIssuer);

        (await proof.Rebind.RefusalAsync("UPDATE public.identity_authority SET authority_id = uuidv7() WHERE id = 1"))
            .Should().Be("42501", "no column grant covers the stable key");
        (await proof.Rebind.RefusalAsync("UPDATE public.identity_authority SET id = 2 WHERE id = 1"))
            .Should().Be("42501", "no column grant covers the singleton id");
        (await proof.Rebind.RefusalAsync("DELETE FROM public.identity_authority")).Should().Be("42501");
        (await proof.Rebind.RefusalAsync("INSERT INTO public.identity_authority (id, authority_id, issuer) VALUES (2, uuidv7(), 'https://x.example.test/realms/learnstack')"))
            .Should().Be("42501");
        (await proof.Rebind.RefusalAsync("TRUNCATE public.identity_authority")).Should().Be("42501");
        (await proof.Rebind.RefusalAsync($"UPDATE public.identity_authority SET issuer = '{NewIssuer}' WHERE id = 1"))
            .Should().BeNull("the one admitted change");

        (await proof.ReadIssuerAsync()).Should().Be(NewIssuer);
        (await proof.ReadAuthorityAsync()).Should().Be(proof.AuthorityId, "the stable key is untouched");
    }

    [Fact]
    public async Task No_Other_Role_Can_Change_The_Issuer()
    {
        await using var proof = await Proof.CreateAsync(parent);

        foreach (var (name, connectionString) in new (string, string)[]
        {
            ("learnstack_app", proof.AppConnectionString),
            ("learnstack_platform", proof.PlatformConnectionString),
            ("learnstack_outbox_admin", proof.OutboxConnectionString),
        })
        {
            await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(connectionString);
            await using var command = new NpgsqlCommand(
                $"UPDATE public.identity_authority SET issuer = '{NewIssuer}' WHERE id = 1", connection);
            var act = async () => await command.ExecuteNonQueryAsync();
            (await act.Should().ThrowAsync<PostgresException>(name)).Which.SqlState.Should().Be("42501", name);
        }

        // The owner holds the privilege and no policy admits it: the row security filters its target away.
        await using (var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(proof.MigrationConnectionString))
        {
            await using var command = new NpgsqlCommand(
                $"UPDATE public.identity_authority SET issuer = '{NewIssuer}' WHERE id = 1", owner);
            (await command.ExecuteNonQueryAsync()).Should().Be(0, "no policy admits an owner update");
        }

        (await proof.ReadIssuerAsync()).Should().Be(proof.OldIssuer);
    }

    [Fact]
    public async Task The_Key_Trigger_Refuses_A_Change_Of_The_Id_Or_The_Authority_Id_Whoever_Holds_The_Grant()
    {
        await using var proof = await Proof.CreateAsync(parent);
        await using (var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(proof.MigrationConnectionString))
        {
            await IdentityMembershipFixture.ExecuteAsync(owner, null,
                "GRANT UPDATE (id, authority_id) ON public.identity_authority TO learnstack_identity_rebind");
        }

        // The trigger is the second line behind the missing column grant, so it is proved with the grant present.
        (await proof.Rebind.RefusalAsync("UPDATE public.identity_authority SET authority_id = uuidv7() WHERE id = 1"))
            .Should().Be("23514");
        (await proof.Rebind.RefusalAsync("UPDATE public.identity_authority SET id = 2 WHERE id = 1")).Should().Be("23514");
        (await proof.Rebind.RefusalAsync($"UPDATE public.identity_authority SET issuer = '{NewIssuer}' WHERE id = 1"))
            .Should().BeNull("an issuer change is not a key change");

        (await proof.ReadAuthorityAsync()).Should().Be(proof.AuthorityId);
    }

    [Fact]
    public async Task The_Role_Reads_Four_User_Columns_Without_Any_Announcement_And_Writes_None()
    {
        await using var proof = await Proof.CreateAsync(parent);

        (await proof.Rebind.ScalarAsync<long>("SELECT count(*) FROM public.users WHERE authority_id = @authority", ("authority", proof.AuthorityId)))
            .Should().Be(2, "the continuity proof reads every user of the stored authority");
        (await proof.Rebind.ScalarAsync<long>(
            "SELECT count(*) FROM public.users WHERE status = 'Active' AND provider_subject IS NOT NULL AND octet_length(subject_key) = 32"))
            .Should().Be(2);

        foreach (var statement in new[]
        {
            "SELECT id FROM public.users",
            "SELECT creation_audit_id FROM public.users",
            "INSERT INTO public.users (id, authority_id, subject_key, provider_subject, status) VALUES (uuidv7(), uuidv7(), '\\x00', 'x', 'Active')",
            "UPDATE public.users SET status = 'Closed'",
            "DELETE FROM public.users",
        })
        {
            (await proof.Rebind.RefusalAsync(statement)).Should().Be("42501", statement);
        }

        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(proof.AppConnectionString);
        await using var control = new NpgsqlCommand("SELECT count(*) FROM public.users", app);
        (await control.ExecuteScalarAsync()).Should().Be(0L, "the control: the application role without its announcement sees no user");
    }

    [Fact]
    public async Task The_Role_Inserts_The_Two_Rebind_Rows_And_Nothing_Else_Into_The_Audit_Log()
    {
        await using var proof = await Proof.CreateAsync(parent);

        (await proof.Rebind.RefusalAsync(AuditInsert(Row()))).Should().BeNull("the intent row");
        (await proof.Rebind.RefusalAsync(AuditInsert(Row(operation: "identity.authority.rebind", outcome: "success"))))
            .Should().BeNull("the result row");

        await using (var platform = (NpgsqlConnection)await PostgresFixture.OpenAsync(proof.PlatformConnectionString))
        {
            await using var count = new NpgsqlCommand(
                $"SELECT count(*) FROM public.audit_log WHERE tenant_id = '{Sentinel}' AND module = 'identity' AND operation LIKE 'identity.authority.rebind%'",
                platform);
            (await count.ExecuteScalarAsync()).Should().Be(2L);
        }

        var refused = new (string Why, string Insert)[]
        {
            ("a real tenant", AuditInsert(Row(tenant: Guid.CreateVersion7().ToString("D")))),
            ("an organization", AuditInsert(Row(organization: Guid.CreateVersion7().ToString("D")))),
            ("another module", AuditInsert(Row(module: "tenancy"))),
            ("the result slug with the intent outcome", AuditInsert(Row(operation: "identity.authority.rebind"))),
            ("the intent slug with the result outcome", AuditInsert(Row(outcome: "success"))),
            ("a failed outcome", AuditInsert(Row(outcome: "failed"))),
            ("a Should row", AuditInsert(Row(operationClass: "Should"))),
            ("another operation type", AuditInsert(Row(operationType: "Create"))),
            ("another entity type", AuditInsert(Row(entityType: "User"))),
            ("another reason", AuditInsert(Row(reason: "host_map"))),
            ("an actor", AuditInsert(Row(actor: Guid.CreateVersion7().ToString("D")))),
            ("a mail address", AuditInsert(Row(email: "operator@example.test"))),
            ("no operator attribution", AuditInsert(Row(metadata: "{}"))),
        };
        foreach (var (why, insert) in refused)
        {
            (await proof.Rebind.RefusalAsync(insert)).Should().Be("42501", why);
        }

        // The creation witness guard of ADR-0072 refuses the one operation it governs, and the policy would refuse it too.
        (await proof.Rebind.RefusalAsync(AuditInsert(Row(operation: "identity.user.create"))))
            .Should().Be("23514", "the global creation guard answers before the policy");
        (await proof.Rebind.RefusalAsync(AuditInsert(Row(operation: "identity.authority.rebind_other"))))
            .Should().Be("42501", "another operation of the identity module");

        foreach (var statement in new[]
        {
            "SELECT count(*) FROM public.audit_log",
            "UPDATE public.audit_log SET reason = 'x'",
            "DELETE FROM public.audit_log",
            "TRUNCATE public.audit_log",
        })
        {
            (await proof.Rebind.RefusalAsync(statement)).Should().Be("42501", statement);
        }
    }

    [Fact]
    public async Task The_Application_And_Ingress_Roles_Cannot_Write_The_Rebind_Rows()
    {
        await using var proof = await Proof.CreateAsync(parent);

        // The application role announces no tenant here and the sentinel guard of ADR-0072 refuses its attribution
        // before the policy; the ingress role holds no audit privilege at all.
        foreach (var (name, connectionString, expected) in new (string, string, string)[]
        {
            ("learnstack_app", proof.AppConnectionString, "23514"),
            ("learnstack_identity_ingress", parent.Postgres.IdentityIngressConnectionString, "42501"),
        })
        {
            await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(connectionString);
            await using var transaction = await connection.BeginTransactionAsync();
            await using var command = new NpgsqlCommand(AuditInsert(Row()), connection, transaction);
            var act = async () => await command.ExecuteNonQueryAsync();
            (await act.Should().ThrowAsync<PostgresException>(name)).Which.SqlState.Should().Be(expected, name);
        }
    }

    private static async Task<string[]> ReadAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0));
        }

        return [.. rows];
    }

    private static string Row(
        string tenant = Sentinel, string? organization = null, string module = "identity",
        string operation = "identity.authority.rebind_attempt", string operationType = "SecurityEvent",
        string operationClass = "Must", string entityType = "IdentityAuthority", string outcome = "indeterminate",
        string reason = "issuer_rebind", string? actor = null, string? email = null,
        string metadata = """{"operator":{"issuer":"https://operator.example.test/realms/learnstack-hub"}}""") =>
        string.Join('\u001f', tenant, organization ?? string.Empty, module, operation, operationType, operationClass,
            entityType, outcome, reason, actor ?? string.Empty, email ?? string.Empty, metadata);

    private static string AuditInsert(string row)
    {
        var f = row.Split('\u001f');
        string Nullable(string value) => value.Length == 0 ? "NULL" : $"'{value}'";
        return $"""
            INSERT INTO public.audit_log
                (id, tenant_id, organization_id, actor_user_id, actor_email, module, operation, operation_type,
                 operation_class, entity_type, entity_id, outcome, reason, metadata, timestamp)
            VALUES (uuidv7(), '{f[0]}', {Nullable(f[1])}, {Nullable(f[9])}, {Nullable(f[10])}, '{f[2]}', '{f[3]}', '{f[4]}',
                    '{f[5]}', '{f[6]}', 'authority', '{f[7]}', '{f[8]}', '{f[11]}'::jsonb, now())
            """;
    }

    /// <summary>An installed authority, two bound users and a role session on its own connection each time.</summary>
    private sealed class Proof : IAsyncDisposable
    {
        private readonly IdentityPublicReadFixture _database;

        private Proof(IdentityPublicReadFixture database) => _database = database;

        internal string OldIssuer => _database.Registration.Issuer;
        internal Guid AuthorityId => _database.Registration.AuthorityId.Value;
        internal string MigrationConnectionString => _database.MigrationConnectionString;
        internal string AppConnectionString => _database.AppConnectionString;
        internal string PlatformConnectionString => _database.PlatformConnectionString;
        internal string OutboxConnectionString => _database.OutboxConnectionString;
        internal RoleSession Rebind => new(_database.IdentityRebindConnectionString);

        internal static async Task<Proof> CreateAsync(PublicReadFixture parent)
        {
            var first = UserId.From(Guid.CreateVersion7());
            var second = UserId.From(Guid.CreateVersion7());
            var database = await IdentityPublicReadFixture.CreateAsync(
                parent, "https://old.example.test/realms/learnstack",
                new Dictionary<UserId, string>
                {
                    [first] = "rebind-storage-" + first.Value.ToString("N"),
                    [second] = "rebind-storage-" + second.Value.ToString("N"),
                });
            return new Proof(database);
        }

        internal async Task<string> ReadIssuerAsync() => await ReadAsApplicationAsync<string>("SELECT issuer FROM public.identity_authority");

        internal async Task<Guid> ReadAuthorityAsync() => await ReadAsApplicationAsync<Guid>("SELECT authority_id FROM public.identity_authority");

        // Read through the application role, whose policy admits the one authority row.
        private async Task<T> ReadAsApplicationAsync<T>(string sql)
        {
            await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.AppConnectionString);
            await using var command = new NpgsqlCommand(sql, connection);
            return (T)(await command.ExecuteScalarAsync())!;
        }

        public ValueTask DisposeAsync() => _database.DisposeAsync();
    }

    private sealed class RoleSession(string connectionString)
    {
        /// <summary>The SQLSTATE the statement raises as the role, or null when it succeeds.</summary>
        internal async Task<string?> RefusalAsync(string sql)
        {
            await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(connectionString);
            await using var transaction = await connection.BeginTransactionAsync();
            try
            {
                await using var command = new NpgsqlCommand(sql, connection, transaction);
                await command.ExecuteNonQueryAsync();
                await transaction.CommitAsync();
                return null;
            }
            catch (PostgresException failure)
            {
                return failure.SqlState;
            }
        }

        internal async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] values)
        {
            await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(connectionString);
            await using var command = new NpgsqlCommand(sql, connection);
            foreach (var (name, value) in values)
            {
                command.Parameters.Add(new NpgsqlParameter(name, value is Guid ? NpgsqlDbType.Uuid : NpgsqlDbType.Text) { Value = value });
            }

            return (T)(await command.ExecuteScalarAsync())!;
        }
    }
}
