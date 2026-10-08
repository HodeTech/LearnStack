using Microsoft.Extensions.Logging;
using Npgsql;

namespace LearnStack.Infrastructure.Persistence;

/// <summary>The shared application-role guard for HTTP and one-shot request hosts.</summary>
/// <remarks>
/// ADR-0003's NOBYPASSRLS boundary applies to the seeder as well as the API.
/// Validation never connects; every physical connection checks transitive role
/// membership so a correctly named login cannot reach a bypass role with SET ROLE.
/// </remarks>
public static class ApplicationDataSource
{
    public const string RuntimeRole = "learnstack_app";

    public static NpgsqlDataSource Build(string? connectionString) => Build(connectionString, null);

    /// <summary>Builds the guarded pool with driver logging for test fault injection.</summary>
    /// <remarks>
    /// Test hosts inject logging to observe or fault setup commands. Production
    /// callers use <see cref="Build(string)"/> without an injected logger factory.
    /// This overload does not enable parameter logging.
    /// </remarks>
    public static NpgsqlDataSource Build(string? connectionString, ILoggerFactory? loggerFactory)
    {
        Validate(connectionString);
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        if (loggerFactory is not null) builder.UseLoggerFactory(loggerFactory);
        builder.UsePhysicalConnectionInitializer(
            connection => RefuseBypassRole(connection, async: false).GetAwaiter().GetResult(),
            connection => RefuseBypassRole(connection, async: true));
        return builder.Build();
    }

    public static void Validate(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:Default is not configured. It names the learnstack_app "
                + "role — the NOBYPASSRLS runtime credential — and is in .env.example. Do "
                + "not point it at ConnectionStrings:Migration: that role owns every table, "
                + "and a runtime that is the owner is what FORCE ROW LEVEL SECURITY exists "
                + "to defeat.");
        }

        NpgsqlConnectionStringBuilder parsed;

        try
        {
            parsed = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            // Npgsql's own message names neither the key nor the file. An
            // operator who pasted a URI-style DSN — the form DATABASE_URL carries
            // on several hosts — otherwise gets a bare ArgumentException out of
            // System.Data.Common.
            // The value is NOT echoed, redacted or otherwise. It failed to parse, so
            // there is no field to be confident about: the userinfo pattern could not
            // cross a '/' or a second '@' inside a password, and either one put the
            // secret in a startup log. The message's job is to name the key and the
            // expected form, and it does that without quoting anything.
            throw new InvalidOperationException(
                "ConnectionStrings:Default is not a valid connection string. The expected "
                + "form is a semicolon-separated key/value list — Host, Port, Database, "
                + "Username, Password — not a URI. The value is not repeated here because "
                + "an unparseable one cannot be reliably redacted. See .env.example.");
        }

        if (!string.Equals(parsed.Username, RuntimeRole, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"ConnectionStrings:Default names Username='{parsed.Username}', not {RuntimeRole}: "
                + $"{Redact(parsed)}. A runtime process connects as the NOBYPASSRLS "
                + "application role and nothing else. learnstack_migration owns the tables "
                + "and has DDL privileges; learnstack_platform and learnstack_outbox_admin "
                + "hold BYPASSRLS. A bypass credential makes the unresolved-tenant state "
                + "that returns no rows return every tenant's instead. "
                + "EnterPlatformAdminScope is the only sanctioned path to a bypass credential.");
        }
    }

    private static async Task RefuseBypassRole(NpgsqlConnection connection, bool async)
    {
        await using var command = connection.CreateCommand();

        // Reachability, not the role's own two attributes. `GRANT
        // learnstack_platform TO learnstack_app` leaves `rolbypassrls` and
        // `rolsuper` false on learnstack_app and still lets it `SET ROLE` into a
        // BYPASSRLS role — measured, directly and through a bridge role that holds
        // the membership on its behalf. `pg_has_role(..., 'MEMBER')` follows the
        // whole chain and includes the role itself, so this subsumes the attribute
        // check rather than sitting beside it.
        command.CommandText =
            """
            SELECT EXISTS (
                SELECT 1 FROM pg_roles r
                WHERE (r.rolbypassrls OR r.rolsuper)
                  AND pg_has_role(current_user, r.oid, 'MEMBER'))
            """;

        var bypasses = async
            ? await command.ExecuteScalarAsync()
            : command.ExecuteScalar();

        if (bypasses is true)
        {
            throw new InvalidOperationException(
                "The runtime connected as a role that can reach one which bypasses Row Level "
                + "Security — by holding rolbypassrls or rolsuper itself, or by being a member "
                + "of a role that does, directly or through another. Every policy in the "
                + "database is then one SET ROLE away from inert. Check "
                + "ConnectionStrings:Default and the role memberships granted to the role it "
                + "names; EnterPlatformAdminScope is the only sanctioned path to a bypass "
                + "credential.");
        }
    }

    private static string Redact(NpgsqlConnectionStringBuilder parsed) =>
        new NpgsqlConnectionStringBuilder(parsed.ConnectionString) { Password = "***" }.ConnectionString;
}
