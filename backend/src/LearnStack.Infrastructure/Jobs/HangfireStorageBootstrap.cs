using System.Data;
using System.Globalization;
using Hangfire;
using Hangfire.Logging;
using Hangfire.PostgreSql;
using LearnStack.Infrastructure.Persistence;
using Npgsql;
using Newtonsoft.Json;

namespace LearnStack.Infrastructure.Jobs;

/// <summary>Explicit Step 4 entry: validates durable storage before constructing the stock provider.</summary>
/// <remarks>Contract registration alone never invokes this helper or starts workers.</remarks>
public static class HangfireStorageBootstrap
{
    public static PostgreSqlStorage Create(PrivilegedDataSource source,
        BackgroundJobInfrastructureRegistry registry, HangfireBoundedLogProvider diagnostics)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ConfigureWireFormat();
        LogProvider.SetCurrentLogProvider(diagnostics);
        try
        {
            using var connection = source.OpenConnection();
            Verify(connection, registry);
            return new PostgreSqlStorage(new HangfireConnectionFactory(source), Options());
        }
        catch (Exception failure) when (failure is not HangfireStorageRefusedException)
        {
            // No provider/SQL text or exception object crosses this boundary.
            throw new HangfireStorageRefusedException();
        }
    }

    /// <summary>Pin the full stock invocation wire form, built-in primitive names and safe argument settings.</summary>
    internal static void ConfigureWireFormat() => GlobalConfiguration.Configuration
        .SetDataCompatibilityLevel(CompatibilityLevel.Version_110)
        .UseSimpleAssemblyNameTypeSerializer()
        .UseSerializerSettings(new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.None,
            MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
            MissingMemberHandling = MissingMemberHandling.Error,
            Culture = CultureInfo.InvariantCulture,
            MaxDepth = 8,
        });

    public static PostgreSqlStorageOptions Options() => new()
    {
        SchemaName = "public",
        PrepareSchemaIfNecessary = false,
        AllowDegradedModeWithoutStorage = false,
        StartupConnectionMaxRetries = 0,
        EnableTransactionScopeEnlistment = false,
        UseNativeDatabaseTransactions = true,
        UseSlidingInvisibilityTimeout = true,
        EnableLongPolling = false,
        QueuePollInterval = TimeSpan.FromMilliseconds(200),
        DistributedLockTimeout = TimeSpan.FromSeconds(5),
        TransactionSynchronisationTimeout = TimeSpan.FromSeconds(5),
    };

    private static void Verify(NpgsqlConnection connection, BackgroundJobInfrastructureRegistry registry)
    {
        if (connection.State != ConnectionState.Open) throw new HangfireStorageRefusedException();
        Require(connection, "SELECT count(*) = 1 AND min(version) = 23 AND max(version) = 23 FROM public.\"schema\"");
        Require(connection, """
            SELECT current_setting('statement_timeout')::interval > interval '0'
              AND current_setting('statement_timeout')::interval <= interval '5 seconds'
              AND current_setting('lock_timeout')::interval > interval '0'
              AND current_setting('lock_timeout')::interval <= interval '5 seconds'
              AND current_setting('idle_in_transaction_session_timeout')::interval > interval '0'
              AND current_setting('idle_in_transaction_session_timeout')::interval <= interval '5 seconds'
              AND NOT has_schema_privilege(current_user,'public','CREATE')
            """);
        HangfireSchemaShape.Verify(connection);
        foreach (var (table, columns) in HangfireStorageContract.Columns)
        {
            Require(connection, """
                SELECT c.relowner = 'learnstack_migration'::regrole AND NOT c.relrowsecurity AND NOT c.relforcerowsecurity
                  AND c.relkind = 'r'
                  AND NOT EXISTS (SELECT FROM aclexplode(coalesce(c.relacl,acldefault('r',c.relowner))) acl
                      WHERE acl.grantee NOT IN (c.relowner,'learnstack_outbox_admin'::regrole) OR (acl.grantee<>c.relowner AND acl.is_grantable))
                  AND NOT EXISTS (SELECT FROM pg_attribute a CROSS JOIN LATERAL aclexplode(a.attacl) acl
                      WHERE a.attrelid = c.oid AND (acl.grantee NOT IN (c.relowner,'learnstack_outbox_admin'::regrole)
                          OR (acl.grantee<>c.relowner AND acl.is_grantable)))
                  AND (SELECT array_agg(a.attname::text ORDER BY a.attname) FROM pg_attribute a
                       WHERE a.attrelid=c.oid AND a.attnum>0 AND NOT a.attisdropped) = @columns
                FROM pg_class c WHERE c.oid = @target::regclass
                """, ("target", $"public.\"{table}\""), ("columns", columns.Order(StringComparer.Ordinal).ToArray()));
            foreach (var privilege in new[] { "SELECT", "DELETE", "INSERT", "UPDATE", "REFERENCES", "TRIGGER", "TRUNCATE" })
                Require(connection, "SELECT has_table_privilege(current_user,@target,@privilege) = @expected",
                    ("target", $"public.\"{table}\""), ("privilege", privilege),
                    ("expected", privilege == "SELECT" || privilege == "DELETE" && HangfireStorageContract.Deletes(table)));
            foreach (var column in columns)
                foreach (var privilege in new[] { "INSERT", "UPDATE", "REFERENCES" })
                    Require(connection, "SELECT has_column_privilege(current_user,@target,@column,@privilege) = @expected",
                        ("target", $"public.\"{table}\""), ("column", column), ("privilege", privilege),
                        ("expected", privilege == "INSERT" && HangfireStorageContract.Inserts(table)
                            || privilege == "UPDATE" && HangfireStorageContract.Updates(table)));
        }
        foreach (var sequence in HangfireStorageContract.Sequences)
            Require(connection, """
                SELECT c.relowner = 'learnstack_migration'::regrole AND c.relkind='S'
                  AND has_sequence_privilege(current_user,c.oid,'USAGE')
                  AND NOT has_sequence_privilege(current_user,c.oid,'SELECT,UPDATE')
                  AND NOT EXISTS (SELECT FROM aclexplode(coalesce(c.relacl,acldefault('S',c.relowner))) acl
                      WHERE acl.grantee NOT IN (c.relowner,'learnstack_outbox_admin'::regrole) OR (acl.grantee<>c.relowner AND acl.is_grantable))
                FROM pg_class c WHERE c.oid = @target::regclass
                """, ("target", $"public.\"{sequence}\""));
        VerifyGuards(connection);
        VerifyMetadata(connection);
        if (registry.QueueNames.Count is < 3 or > 64) throw new HangfireStorageRefusedException();
    }

    private static void VerifyGuards(NpgsqlConnection connection)
    {
        var guards = HangfireStorageContract.Guards();
        if (guards.Count != 11) throw new HangfireStorageRefusedException();
        foreach (var (name, definition) in guards)
            Require(connection, """
                SELECT count(*) = 1 AND bool_and(p.proowner='learnstack_migration'::regrole AND NOT p.prosecdef
                  AND p.prosrc=@body AND l.lanname=@language AND p.proconfig=ARRAY[@settings]::text[]
                  AND has_function_privilege(current_user,p.oid,'EXECUTE')
                  AND NOT EXISTS (SELECT FROM aclexplode(coalesce(p.proacl,acldefault('f',p.proowner))) acl
                      WHERE acl.grantee NOT IN (p.proowner,'learnstack_outbox_admin'::regrole) OR (acl.grantee<>p.proowner AND acl.is_grantable)))
                FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace JOIN pg_language l ON l.oid=p.prolang
                WHERE n.nspname='public' AND p.proname=@name
                """, ("name", name), ("body", definition.Body), ("language", definition.Language), ("settings", definition.Settings));
        foreach (var table in HangfireStorageContract.Columns.Keys)
        {
            var expected = table switch
            {
                "schema" => null,
                "job" => "fn_hangfire_job_guard",
                "state" => "fn_hangfire_state_guard",
                "jobparameter" => "fn_hangfire_parameter_guard",
                _ => "fn_hangfire_metadata_guard",
            };
            Require(connection, """
                SELECT count(*)=@count AND coalesce(bool_and(t.tgname=replace(@guard,'fn_','tg_') AND p.proname=@guard
                  AND p.pronamespace='public'::regnamespace AND t.tgenabled='A' AND t.tgtype=23
                  AND t.tgnargs=0 AND t.tgqual IS NULL AND t.tgconstraint=0),true)
                FROM pg_trigger t JOIN pg_proc p ON p.oid=t.tgfoid
                WHERE t.tgrelid=@target::regclass AND NOT t.tgisinternal
                """, ("target", $"public.\"{table}\""), ("count", expected is null ? 0 : 1), ("guard", expected ?? ""));
        }
    }

    private static void VerifyMetadata(NpgsqlConnection connection)
    {
        // The same read-only predicates validate retained rows and post-redaction
        // trigger writes. Startup must not accept drift just because a guard was
        // re-enabled after a previously invalid write.
        Require(connection, """
            SELECT NOT EXISTS (SELECT FROM public.job
              WHERE public.fn_hangfire_invocation(invocationdata,arguments) IS DISTINCT FROM true
                 OR (statename IS NOT NULL AND statename NOT IN ('Enqueued','Scheduled','Processing','Succeeded','Failed','Deleted')))
              AND NOT EXISTS (SELECT FROM public.state
                WHERE public.fn_hangfire_state_valid(name,reason,data) IS DISTINCT FROM true)
              AND NOT EXISTS (SELECT FROM public.jobparameter
                WHERE public.fn_hangfire_parameter_valid(name,value) IS DISTINCT FROM true)
            """);
        foreach (var table in new[] { "jobqueue", "server", "counter", "aggregatedcounter", "lock", "set", "list", "hash" })
            Require(connection, $"SELECT NOT EXISTS (SELECT FROM public.\"{table}\" AS retained WHERE public.fn_hangfire_metadata_valid(@table,to_jsonb(retained)) IS DISTINCT FROM true)",
                ("table", table));
    }

    private static void Require(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 5 };
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        if (command.ExecuteScalar() is not true) throw new HangfireStorageRefusedException();
    }
}

/// <summary>A closed refusal, without vendor exception detail or connection material.</summary>
public sealed class HangfireStorageRefusedException() : InvalidOperationException("Hangfire storage contract verification refused startup.");
