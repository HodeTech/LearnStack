using Npgsql;

namespace LearnStack.Tools.Seeder;

/// <summary>Rejects stale local credentials before infrastructure or migrations run.</summary>
public static class DemoDatabaseConfiguration
{
    public static bool IsValid(IReadOnlyDictionary<string, string> values)
    {
        try
        {
            foreach (var (key, role, password) in new[]
            {
                ("Migration", "learnstack_migration", "LEARNSTACK_MIGRATION_PW"),
                ("Default", "learnstack_app", "LEARNSTACK_APP_PW"),
                ("PlatformAdmin", "learnstack_platform", "LEARNSTACK_PLATFORM_PW"),
                ("OutboxDispatcher", "learnstack_outbox_admin", "LEARNSTACK_OUTBOX_PW"),
            })
            {
                var builder = new NpgsqlConnectionStringBuilder(values[$"ConnectionStrings__{key}"]);
                if (builder.Username != role || builder.Password != values[password]
                    || builder.Database != values["POSTGRES_DB"] || builder.Port != 5432
                    || builder.Host is not ("localhost" or "127.0.0.1")) return false;
            }
            return true;
        }
        catch (Exception failure) when (failure is ArgumentException or KeyNotFoundException)
        {
            return false;
        }
    }
}
