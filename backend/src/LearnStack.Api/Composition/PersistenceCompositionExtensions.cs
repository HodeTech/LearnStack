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
/// <b>Built lazily; validated eagerly when there is anything to validate.</b>
/// The data source is a singleton whose factory runs when something first needs a
/// connection, so a <c>WebApplicationFactory</c> test — and a deployment serving
/// only platform hosts — carries no database it does not use. The two checks above
/// are not deferred with it: a present <c>ConnectionStrings:Default</c> is
/// name-checked at <c>AddLearnStackPersistence</c> time, because a string naming
/// <c>learnstack_migration</c> is precisely the ownership mistake this guard exists
/// for and the first tenant request is a bad place to discover it. An <b>absent</b>
/// key still fails lazily, on the first request that needs a tenant — which is the
/// first moment its absence means anything.
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

        var connectionString = configuration.GetConnectionString(DefaultConnectionName);

        // Validated at boot when the key is present; built on first use either way.
        //
        // The build is deferred so that a request on a platform host — answered
        // from Tenancy:PlatformHosts, never from the database — costs nothing
        // below it, and so the Docker-free host suites keep working with no
        // credential at all. But deferring the build used to defer the *checks*
        // with it, and those are worth having early: a connection string that
        // names learnstack_migration is the ownership mistake FORCE ROW LEVEL
        // SECURITY exists to defeat, and discovering it on the first tenant
        // request rather than at boot is discovering it in production. An absent
        // key still throws lazily, because a deployment that serves only platform
        // hosts legitimately has none.
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            ValidateApplicationConnectionString(connectionString);
        }

        services.TryAddSingleton(_ => BuildApplicationDataSource(connectionString));

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
        ValidateApplicationConnectionString(connectionString);

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

    /// <summary>
    /// Everything about <c>ConnectionStrings:Default</c> that can be checked
    /// without opening a connection.
    /// </summary>
    /// <remarks>
    /// Separate from the build so the composition root can run it at boot while
    /// still deferring the data source itself. The server-side bypass check is not
    /// here — it needs a connection, and it runs per physical connection.
    /// </remarks>
    internal static void ValidateApplicationConnectionString(string? connectionString)
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
                $"ConnectionStrings:Default is not a valid connection string: "
                + $"{RedactUnparsed(connectionString)}. "
                + "The expected form is a semicolon-separated key/value list — Host, Port, "
                + "Database, Username, Password — not a URI. See .env.example.",
                exception);
        }

        if (!string.Equals(parsed.Username, RuntimeRole, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"ConnectionStrings:Default names Username='{parsed.Username}', not {RuntimeRole}: "
                + $"{Redact(parsed)}. A runtime process connects as the NOBYPASSRLS "
                + "application role and nothing else. learnstack_migration owns every table, and "
                + "learnstack_platform and learnstack_outbox_admin hold BYPASSRLS — with any of "
                + "them here every Row Level Security policy in the database is inert, and the "
                + "unresolved-tenant state that returns no rows returns every tenant's instead. "
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

    /// <summary>The connection string with its password removed.</summary>
    /// <remarks>
    /// From the <b>parsed</b> builder, not by pattern-matching the raw text.
    /// Npgsql accepts <c>Pwd</c> and <c>PSW</c> as aliases for <c>Password</c> and
    /// parses all three into the same field, so a keyword regex over the raw value
    /// that knows only the canonical spelling carries the other two straight into
    /// the exception message — measured, and it is what shipped first. Setting the
    /// field is alias-proof by construction. (A regex over
    /// <c>parsed.ConnectionString</c> would also work, because the round trip
    /// normalises the aliases away — but it works for a reason a reader would have
    /// to know, and the raw-string form one edit away from it does not.)
    /// </remarks>
    private static string Redact(NpgsqlConnectionStringBuilder parsed)
    {
        var redacted = new NpgsqlConnectionStringBuilder(parsed.ConnectionString)
        {
            Password = "***",
        };

        return redacted.ConnectionString;
    }

    /// <summary>
    /// The same, for a value Npgsql could not parse — so there is no builder to
    /// clear a field on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A regex is the only tool left here, so it covers both forms a rejected value
    /// arrives in. The keyword pass knows every alias Npgsql accepts —
    /// <c>Password</c>, <c>PSW</c>, <c>PWD</c>, measured, not the canonical
    /// spelling alone.
    /// </para>
    /// <para>
    /// The second pass is the one this branch exists for. Npgsql <b>rejects</b> a
    /// URI-style DSN outright — measured — so <c>postgres://user:secret@host/db</c>
    /// is not some exotic input here, it is the input, and it carries its password
    /// in the userinfo where no <c>password=</c> appears. The first version of this
    /// method echoed it whole into an exception message that a startup failure puts
    /// in the log.
    /// </para>
    /// </remarks>
    private static string RedactUnparsed(string connectionString)
    {
        var byKeyword = System.Text.RegularExpressions.Regex.Replace(
            connectionString,
            "(?i)\\b(password|pwd|psw)(\\s*=)[^;]*",
            "$1$2***");

        // The whole userinfo, not the password half: a value shaped like a URI
        // failed to parse, so there is no field to be confident about, and the
        // username is not what the operator needs from this message anyway — the
        // message tells them the form is wrong, not which role they named.
        return System.Text.RegularExpressions.Regex.Replace(
            byKeyword,
            "(?i)(://)[^/@\\s]*@",
            "$1***@");
    }
}
