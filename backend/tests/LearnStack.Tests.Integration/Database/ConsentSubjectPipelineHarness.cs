using System.Diagnostics.Metrics;
using LearnStack.Application.Authorization;
using LearnStack.Application.Pipeline;
using LearnStack.Infrastructure.Audit;
using LearnStack.Infrastructure.Audit.Capture;
using LearnStack.Infrastructure.Caching;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Application.Authorization;
using LearnStack.Modules.Identity.Application.Contracts.Authorization;
using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Tenancy.Application.Audit;
using LearnStack.Modules.Tenancy.Application.Authorization;
using LearnStack.Modules.Tenancy.Application.FieldConsents;
using LearnStack.Modules.Tenancy.Infrastructure;
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

namespace LearnStack.Tests.Integration.Database;

/// <summary>Test-only captured subject source, real MediatR authorization/transaction/audit pipeline.</summary>
internal sealed class ConsentSubjectPipelineHarness : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ServiceProvider _provider;
    private readonly AsyncServiceScope _scope;
    private readonly CandidateSource _candidate;
    private readonly WitnessSource _witness;

    private ConsentSubjectPipelineHarness(NpgsqlDataSource dataSource, ServiceProvider provider,
        CandidateSource candidate, WitnessSource witness)
    {
        _dataSource = dataSource; _provider = provider; _scope = provider.CreateAsyncScope();
        _candidate = candidate; _witness = witness;
    }

    internal ISender Sender => _scope.ServiceProvider.GetRequiredService<ISender>();

    internal static ConsentSubjectPipelineHarness Create(string connectionString, GlobalIdentitySubject subject,
        UserId actor, TenantId tenant, OrganizationId? hostOrganization, ConsentSubjectScopeObservation selected,
        string correlation)
    {
        var context = TenantContextFactory.CreateForConsentSubjectScope(tenant, hostOrganization, actor, selected, correlation).Value!;
        var dataSource = ApplicationDataSource.Build(connectionString);
        var candidate = new CandidateSource(context);
        var witness = new WitnessSource(subject, actor, tenant, hostOrganization, selected);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMetrics();
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IGuidFactory, SystemGuidFactory>();
        services.AddSingleton(dataSource);
        services.AddSingleton(new Lazy<NpgsqlDataSource>(() => dataSource));
        services.AddSingleton<ITenantContextAccessor>(new StaticTenantContextAccessor(context));
        services.AddTransient<ITenantContext>(_ => context);
        services.AddSingleton<IAuthorizationCandidateSource>(candidate);
        services.AddSingleton<IConsentSubjectScopeSource>(witness);
        services.AddScoped<IUnitOfWork, NpgsqlUnitOfWork>();
        services.AddScoped<GlobalIdentityEnrollment>();
        services.AddScoped<IGlobalIdentityEnrollment>(provider => provider.GetRequiredService<GlobalIdentityEnrollment>());
        services.AddScoped<IGlobalIdentityScopeAccessor, GlobalIdentityScopeAccessor>();
        services.AddModuleDbContext<TenancyDbContext>();
        services.AddScoped<IIdentityMutationLocks, IdentityMutationLocks>();
        services.AddScoped<OwnConsentEntryAuthorization>();
        services.AddScoped<IOwnMembershipFieldConsentHistory, OwnMembershipFieldConsentHistory>();
        services.AddScoped<IMembershipFieldConsentWriteStore, MembershipFieldConsentWriteStore>();
        services.AddSingleton<IOperationPolicySource, TenancyOperationPolicySource>();
        services.AddSingleton<IOperationPolicyCatalog>(provider => new OperationPolicyCatalog(provider.GetServices<IOperationPolicySource>()));
        services.AddScoped<IProtectedOperationAuthorizer>(provider => new IdentityOperationAuthorizer(
            () => throw new InvalidOperationException("Ordinary membership authorization is outside this captured entry proof."),
            provider.GetRequiredService<IOperationPolicyCatalog>(), consentSources: provider.GetServices<IConsentSubjectScopeSource>()));
        services.AddScoped<AuditStateCapture>();
        services.AddScoped<IAuditStateCapture>(provider => provider.GetRequiredService<AuditStateCapture>());
        services.AddScoped<IAuditSubject>(provider => provider.GetRequiredService<AuditStateCapture>());
        services.AddScoped<ISaveChangesInterceptor, AuditChangeTrackerInterceptor>();
        services.AddScoped<ISaveChangesInterceptor, DomainEventSaveChangesInterceptor>();
        services.AddSingleton<IAuditCatalogSource, TenancyAuditCatalogSource>();
        services.AddSingleton<IAuditCatalog>(provider => new AuditCatalog(provider.GetServices<IAuditCatalogSource>()));
        services.AddSingleton<IAuditHealth, AuditHealth>();
        services.AddSingleton<ICacheService>(provider => new InMemoryCacheService(provider.GetRequiredService<IClock>(),
            provider.GetRequiredService<IMeterFactory>()));
        services.AddScoped<IAuditConfigService, AuditConfigService>();
        services.AddScoped<IAuditStore, PostgresAuditStore>();
        services.AddTenantSettingsReads();
        services.AddLearnStackBackgroundJobContracts();
        services.AddLearnStackMediatRPipeline(typeof(WithdrawOwnMembershipFieldConsentCommandHandler).Assembly);
        return new(dataSource, services.BuildServiceProvider(), candidate, witness);
    }

    public async ValueTask DisposeAsync()
    {
        await _scope.DisposeAsync();
        await _provider.DisposeAsync();
        _candidate.Dispose();
        _witness.Dispose();
        await _dataSource.DisposeAsync();
    }

    private sealed class CandidateSource(TenantContext context) : AuthorizationCandidate.MintingSource, IAuthorizationCandidateSource
    {
        public AuthorizationCandidate? ForValidatedScope(TenantId tenant, OrganizationId? organization) =>
            tenant == context.TenantId && organization == context.OrganizationId
                ? ForCandidate(AuthorizationSourceKind.Http, context.UserId!.Value, tenant, organization) : null;
    }

    private sealed class WitnessSource(GlobalIdentitySubject subject, UserId actor, TenantId tenant,
        OrganizationId? hostOrganization, ConsentSubjectScopeObservation selected)
        : ConsentSubjectScopeWitness.MintingSource, IConsentSubjectScopeSource
    {
        private readonly object _evidence = new();
        private OperationInvocation? _current;
        public ConsentSubjectScopeWitness? ForInvocation(OperationInvocation invocation)
        {
            if (invocation.Operation is not LearnStack.Modules.Tenancy.Application.Contracts.FieldConsents.WithdrawOwnMembershipFieldConsentCommand)
                return null;
            _current = invocation;
            return Capture(_evidence, invocation, subject, actor, tenant, hostOrganization,
                "consent-fixture.invalid", ConsentSubjectPurpose.Withdraw, selected, null);
        }
        protected override bool IsEvidenceCurrent(object evidence, OperationInvocation invocation) =>
            ReferenceEquals(evidence, _evidence) && ReferenceEquals(invocation, _current);
    }
}
