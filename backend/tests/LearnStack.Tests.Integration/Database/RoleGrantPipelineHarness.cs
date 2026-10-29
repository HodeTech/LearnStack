using System.Diagnostics.Metrics;
using LearnStack.Application.Authorization;
using LearnStack.Application.Pipeline;
using LearnStack.Infrastructure.Audit;
using LearnStack.Infrastructure.Audit.Capture;
using LearnStack.Infrastructure.Caching;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Application;
using LearnStack.Modules.Identity.Application.Audit;
using LearnStack.Modules.Identity.Infrastructure.Authorization;
using LearnStack.Modules.Identity.Infrastructure.Bindings;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Application.Contracts.IdentityLifecycle;
using LearnStack.Modules.Tenancy.Application.Authorization;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Caching;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using MediatR;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Actual scoped authorization, committed rechecks, audited Role writes and independent evidence source.</summary>
internal sealed class RoleGrantPipelineHarness : IAsyncDisposable
{
    private readonly IdentityAuthorizationReadSource _authorization;
    private readonly AuthorizationEvidenceSource _evidence;
    private readonly NpgsqlDataSource _business;
    private readonly CandidateSource _candidate;
    private readonly ServiceProvider _provider;
    private readonly AsyncServiceScope _scope;

    private RoleGrantPipelineHarness(IdentityAuthorizationReadSource authorization, AuthorizationEvidenceSource evidence,
        NpgsqlDataSource business, CandidateSource candidate, ServiceProvider provider, string applicationName)
    {
        _authorization = authorization; _evidence = evidence; _business = business; _candidate = candidate;
        _provider = provider; _scope = provider.CreateAsyncScope(); ApplicationName = applicationName;
    }

    public string ApplicationName { get; }
    public ISender Sender => _scope.ServiceProvider.GetRequiredService<ISender>();
    public NpgsqlUnitOfWork Unit => (NpgsqlUnitOfWork)_scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

    public static async Task<RoleGrantPipelineHarness> CreateAsync(string connectionString, IdentityAuthorityRegistration authority,
        TenantId tenant, UserId actor, string correlation, OrganizationId? organization = null)
    {
        var authorization = new IdentityAuthorizationReadSource(connectionString);
        var evidence = new AuthorizationEvidenceSource(connectionString);
        await authorization.QualifyAsync(authority.AuthorityId, authority.Issuer);
        await evidence.QualifyAsync(authority.AuthorityId, authority.Issuer);
        var application = "role-grant-proof-" + Guid.NewGuid().ToString("N");
        var business = ApplicationDataSource.Build(new NpgsqlConnectionStringBuilder(connectionString)
        { ApplicationName = application }.ConnectionString);
        var context = TenantContextFactory.Create(new TenantResolutionAttempt
        {
            HasValidatedPrincipal = true,
            UserId = actor,
            HostTenantId = tenant,
            ClaimTenantId = tenant,
            ClaimOrganizationId = organization,
            MembershipCovers = true,
            ClaimedOrganizationBelongsToTenant = true,
            CorrelationId = correlation,
        }).Value;
        Assert.NotNull(context);
        var candidate = new CandidateSource(context);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMetrics();
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IGuidFactory, SystemGuidFactory>();
        services.AddSingleton(business);
        services.AddSingleton(new Lazy<NpgsqlDataSource>(() => business));
        services.AddSingleton(authorization);
        services.AddSingleton<IAuthorizationEvidenceSessionFactory>(evidence);
        services.AddSingleton<ITenantContextAccessor>(new StaticTenantContextAccessor(context));
        services.AddTransient<ITenantContext>(_ => context);
        services.AddSingleton<IAuthorizationCandidateSource>(candidate);
        services.AddScoped<IUnitOfWork, NpgsqlUnitOfWork>();
        services.AddScoped<GlobalIdentityEnrollment>();
        services.AddScoped<IGlobalIdentityEnrollment>(provider => provider.GetRequiredService<GlobalIdentityEnrollment>());
        services.AddScoped<IGlobalIdentityScopeAccessor, GlobalIdentityScopeAccessor>();
        services.AddModuleDbContext<IdentityDbContext>();
        services.AddIdentityContextualFoundation();
        services.AddIdentityAuthorizationReader();
        services.AddIdentityOperationPolicies();
        services.AddSingleton<IOperationPolicySource, TenancyOperationPolicySource>();
        services.AddScoped<IIdentityScopeLifecycleReader, IdentityScopeLifecycleReader>();
        services.AddSingleton<IOperationPolicyCatalog>(provider => new OperationPolicyCatalog(provider.GetServices<IOperationPolicySource>()));
        services.AddScoped<AuditStateCapture>();
        services.AddScoped<IAuditStateCapture>(provider => provider.GetRequiredService<AuditStateCapture>());
        services.AddScoped<IAuditSubject>(provider => provider.GetRequiredService<AuditStateCapture>());
        services.AddScoped<ISaveChangesInterceptor, AuditChangeTrackerInterceptor>();
        services.AddScoped<ISaveChangesInterceptor, DomainEventSaveChangesInterceptor>();
        services.AddSingleton<IAuditCatalogSource, IdentityAuditCatalogSource>();
        services.AddSingleton<IAuditCatalog>(provider => new AuditCatalog(provider.GetServices<IAuditCatalogSource>()));
        services.AddSingleton<IAuditHealth, AuditHealth>();
        services.AddSingleton<ICacheService>(provider => new InMemoryCacheService(provider.GetRequiredService<IClock>(),
            provider.GetRequiredService<IMeterFactory>()));
        services.AddScoped<IAuditConfigService, AuditConfigService>();
        services.AddScoped<IAuditStore, PostgresAuditStore>();
        services.AddLearnStackBackgroundJobContracts();
        services.AddLearnStackMediatRPipeline(typeof(IdentityOperationPolicySource).Assembly);
        services.AddLearnStackOperationAuthorization();
        Assert.Equal(MediatRPipelineRegistration.CanonicalBehaviorOrder,
            services.Where(descriptor => descriptor.ServiceType == typeof(IPipelineBehavior<,>))
                .Select(descriptor => descriptor.ImplementationType));
        return new(authorization, evidence, business, candidate, services.BuildServiceProvider(), application);
    }

    public async ValueTask DisposeAsync()
    {
        await _scope.DisposeAsync();
        await _provider.DisposeAsync();
        _candidate.Dispose();
        await _business.DisposeAsync();
        await _authorization.DisposeAsync();
        await _evidence.DisposeAsync();
    }

    private sealed class CandidateSource(TenantContext context) : AuthorizationCandidate.MintingSource, IAuthorizationCandidateSource
    {
        public AuthorizationCandidate? ForValidatedScope(TenantId tenant, OrganizationId? organization) =>
            tenant == context.TenantId && organization == context.OrganizationId
                ? ForCandidate(AuthorizationSourceKind.Http, context.UserId!.Value, tenant, organization) : null;
    }
}
