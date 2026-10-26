using System.Text.RegularExpressions;
using FluentAssertions;
using LearnStack.Modules.Identity.Infrastructure.Persistence.Migrations;
using LearnStack.SharedKernel.Identity;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class GlobalIdentitySchemaTests
{
    internal static IReadOnlyDictionary<string, string> WithCheckProofs { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["users"] = nameof(Global_User_INSERT_refuses_wrong_or_absent_subject_scope),
        ["identity_authority"] = nameof(Authority_anchor_writes_require_the_deployment_owner_and_singleton_installation),
    };

    [Fact]
    public async Task Global_User_INSERT_refuses_wrong_or_absent_subject_scope()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(fixture.Postgres);
        await InstallAuthorityAsync(database.MigrationConnectionString);
        var own = NewSubject();
        await InsertPairAsync(database.AppConnectionString, own);
        foreach (var missing in new[] { false, true })
        {
            await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
            await using var transaction = await app.BeginTransactionAsync();
            (await ScalarAsync<bool>(app, transaction, """
                SELECT bool_and(has_column_privilege(current_user,'public.users',column_name,'INSERT'))
                FROM unnest(ARRAY['id','authority_id','subject_key','provider_subject','status','creation_audit_id',
                    'creation_audit_timestamp','created_at','created_by','row_version']) AS column_name
                """)).Should().BeTrue();
            if (missing) await ClearAsync(app, transaction);
            else await AnnounceAsync(app, transaction, own);
            var insert = () => InsertUserAsync(app, transaction, NewSubject(), Guid.CreateVersion7(), Guid.CreateVersion7(), DateTimeOffset.UtcNow);
            (await insert.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501",
                "the initial row must fail self WITH CHECK before any deferred creation witness can run");
            await transaction.RollbackAsync();
        }
        await using var read = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var scope = await read.BeginTransactionAsync();
        await AnnounceAsync(read, scope, own);
        (await ScalarAsync<long>(read, scope, "SELECT count(*) FROM public.users")).Should().Be(1);
    }

    [Fact]
    public async Task Authority_anchor_writes_require_the_deployment_owner_and_singleton_installation()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(fixture.Postgres);
        var appInstall = () => InstallAuthorityAsync(database.AppConnectionString);
        (await appInstall.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501");
        await using (var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString))
            await ExecuteAsync(owner, null, "GRANT INSERT ON public.identity_authority TO learnstack_app");
        await using (var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString))
            (await ScalarAsync<bool>(app, null, "SELECT has_table_privilege(current_user,'public.identity_authority','INSERT')")).Should().BeTrue();
        // The test-only grant is committed on this isolated database. A valid
        // first singleton row must now be refused by WITH CHECK itself.
        (await appInstall.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501");
        await InstallAuthorityAsync(database.MigrationConnectionString);
        await using (var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString))
        {
            (await ScalarAsync<long>(app, null, "SELECT count(*) FROM public.identity_authority")).Should().Be(1);
            foreach (var statement in new[] { "UPDATE public.identity_authority SET issuer=issuer", "DELETE FROM public.identity_authority", "TRUNCATE public.identity_authority" })
            {
                var mutation = () => ExecuteAsync(app, null, statement);
                (await mutation.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501");
            }
        }
    }

    [Fact]
    public async Task Applied_function_bodies_signatures_privileges_and_trigger_callers_match_the_compiled_migration()
    {
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.Postgres.MigrationConnectionString);
        (await FunctionViolationsAsync(owner)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("body", "fn_identity_audit_evidence:body")]
    [InlineData("privilege", "fn_identity_audit_evidence:public-execute")]
    [InlineData("caller", "fn_identity_user_creation_audit_guard:trigger-callers")]
    [InlineData("extra-definer", "public:definer-census")]
    public async Task Applied_function_inspector_rejects_committed_widening_and_a_clean_twin_passes(
        string fault, string expectedViolation)
    {
        await using var clean = await DisposableSchemaDatabase.CreateAsync(fixture.Postgres);
        await using var dirty = await DisposableSchemaDatabase.CreateAsync(fixture.Postgres);
        await using (var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(dirty.MigrationConnectionString))
        {
            (await FunctionViolationsAsync(owner)).Should().BeEmpty();
            var definition = CompiledFunctions().Single(function => function.Name == "fn_identity_audit_evidence");
            var mutation = fault switch
            {
                "body" => definition.Definition.Replace("CREATE FUNCTION", "CREATE OR REPLACE FUNCTION", StringComparison.Ordinal)
                    .Replace(definition.Body, "\nBEGIN RETURN true; END\n", StringComparison.Ordinal),
                "privilege" => "GRANT EXECUTE ON FUNCTION public.fn_identity_audit_evidence(uuid,timestamptz,uuid,bytea,uuid,text,text) TO PUBLIC",
                "caller" => "DROP TRIGGER tg_users_creation_audit ON public.users",
                "extra-definer" => """
                    CREATE FUNCTION public.fn_unrelated_definer_probe() RETURNS boolean
                    LANGUAGE sql SECURITY DEFINER SET search_path=pg_catalog AS $$ SELECT true $$;
                    REVOKE ALL ON FUNCTION public.fn_unrelated_definer_probe() FROM PUBLIC;
                    """,
                _ => throw new InvalidOperationException("Unknown planted function fault."),
            };
            // No surrounding transaction: the fault commits before independent connections inspect it.
            await ExecuteAsync(owner, null, mutation);
        }

        await using (var observer = (NpgsqlConnection)await PostgresFixture.OpenAsync(dirty.MigrationConnectionString))
            (await FunctionViolationsAsync(observer)).Should().Contain(expectedViolation);
        await using (var observer = (NpgsqlConnection)await PostgresFixture.OpenAsync(clean.MigrationConnectionString))
            (await FunctionViolationsAsync(observer)).Should().BeEmpty();

        if (fault == "body")
        {
            var subject = NewSubject();
            (await MissingEvidenceAsync(clean.AppConnectionString, subject)).Should().BeFalse();
            (await MissingEvidenceAsync(dirty.AppConnectionString, subject)).Should().BeTrue(
                "the committed replacement really widens the app-callable evidence result");
        }
        if (fault == "caller")
        {
            await InstallAuthorityAsync(clean.MigrationConnectionString);
            await InstallAuthorityAsync(dirty.MigrationConnectionString);
            var orphan = () => InsertOrphanAsync(clean.AppConnectionString, NewSubject());
            (await orphan.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514");
            await InsertOrphanAsync(dirty.AppConnectionString, NewSubject());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Independent_app_probe_detects_a_committed_self_RLS_widening(bool widened)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(fixture.Postgres);
        await InstallAuthorityAsync(database.MigrationConnectionString);
        var own = NewSubject();
        var foreign = NewSubject();
        await InsertPairAsync(database.AppConnectionString, own);
        var foreignPair = await InsertPairAsync(database.AppConnectionString, foreign);
        if (widened)
        {
            await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString);
            await ExecuteAsync(owner, null, "ALTER POLICY users_isolation ON public.users USING (true)");
        }

        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await AnnounceAsync(app, transaction, own);
        // Raw SQL deliberately omits EF's independent filter: it must observe the RLS fault.
        await using var read = new NpgsqlCommand("SELECT count(*) FROM public.users WHERE id=@foreign", app, transaction);
        read.Parameters.AddWithValue("foreign", foreignPair.Target);
        ((long)(await read.ExecuteScalarAsync())!).Should().Be(widened ? 1 : 0);
        (await ScalarAsync<long>(app, transaction, "SELECT count(*) FROM public.users")).Should().Be(widened ? 2 : 1,
            "the clean twin must see its own row, so an empty table or missing SELECT grant cannot mask the boundary");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Owner_DELETE_probe_detects_guard_removal_while_app_delete_stays_denied(bool removed)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(fixture.Postgres);
        await InstallAuthorityAsync(database.MigrationConnectionString);
        var subject = NewSubject();
        var pair = await InsertPairAsync(database.AppConnectionString, subject);
        if (removed)
        {
            await using var setup = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString);
            await ExecuteAsync(setup, null, "DROP TRIGGER tg_users_no_delete ON public.users");
        }

        await using (var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString))
            (await ScalarAsync<bool>(app, null, "SELECT has_table_privilege(current_user,'public.users','DELETE')")).Should().BeFalse();
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        (await ScalarAsync<long>(owner, null, "SELECT count(*) FROM public.users")).Should().Be(1,
            "the owner arm must expose an actual target before the mutation probe");
        await using var delete = new NpgsqlCommand("DELETE FROM public.users WHERE id=@target", owner);
        delete.Parameters.AddWithValue("target", pair.Target);
        if (removed)
        {
            (await delete.ExecuteNonQueryAsync()).Should().Be(1);
            (await ScalarAsync<long>(owner, null, "SELECT count(*) FROM public.users")).Should().Be(0);
        }
        else
        {
            var mutate = () => delete.ExecuteNonQueryAsync();
            (await mutate.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514");
            (await ScalarAsync<long>(owner, null, "SELECT count(*) FROM public.users")).Should().Be(1);
        }
    }

    private static async Task<IReadOnlyList<string>> FunctionViolationsAsync(NpgsqlConnection owner)
    {
        var expected = CompiledFunctions();
        var issues = new List<string>();
        var actualDefiners = await SchemaQueries.ReadStringsAsync(owner, """
            SELECT p.proname FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
            WHERE n.nspname='public' AND p.prosecdef ORDER BY p.proname
            """, null);
        if (!actualDefiners.SequenceEqual(expected.Where(function => function.Definer).Select(function => function.Name).Order(StringComparer.Ordinal)))
            issues.Add("public:definer-census");

        foreach (var function in expected)
        {
            await using var command = new NpgsqlCommand("""
                SELECT p.prosrc,pg_get_function_result(p.oid),p.prosecdef,p.proretset,l.lanname,r.rolname,r.rolbypassrls,
                    p.provolatile::text,COALESCE(p.proconfig,ARRAY[]::text[]),pg_get_function_identity_arguments(p.oid),
                    p.pronargdefaults,has_function_privilege('learnstack_app',p.oid,'EXECUTE'),
                    EXISTS(SELECT 1 FROM aclexplode(COALESCE(p.proacl,acldefault('f',p.proowner))) a WHERE a.grantee=0 AND a.privilege_type='EXECUTE'),
                    ARRAY(SELECT (CASE WHEN a.grantee=0 THEN 'PUBLIC' ELSE pg_get_userbyid(a.grantee)::text END)
                        || CASE WHEN a.is_grantable THEN ':grant-option' ELSE '' END
                        FROM aclexplode(COALESCE(p.proacl,acldefault('f',p.proowner))) a
                        WHERE a.grantee<>p.proowner AND a.privilege_type='EXECUTE' ORDER BY a.grantee),
                    p.proisstrict,p.proparallel::text,p.proleakproof
                FROM pg_proc p JOIN pg_roles r ON r.oid=p.proowner JOIN pg_language l ON l.oid=p.prolang
                WHERE p.oid=to_regprocedure(@signature)
                """, owner);
            command.Parameters.AddWithValue("signature", function.Signature);
            await using (var reader = await command.ExecuteReaderAsync())
            {
                if (!await reader.ReadAsync()) { issues.Add(function.Name + ":missing-signature"); continue; }
                if (reader.GetString(0).Trim() != function.Body.Trim()) issues.Add(function.Name + ":body");
                if (reader.GetString(1) != function.ReturnType || reader.GetBoolean(3)) issues.Add(function.Name + ":return-type");
                if (reader.GetBoolean(2) != function.Definer || reader.GetString(4) != function.Language
                    || reader.GetString(5) != "learnstack_migration" || reader.GetBoolean(6)) issues.Add(function.Name + ":execution-owner");
                if (reader.GetString(7) != function.Volatility || !reader.GetFieldValue<string[]>(8).SequenceEqual(["search_path=pg_catalog"]))
                    issues.Add(function.Name + ":execution-settings");
                if (reader.GetString(9) != function.IdentityArguments || reader.GetInt16(10) != 0) issues.Add(function.Name + ":arguments");
                if (reader.GetBoolean(11) != function.AppCallable) issues.Add(function.Name + ":app-execute");
                if (reader.GetBoolean(12)) issues.Add(function.Name + ":public-execute");
                var permittedGrantees = function.AppCallable ? new[] { "learnstack_app" } : [];
                if (!reader.GetFieldValue<string[]>(13).SequenceEqual(permittedGrantees)) issues.Add(function.Name + ":execution-grantees");
                if (reader.GetBoolean(14) != function.Strict || reader.GetString(15) != "u" || reader.GetBoolean(16))
                    issues.Add(function.Name + ":execution-properties");
            }
            var actualCallers = new List<TriggerContract>();
            await using var triggers = new NpgsqlCommand("""
                SELECT t.tgname,n.nspname||'.'||c.relname,t.tgtype::integer,t.tgdeferrable,t.tginitdeferred,t.tgenabled::text
                FROM pg_trigger t JOIN pg_class c ON c.oid=t.tgrelid JOIN pg_namespace n ON n.oid=c.relnamespace
                WHERE t.tgfoid=to_regprocedure(@signature) AND NOT t.tgisinternal ORDER BY t.tgname
                """, owner);
            triggers.Parameters.AddWithValue("signature", function.Signature);
            await using (var reader = await triggers.ExecuteReaderAsync())
                while (await reader.ReadAsync())
                    actualCallers.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2),
                        reader.GetBoolean(3), reader.GetBoolean(4), reader.GetString(5)));
            if (!actualCallers.SequenceEqual(function.Triggers)) issues.Add(function.Name + ":trigger-callers");
        }
        return issues;
    }

    private static CanonicalFunction[] CompiledFunctions()
    {
        // This is the executed compiled Up operation, including the one central installer.
        // No copied witness DDL or checked-in expected-body snapshot can silently drift.
        var sql = string.Join("\n", new create_identity_schema().UpOperations
            .Concat(new add_contextual_identity().UpOperations).OfType<SqlOperation>().Select(operation => operation.Sql));
        var functions = FunctionDeclaration().Matches(sql).Select(match =>
        {
            var name = match.Groups["name"].Value;
            var parameters = match.Groups["parameters"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var types = parameters.Select(parameter => parameter[(parameter.IndexOf(' ') + 1)..]).ToArray();
            var triggers = TriggerDeclaration().Matches(sql).Where(trigger => trigger.Groups["function"].Value == name)
                .Select(trigger => new TriggerContract(trigger.Groups["name"].Value, "public." + trigger.Groups["table"].Value,
                    (trigger.Groups["attributes"].Value.Contains("FOR EACH ROW", StringComparison.Ordinal) ? 1 : 0)
                    | (trigger.Groups["timing"].Value == "BEFORE" ? 2 : 0)
                    | (trigger.Groups["events"].Value.Split(" OR ", StringSplitOptions.TrimEntries).Aggregate(0, (mask, command) => mask | (command switch { "INSERT" => 4, "DELETE" => 8, "UPDATE" => 16, "TRUNCATE" => 32, _ => 0 }))),
                    trigger.Groups["attributes"].Value.Contains("DEFERRABLE", StringComparison.Ordinal),
                    trigger.Groups["attributes"].Value.Contains("INITIALLY DEFERRED", StringComparison.Ordinal), "O"))
                .OrderBy(trigger => trigger.Name, StringComparer.Ordinal).ToArray();
            return new CanonicalFunction(name, "public." + name + "(" + string.Join(',', types) + ")",
                string.Join(", ", parameters).Replace("timestamptz", "timestamp with time zone", StringComparison.Ordinal),
                match.Groups["returns"].Value, match.Groups["attributes"].Value.Contains("SECURITY DEFINER", StringComparison.Ordinal),
                sql.Contains("GRANT EXECUTE ON FUNCTION public." + name + "(", StringComparison.Ordinal),
                match.Groups["body"].Value, match.Value, triggers,
                FunctionLanguage().Match(match.Groups["attributes"].Value).Groups[1].Value,
                match.Groups["attributes"].Value.Contains("IMMUTABLE", StringComparison.Ordinal) ? "i"
                    : match.Groups["attributes"].Value.Contains("STABLE", StringComparison.Ordinal) ? "s" : "v",
                match.Groups["attributes"].Value.Contains("STRICT", StringComparison.Ordinal));
        }).OrderBy(function => function.Name, StringComparer.Ordinal).ToArray();
        functions.Should().HaveCount(12, "the two applied Identity migrations install the complete global/contextual function set");
        functions.Count(function => function.Definer).Should().Be(5);
        functions.SelectMany(function => function.Triggers).Should().HaveCount(18);
        var globalNames = FunctionDeclaration().Matches(string.Join("\n", new create_identity_schema().UpOperations
                .OfType<SqlOperation>().Select(operation => operation.Sql)))
            .Select(match => match.Groups["name"].Value).ToHashSet(StringComparer.Ordinal);
        foreach (var function in functions)
        {
            var forbidden = globalNames.Contains(function.Name) ? ForbiddenFunctionBody() : ContextualForbiddenFunctionBody();
            forbidden.IsMatch(function.Body).Should().BeFalse(
                "the canonical body admits no dynamic SQL, authority setters, or row-returning reader");
        }
        return functions;
    }

    private static async Task InstallAuthorityAsync(string connectionString)
    {
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(connectionString);
        await using var insert = new NpgsqlCommand("INSERT INTO public.identity_authority(id,authority_id,issuer) VALUES(1,@authority,@issuer)", owner);
        insert.Parameters.AddWithValue("authority", GlobalIdentitySchemaFixture.Authority.Value);
        insert.Parameters.AddWithValue("issuer", GlobalIdentitySchemaFixture.Issuer);
        await insert.ExecuteNonQueryAsync();
    }

    private static async Task<bool> MissingEvidenceAsync(string connectionString, GlobalIdentitySubject subject)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(connectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await AnnounceAsync(app, transaction, subject);
        return await EvidenceAsync(app, transaction, subject, Guid.CreateVersion7(), Guid.CreateVersion7(),
            DateTimeOffset.UtcNow, "identity-bootstrap", "success");
    }

    private static async Task InsertOrphanAsync(string connectionString, GlobalIdentitySubject subject)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(connectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await AnnounceAsync(app, transaction, subject);
        await InsertUserAsync(app, transaction, subject, Guid.CreateVersion7(), Guid.CreateVersion7(), DateTimeOffset.UtcNow);
        await transaction.CommitAsync();
    }

    private sealed record CanonicalFunction(string Name, string Signature, string IdentityArguments, string ReturnType,
        bool Definer, bool AppCallable, string Body, string Definition, TriggerContract[] Triggers, string Language, string Volatility, bool Strict);
    private sealed record TriggerContract(string Name, string Table, int Type, bool Deferrable, bool InitiallyDeferred, string Enabled);

    [GeneratedRegex(@"CREATE FUNCTION public\.(?<name>[a-z0-9_]+)\((?<parameters>[^)]*)\)\s+RETURNS\s+(?<returns>[a-z]+)\s+(?<attributes>.*?)\bAS\s+(?<delimiter>\$[a-z0-9_]*\$)(?<body>.*?)\k<delimiter>;", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex FunctionDeclaration();
    [GeneratedRegex(@"LANGUAGE\s+(\w+)", RegexOptions.IgnoreCase)]
    private static partial Regex FunctionLanguage();
    [GeneratedRegex(@"CREATE (?<constraint>CONSTRAINT )?TRIGGER (?<name>\w+)\s+(?<timing>BEFORE|AFTER)\s+(?<events>(?:INSERT|UPDATE|DELETE|TRUNCATE)(?:\s+OR\s+(?:INSERT|UPDATE|DELETE|TRUNCATE))*) ON public\.(?<table>\w+)(?<attributes>.*?)EXECUTE FUNCTION public\.(?<function>\w+)\(\);", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TriggerDeclaration();
    [GeneratedRegex(@"\bEXECUTE\b|\bset_config\s*\(|\bRETURN\s+QUERY\b|\bSELECT\s+(?!1\b)", RegexOptions.IgnoreCase)]
    private static partial Regex ForbiddenFunctionBody();
    [GeneratedRegex(@"\bEXECUTE\b|\bset_config\s*\(|\bRETURN\s+QUERY\b|\bpg_(?:try_)?advisory_(?:xact_)?lock(?:_shared)?\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex ContextualForbiddenFunctionBody();
}
