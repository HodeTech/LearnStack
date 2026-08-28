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
/// <b>One data source, and it is the application role's.</b>
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
/// <b>Resolved lazily, not built eagerly.</b> The data source is a singleton
/// whose factory reads the connection string when something first needs a
/// connection. A deployment with no database configured therefore fails on the
/// first request that touches one, naming the key — rather than at startup, which
/// would make every <c>WebApplicationFactory</c> test carry a database it does
/// not use.
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

    public static IServiceCollection AddLearnStackPersistence(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(_ =>
        {
            var connectionString = configuration.GetConnectionString(DefaultConnectionName);

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    "ConnectionStrings:Default is not configured. It names the learnstack_app "
                    + "role — the NOBYPASSRLS runtime credential — and is in .env.example. Do "
                    + "not point it at ConnectionStrings:Migration: that role owns every table, "
                    + "and a runtime that is the owner is what FORCE ROW LEVEL SECURITY exists "
                    + "to defeat.");
            }

            return new NpgsqlDataSourceBuilder(connectionString).Build();
        });

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
}
