using Npgsql;

namespace LearnStack.Infrastructure.Jobs;

/// <summary>Load-bearing schema-23 facts from the unmodified Install.v3–v23 resources.</summary>
internal static class HangfireSchemaShape
{
    private sealed record Column(string Table, string Name, string Type, bool Nullable, string Default);
    private sealed record Constraint(string Table, string Name, string Definition, bool Validated, bool Deferred, bool InitiallyDeferred);
    private sealed record Index(string Table, string Name, string Definition, bool Valid, bool Ready, bool ReplicaIdentity);
    private sealed record Sequence(string Name, string Type, long Start, long Increment, long Minimum, long Maximum,
        long Cache, bool Cycle, string Table, string Column, string Dependency);

    public static void Verify(NpgsqlConnection connection)
    {
        RequireSet(Read(connection, """
            SELECT c.relname, a.attname, format_type(a.atttypid,a.atttypmod), NOT a.attnotnull,
                   replace(coalesce(pg_get_expr(d.adbin,d.adrelid),''),'public.','')
            FROM pg_class c JOIN pg_attribute a ON a.attrelid=c.oid
            LEFT JOIN pg_attrdef d ON d.adrelid=c.oid AND d.adnum=a.attnum
            WHERE c.relnamespace='public'::regnamespace AND c.relkind='r' AND c.relname=ANY(@names)
              AND a.attnum>0 AND NOT a.attisdropped
            """, HangfireStorageContract.Columns.Keys,
            row => new Column(row.GetString(0), row.GetString(1), row.GetString(2), row.GetBoolean(3), row.GetString(4))), Columns());
        RequireSet(Read(connection, """
            SELECT c.relname, k.conname, replace(pg_get_constraintdef(k.oid),'public.',''),
                   k.convalidated, k.condeferrable, k.condeferred
            FROM pg_class c JOIN pg_constraint k ON k.conrelid=c.oid
            WHERE c.relnamespace='public'::regnamespace AND c.relname=ANY(@names) AND k.contype<>'n'
            """, HangfireStorageContract.Columns.Keys,
            row => new Constraint(row.GetString(0), row.GetString(1), row.GetString(2),
                row.GetBoolean(3), row.GetBoolean(4), row.GetBoolean(5))), Constraints());
        RequireSet(Read(connection, """
            SELECT t.relname, i.relname, pg_get_indexdef(i.oid), x.indisvalid, x.indisready, x.indisreplident
            FROM pg_index x JOIN pg_class t ON t.oid=x.indrelid JOIN pg_class i ON i.oid=x.indexrelid
            WHERE t.relnamespace='public'::regnamespace AND t.relname=ANY(@names)
            """, HangfireStorageContract.Columns.Keys,
            row => new Index(row.GetString(0), row.GetString(1), row.GetString(2),
                row.GetBoolean(3), row.GetBoolean(4), row.GetBoolean(5))), Indexes());
        RequireSet(Read(connection, """
            SELECT s.relname, format_type(q.seqtypid,NULL), q.seqstart, q.seqincrement, q.seqmin, q.seqmax,
                   q.seqcache, q.seqcycle, coalesce(t.relname,''), coalesce(a.attname,''), coalesce(d.deptype::text,'')
            FROM pg_class s JOIN pg_sequence q ON q.seqrelid=s.oid
            LEFT JOIN pg_depend d ON d.objid=s.oid AND d.classid='pg_class'::regclass
                AND d.refclassid='pg_class'::regclass AND d.deptype='a'
            LEFT JOIN pg_class t ON t.oid=d.refobjid AND t.relnamespace='public'::regnamespace
            LEFT JOIN pg_attribute a ON a.attrelid=t.oid AND a.attnum=d.refobjsubid
            WHERE s.relnamespace='public'::regnamespace AND s.relname=ANY(@names)
            """, HangfireStorageContract.Sequences,
            row => new Sequence(row.GetString(0), row.GetString(1), row.GetInt64(2), row.GetInt64(3), row.GetInt64(4),
                row.GetInt64(5), row.GetInt64(6), row.GetBoolean(7), row.GetString(8), row.GetString(9), row.GetString(10))),
            HangfireStorageContract.Sequences.Select(name => new Sequence(name, "bigint", 1, 1, 1, long.MaxValue,
                1, false, name[..^"_id_seq".Length], "id", "a")));
    }

