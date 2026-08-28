using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Persistence;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace LearnStack.Api.Composition;

/// <summary>
/// The persistence half of the composition root: the application data source,
/// the ambient unit of work, and every module <c>DbContext</c> built on it.
/// </summary>
/// <remarks>
/// <para>
/// <b>One data source, and it must be the application role's.</b>
/// <c>ConnectionStrings:Default</c> names <c>learnstack_app</c>, which is
/// <c>NOBYPASSRLS</c> and holds <c>USAGE</c> but not <c>CREATE</c> on schema
/// <c>public</c>. <c>ConnectionStrings:Migration</c> is never read here — that
/// credential lives in <c>make migrate</c> and nowhere else, because a runtime
/// that is the table owner is the arrangement <c>FORCE ROW LEVEL SECURITY</c>
/// exists to defeat
/// (<see href="../../../../docs/standards/05-database.md">Database Standards
/// § Database roles</see>).
/// </para>
/// <para>
/// <b>That is checked, not asserted.</b> Two checks, because they fail on
/// different mistakes. The name check refuses a value whose <c>Username</c> is
/// not <c>learnstack_app</c> — the symmetric guard to the one <c>make migrate</c>
/// already performs on the migration credential, and the one that catches the
/// likely operator error of pasting the <c>PlatformAdmin</c> row, which sits two
/// lines away in <c>.env.example</c>. The physical-connection check asks the
/// server whether the role it actually connected as bypasses row security, which
/// catches what a name cannot: <c>learnstack_app</c> itself granted
/// <c>BYPASSRLS</c>, or a superuser, which bypasses row security with
/// <c>rolbypassrls = false</c>. Either mistake makes every policy in the database
/// inert, and Packet 6's fail-closed state — an unresolved tenant context, so
/// <c>app.tenant_id = ''</c> — turns from "no rows" into "every tenant's rows".
/// </para>
/// <para>
/// <b>Resolved lazily, not built eagerly.</b> The data source is a singleton
/// whose factory runs when something first needs a connection. A deployment with
/// no database configured therefore fails on the first request that touches one,
/// naming the key — rather than at startup, which would make every
/// <c>WebApplicationFactory</c> test carry a database it does not use.
/// </para>
/// <para>
/// <c>ConnectionStrings:PlatformAdmin</c> and
/// <c>ConnectionStrings:OutboxDispatcher</c> are deliberately absent. They are
/// keyed data sources reachable only from <c>PlatformAdminScope</c> and the
/// outbox dispatcher, and they land with their consumers — Packet 7 and Phase
/// 02b — under
/// <c>Platform_DataSource_Resolved_Only_By_PlatformAdminScope</c>.
/// </para>
/// </remarks>
public static class PersistenceCompositionExtensions
{
    private const string DefaultConnectionName = "Default";

    /// <summary>The one role a runtime process may connect as.</summary>
    internal const string RuntimeRole = "learnstack_app";

    public static IServiceCollection AddLearnStackPersistence(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(_ =>
            BuildApplicationDataSource(configuration.GetConnectionString(DefaultConnectionName)));

        // Scoped: one connection per request, owned by this, shared by every
        // context resolved in the scope (ADR-0040).
        services.TryAddScoped<IUnitOfWork, NpgsqlUnitOfWork>();

        // Every module context goes through the helper. A registration that built
        // its own connection string would give the context its own connection,
        // which never sees SET LOCAL and reads zero rows from every tenant-owned
        // table — silently.
        services.AddModuleDbContext<TenancyDbContext>();

        return services;
    }

    /// <summary>
    /// Validates <c>ConnectionStrings:Default</c> and builds the application data
    /// source from it.
    /// </summary>
    /// <remarks>
    /// Internal rather than private so the guard can be tested for what it
    /// refuses. Every message redacts the password: this is the one place a
    /// runtime credential is read, and an error that echoed it would put it in
    /// every log that captured the startup failure.
    /// </remarks>
    internal static NpgsqlDataSource BuildApplicationDataSource(string? connectionString)
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
            throw new InvalidOperationException(
                $"ConnectionStrings:Default is not a valid connection string: {Redact(connectionString)}. "
                + "The expected form is a semicolon-separated key/value list — Host, Port, "
                + "Database, Username, Password — not a URI. See .env.example.",
                exception);
        }

        if (!string.Equals(parsed.Username, RuntimeRole, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"ConnectionStrings:Default names Username='{parsed.Username}', not {RuntimeRole}: "
                + $"{Redact(connectionString)}. A runtime process connects as the NOBYPASSRLS "
                + "application role and nothing else. learnstack_migration owns every table, and "
                + "learnstack_platform and learnstack_outbox_admin hold BYPASSRLS — with any of "
                + "them here every Row Level Security policy in the database is inert, and the "
                + "unresolved-tenant state that returns no rows returns every tenant's instead. "
                + "EnterPlatformAdminScope is the only sanctioned path to a bypass credential.");
        }

        var builder = new NpgsqlDataSourceBuilder(connectionString);

        // Asked of the server, once per physical connection, because the name is
        // not the privilege: learnstack_app could have been granted BYPASSRLS, and
        // a superuser bypasses row security with rolbypassrls = false — which is
        // why rolsuper is in the predicate.
        builder.UsePhysicalConnectionInitializer(
            connection => RefuseBypassRole(connection, async: false).GetAwaiter().GetResult(),
            connection => RefuseBypassRole(connection, async: true));

        return builder.Build();
    }

    private static async Task RefuseBypassRole(NpgsqlConnection connection, bool async)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT rolbypassrls OR rolsuper FROM pg_roles WHERE rolname = current_user";

        var bypasses = async
            ? await command.ExecuteScalarAsync()
            : command.ExecuteScalar();

        if (bypasses is true)
        {
            throw new InvalidOperationException(
                $"The runtime connected as a role that bypasses Row Level Security "
                + $"(rolbypassrls or rolsuper). Every policy in the database is then inert and "
                + "every query crosses every tenant boundary. Check ConnectionStrings:Default "
                + "and the grants on the role it names; EnterPlatformAdminScope is the only "
                + "sanctioned path to a bypass credential.");
        }
    }

    /// <summary>The connection string with its password replaced.</summary>
    private static string Redact(string connectionString) =>
        System.Text.RegularExpressions.Regex.Replace(
            connectionString,
            "(?i)(password\\s*=)[^;]*",
            "$1***");
}
