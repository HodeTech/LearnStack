using LearnStack.Application.Pipeline;
using LearnStack.Infrastructure.MultiTenancy;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Infrastructure.Validation;
using System.Diagnostics.Metrics;
using LearnStack.Infrastructure.Audit;
using LearnStack.Infrastructure.Caching;
using LearnStack.Modules.Customization.Application.Audit;
using LearnStack.Modules.Tenancy.Application.Audit;
using LearnStack.Infrastructure.Audit.Capture;
using LearnStack.Modules.Audit.Infrastructure.Persistence;
using LearnStack.Modules.Customization.Application.Abstractions;
using LearnStack.Modules.Customization.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Entitlements;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Caching;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Validation;
using LearnStack.SharedKernel.Time;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace LearnStack.Tools.Seeder;

/// <summary>
/// The seeder's service graph: one provider per act, around one shared data source.
/// </summary>
/// <remarks>
/// <para>
/// <b>One file, because the alternative was three copies.</b> The entry point and the
/// integration suite both need this graph, and a hand-maintained second copy is a second
/// thing to keep true — the copy is what drifts, and it had already drifted on the axis
/// that mattered: one built a data source per act where the other shared one.
/// </para>
/// <para>
/// <b>A provider per act, and no writable ambient accessor anywhere.</b> Writes to
/// <c>ITenantContextAccessor.Current</c> are a closed set of four
/// ([ADR-0036 Amendment 2](../../../docs/decisions/0036-tenant-resolution-trusted-inputs.md)),
/// because a writer of it can make work run under a tenant nothing resolved. Composing the
/// context into a <see cref="StaticTenantContextAccessor"/> per act means the seeder
/// cannot move the ambient tenant at all — not that it promises not to.
/// </para>
/// <para>
/// <b>The data source is the caller's, not this method's.</b> A pool per act would leave
/// one idle connection behind per command against a server with a connection ceiling, and
/// <c>AddSingleton(instance)</c> does not dispose what it did not create — measured — so
/// nothing would ever reclaim them. The owner disposes it once, after the run.
/// </para>
/// </remarks>
public static class SeedComposition
{
    /// <param name="reservedHosts">
    /// The deployment's own hosts, which no tenant may map. Defaults to none: a one-shot
    /// process binds no configuration, and a seeder that guessed at the API's list would
    /// be asserting something it cannot know. A caller that does know passes it.
    /// </param>
    public static ServiceProvider Build(
        NpgsqlDataSource dataSource,
        ITenantContext? context,
        ILoggerFactory loggerFactory,
        IReservedHostRegistry? reservedHosts = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var services = new ServiceCollection();

        services.AddSingleton(dataSource);
        services.AddSingleton(loggerFactory);
        services.AddLogging();
        services.AddSingleton<IClock, SystemClock>();

        // AuditLogBehavior mints the audit entry id app-side at step 3 — the one
        // high-volume append-only table whose id is, because a commit-in-doubt pair has to
        // carry one identity across two connections.
        services.AddSingleton<IGuidFactory, SystemGuidFactory>();
        services.AddSingleton<ITenantContextAccessor>(new StaticTenantContextAccessor(context));
        services.AddTransient<ITenantContext>(provider =>
            provider.GetRequiredService<ITenantContextAccessor>().Current
            ?? UnresolvedTenantContext.Instance);

        services.AddScoped<IUnitOfWork, NpgsqlUnitOfWork>();
        services.AddModuleDbContext<TenancyDbContext>();
        services.AddScoped<ITenantWriteStore, TenantWriteStore>();
        services.AddScoped<IOrganizationWriteStore, OrganizationWriteStore>();
        services.AddScoped<IPlatformHostMappingStore, PlatformHostMappingStore>();

        // The Customization module, for act three. Its context goes through the same
        // helper for the same reason Tenancy's does: one connection per scope, enlisted
        // on the ambient transaction, so the built-ins land under the announcement the
        // policies check them against.
        // The gate ADR-0043 § 2 puts in front of every stored schema. Registered here
        // because a second composition root that omitted it would not fail to compile —
        // it would fail at the first Register command, which is what happened. The API
        // registers the same adapter behind the same port; a seed that used a different
        // one would be seeding documents the request path might refuse.
        services.AddSingleton<IJsonSchemaValidator, JsonSchemaNetValidator>();

        services.AddModuleDbContext<CustomizationDbContext>();
        services.AddScoped<ITenantContentTypeStore, TenantContentTypeStore>();
        services.AddScoped<ITenantLevelTaxonomyStore, TenantLevelTaxonomyStore>();
        services.AddScoped<ITenantLevelTaxonomyCatalog, TenantLevelTaxonomyCatalog>();
        services.AddScoped<ICustomizationGenerationStore, CustomizationGenerationStore>();

        // The Audit module's context, on the same helper and for the same reason as the
        // other two. The registration is load-bearing rather than defensive: this root
        // calls AddLearnStackMediatRPipeline below, and CanonicalBehaviorOrder carries
        // AuditLogBehavior unconditionally — so every seeded command classifies, declares
        // and writes. AuditPipelineTests runs THIS root precisely to prove it.
        services.AddModuleDbContext<AuditDbContext>();

        // The audit write path, on the same registrations the API makes and for the
        // same reason this file already registers the schema gate: a second composition
        // root that lacks a service the API has does not fail to compile, it fails at the
        // first command that needs it. The seeder writes through the request path, and
        // AuditLogBehavior is in that pipeline — so these are not spare parts, they are
        // what keeps `make seed` working at all. An earlier comment here said the
        // behaviour was not in this root's pipeline; it always was.
        services.AddScoped<AuditStateCapture>();
        services.AddScoped<IAuditStateCapture>(
            provider => provider.GetRequiredService<AuditStateCapture>());
        // TryAddEnumerable, not TryAddScoped. ISaveChangesInterceptor is a MULTI
        // registration — AddModuleDbContext resolves the whole collection — and
        // TryAddScoped skips when ANY registration of the service type exists, so the
        // moment a second interceptor is registered first the audit capture is silently
        // not added and every audit row ships with empty snapshots and no error anywhere.
        // TryAddEnumerable is keyed on the (service, implementation) pair, which is the
        // idempotence this actually wants.
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<ISaveChangesInterceptor, AuditChangeTrackerInterceptor>());
        // PostgresAuditStore counts the durable-duplicate outcome, so it needs a meter
        // factory. AddMetrics is idempotent and the API host already calls it through
        // AddOpenTelemetry; naming it here keeps this root self-sufficient, which is what
        // lets the seeder build the same graph.
        services.AddMetrics();