    private static IEnumerable<Column> Columns()
    {
        foreach (var (table, names) in HangfireStorageContract.Columns)
            foreach (var name in names)
            {
                var type = name switch
                {
                    "id" when table != "server" => "bigint",
                    "jobid" or "stateid" => "bigint",
                    "value" when table is "counter" or "aggregatedcounter" => "bigint",
                    "version" or "updatecount" => "integer",
                    "score" => "double precision",
                    "invocationdata" or "arguments" or "data" => "jsonb",
                    "expireat" or "createdat" or "fetchedat" or "lastheartbeat" or "acquired" => "timestamp with time zone",
                    _ => "text",
                };
                var nullable = name is "expireat" or "stateid" or "statename" or "reason" or "data" or "fetchedat" or "acquired"
                    || name == "value" && table is "hash" or "list" or "jobparameter";
                var defaultValue = name == "updatecount" ? "0"
                    : name == "id" && table != "server" ? $"nextval('{table}_id_seq'::regclass)" : "";
                yield return new Column(table, name, type, nullable, defaultValue);
            }
    }

    private static IEnumerable<Constraint> Constraints()
    {
        foreach (var table in HangfireStorageContract.Columns.Keys.Where(table => table != "lock"))
            yield return new Constraint(table, table + "_pkey", $"PRIMARY KEY ({(table == "schema" ? "version" : "id")})", true, false, false);
        foreach (var (table, name, columns) in UniqueKeys)
            yield return new Constraint(table, name, $"UNIQUE ({columns})", true, false, false);
        foreach (var table in new[] { "state", "jobparameter" })
            yield return new Constraint(table, table + "_jobid_fkey",
                "FOREIGN KEY (jobid) REFERENCES job(id) ON UPDATE CASCADE ON DELETE CASCADE", true, false, false);
    }

    private static readonly (string Table, string Name, string Columns)[] UniqueKeys =
    [
        ("aggregatedcounter", "aggregatedcounter_key_key", "key"),
        ("hash", "hash_key_field_key", "key, field"),
        ("set", "set_key_value_key", "key, value"),
        ("lock", "lock_resource_key", "resource"),
    ];

    private static readonly (string Table, string Name, string Columns, string Suffix)[] LookupIndexes =
    [
        ("counter", "ix_hangfire_counter_expireat", "expireat", ""),
        ("counter", "ix_hangfire_counter_key", "key", ""),
        ("hash", "ix_hangfire_hash_expireat", "expireat", ""),
        ("job", "ix_hangfire_job_expireat", "expireat", ""),
        ("job", "ix_hangfire_job_statename", "statename", ""),
        ("job", "ix_hangfire_job_statename_is_not_null", "statename", " INCLUDE (id) WHERE (statename IS NOT NULL)"),
        ("jobparameter", "ix_hangfire_jobparameter_jobidandname", "jobid, name", ""),
        ("jobqueue", "ix_hangfire_jobqueue_fetchedat_queue_jobid", "fetchedat NULLS FIRST, queue, jobid", ""),
        ("jobqueue", "ix_hangfire_jobqueue_jobidandqueue", "jobid, queue", ""),
        ("jobqueue", "ix_hangfire_jobqueue_queueandfetchedat", "queue, fetchedat", ""),
        ("list", "ix_hangfire_list_expireat", "expireat", ""),
        ("set", "ix_hangfire_set_expireat", "expireat", ""),
        ("set", "ix_hangfire_set_key_score", "key, score", ""),
        ("state", "ix_hangfire_state_jobid", "jobid", ""),
    ];

    private static IEnumerable<Index> Indexes()
    {
        foreach (var table in HangfireStorageContract.Columns.Keys.Where(table => table != "lock"))
            yield return IndexFact(table, table + "_pkey", table == "schema" ? "version" : "id", unique: true);
        foreach (var (table, name, columns) in UniqueKeys)
            yield return IndexFact(table, name, columns, unique: true, replicaIdentity: table == "lock");
        foreach (var (table, name, columns, suffix) in LookupIndexes)
            yield return IndexFact(table, name, columns, suffix);
    }

    private static Index IndexFact(string table, string name, string columns, string suffix = "", bool unique = false,
        bool replicaIdentity = false) => new(table, name,
            $"CREATE {(unique ? "UNIQUE " : "")}INDEX {name} ON public.{table} USING btree ({columns}){suffix}", true, true, replicaIdentity);

    private static List<T> Read<T>(NpgsqlConnection connection, string sql, IEnumerable<string> names, Func<NpgsqlDataReader, T> read)
    {
        using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("names", names.ToArray());
        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read()) rows.Add(read(reader));
        return rows;
    }

    private static void RequireSet<T>(List<T> actual, IEnumerable<T> expected)
    {
        var facts = expected.ToArray();
        if (actual.Count != facts.Length || !actual.ToHashSet().SetEquals(facts)) throw new HangfireStorageRefusedException();
    }
}
