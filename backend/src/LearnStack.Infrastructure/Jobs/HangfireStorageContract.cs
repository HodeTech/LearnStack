using System.Collections.Frozen;
using System.Globalization;
using System.Text.RegularExpressions;

namespace LearnStack.Infrastructure.Jobs;

/// <summary>The immutable migration/runtime contract of the pinned stock transport.</summary>
public static partial class HangfireStorageContract
{
    public const int SchemaVersion = 23;
    public const string JobWakeupType = "LearnStack.Infrastructure.Jobs.HangfireJobWakeup, LearnStack.Infrastructure";
    public const string RecurrenceTickType = "LearnStack.Infrastructure.Jobs.HangfireRecurrenceTick, LearnStack.Infrastructure";

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Columns { get; } = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["schema"] = ["version"],
        ["counter"] = ["id", "key", "value", "expireat"],
        ["aggregatedcounter"] = ["id", "key", "value", "expireat"],
        ["hash"] = ["id", "key", "field", "value", "expireat", "updatecount"],
        ["job"] = ["id", "stateid", "statename", "invocationdata", "arguments", "createdat", "expireat", "updatecount"],
        ["state"] = ["id", "jobid", "name", "reason", "createdat", "data", "updatecount"],
        ["jobqueue"] = ["id", "jobid", "queue", "fetchedat", "updatecount"],
        ["list"] = ["id", "key", "value", "expireat", "updatecount"],
        ["server"] = ["id", "data", "lastheartbeat", "updatecount"],
        ["set"] = ["id", "key", "score", "value", "expireat", "updatecount"],
        ["jobparameter"] = ["id", "jobid", "name", "value", "updatecount"],
        ["lock"] = ["resource", "updatecount", "acquired"],
    }.ToFrozenDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)Array.AsReadOnly(pair.Value), StringComparer.Ordinal);

    public static IReadOnlyList<string> Sequences { get; } = Array.AsReadOnly(new[]
    {
        "counter_id_seq", "hash_id_seq", "job_id_seq", "state_id_seq", "jobqueue_id_seq",
        "list_id_seq", "set_id_seq", "jobparameter_id_seq", "aggregatedcounter_id_seq",
    });

    internal static bool Inserts(string table) => table != "schema";
    internal static bool Updates(string table) => table is not ("schema" or "state" or "counter");
    internal static bool Deletes(string table) => table is not ("schema" or "state" or "jobparameter");

    internal static string Resource(string name)
    {
        using var stream = typeof(HangfireStorageContract).Assembly.GetManifestResourceStream(
            $"LearnStack.Infrastructure.Jobs.HangfireSql.{name}")
            ?? throw new InvalidOperationException("The immutable Hangfire migration resource is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    internal static IEnumerable<string> InstallStatements()
    {
        var previous = 1;
        for (var version = 3; version <= SchemaVersion; version++)
        {
            // Exactly the stock installer's schema replacement and bookkeeping.
            yield return Resource($"Install.v{version.ToString(CultureInfo.InvariantCulture)}.sql")
                .Replace("'hangfire'", "'public'", StringComparison.Ordinal)
                .Replace("\"hangfire\"", "\"public\"", StringComparison.Ordinal)
                + $"; UPDATE public.\"schema\" SET version = {version.ToString(CultureInfo.InvariantCulture)} WHERE version = {previous.ToString(CultureInfo.InvariantCulture)};";
            previous = version;
        }
    }

    internal static string Grants()
    {
        var statements = new List<string>();
        foreach (var (table, columns) in Columns)
        {
            var target = $"public.\"{table}\"";
            statements.Add($"REVOKE ALL ON TABLE {target} FROM PUBLIC, learnstack_app, learnstack_platform, learnstack_outbox_admin;");
            statements.Add($"GRANT SELECT{(Deletes(table) ? ", DELETE" : "")} ON TABLE {target} TO learnstack_outbox_admin;");
            var names = string.Join(",", columns.Select(column => $"\"{column}\""));
            if (Inserts(table)) statements.Add($"GRANT INSERT ({names}) ON TABLE {target} TO learnstack_outbox_admin;");
            if (Updates(table)) statements.Add($"GRANT UPDATE ({names}) ON TABLE {target} TO learnstack_outbox_admin;");
        }
        var sequences = string.Join(",", Sequences.Select(sequence => $"public.\"{sequence}\""));
        statements.Add($"REVOKE ALL ON SEQUENCE {sequences} FROM PUBLIC, learnstack_app, learnstack_platform, learnstack_outbox_admin;");
        statements.Add($"GRANT USAGE ON SEQUENCE {sequences} TO learnstack_outbox_admin;");
        return string.Join(Environment.NewLine, statements);
    }

    internal static IReadOnlyDictionary<string, (string Body, string Language, string Settings)> Guards() =>
        GuardDefinition().Matches(Resource("Guards.sql")).ToDictionary(match => match.Groups[1].Value,
            match => (match.Groups[4].Value, match.Groups[2].Value,
                "search_path=" + match.Groups[3].Value), StringComparer.Ordinal);

    [GeneratedRegex(@"CREATE FUNCTION public\.(fn_hangfire_\w+)\([^\n]*\) RETURNS \w+\nLANGUAGE (sql|plpgsql)(?: IMMUTABLE| STABLE)? SET search_path = ([^\n]+)\nAS \$\$([\s\S]*?)\$\$;")]
    private static partial Regex GuardDefinition();
}