        // The classifier reads overrides through the cache, so the seeder needs the same
        // implementation the API's DeploymentMode socket selects today. A seeder that
        // resolved a different one would be classifying against a different projection
        // than the request path does.
        services.AddSingleton<ICacheService>(provider => new InMemoryCacheService(
            provider.GetRequiredService<IClock>(),
            provider.GetRequiredService<IMeterFactory>()));

        // The store reports the standalone path's last outcome to this; a singleton for
        // the reason the API root's is one. The seeder maps no readiness surface and needs
        // none — what it needs is the graph to build, and a missing singleton here would
        // surface as a container error on the first seeded command rather than at startup.
        // The entitlement socket. A SINGLETON and registered in EVERY deployment mode,
        // not Development only: ADR-0035 names NullEntitlementProvider the working default
        // for this gate, with Phase 02c as the owning phase and "a tenant must be billed
        // or plan-gated" as the trigger. Until that fires there is no billing to enforce
        // and no Hub to ask, so a mode-conditional registration would make four of the
        // five modes unbootable for a capability none of them uses (ADR-0045 § 4).
        //
        // Swapping this one line is the whole of the Phase 02a completion criterion: it
        // changes the answer without touching module code, which is only true because
        // IFeatureFlags composes over the PORT rather than reading
        // platform_entitlement_cache itself.
        services.TryAddSingleton<IEntitlementProvider, NullEntitlementProvider>();

        services.TryAddSingleton<IAuditHealth, AuditHealth>();

        services.AddScoped<IAuditStore, PostgresAuditStore>();

        // The catalogue, merged once from every module's source. A singleton: it is built
        // at composition time and read on every request, and rebuilding it per scope would
        // pay the merge — and its enforcement — on every call.
        services.TryAddEnumerable([
            ServiceDescriptor.Singleton<IAuditCatalogSource, TenancyAuditCatalogSource>(),
            ServiceDescriptor.Singleton<IAuditCatalogSource, CustomizationAuditCatalogSource>(),
        ]);

        services.TryAddSingleton<IAuditCatalog>(provider =>
            new AuditCatalog(provider.GetServices<IAuditCatalogSource>()));

        // The classifier is scoped because its cache reads are per request, and it holds
        // no state of its own between them.
        services.TryAddScoped<IAuditConfigService, AuditConfigService>();


        // Its own short read-only transaction on its own connection, which is why it takes
        // a Lazy data source rather than the ambient unit of work: it answers "is this
        // organization one of this tenant's?" before the write that would depend on the
        // answer, and it must not be able to see uncommitted state from that write.
        services.AddSingleton(new Lazy<NpgsqlDataSource>(() => dataSource));
        services.AddSingleton<IOrganizationScopeValidator, OrganizationScopeValidator>();

        // Fronts no cache: a one-shot process has nothing in memory to go stale, and the
        // default answers truthfully for that host rather than approximating the API's.
        services.AddSingleton(reservedHosts ?? NoReservedHosts.Instance);
        services.AddSingleton<IHostResolutionInvalidator>(NullHostResolutionInvalidator.Instance);
        services.AddLearnStackMediatRPipeline(
            typeof(ITenantWriteStore).Assembly,
            typeof(ITenantContentTypeStore).Assembly);

        return services.BuildServiceProvider();
    }
}

/// <summary>Source-generated logging, per the house CA1848 rule.</summary>
public static partial class SeedLog
{
    [LoggerMessage(EventId = 7001, Level = LogLevel.Error, Message = "Seeding failed.")]
    public static partial void Failed(ILogger logger, Exception exception);
}
