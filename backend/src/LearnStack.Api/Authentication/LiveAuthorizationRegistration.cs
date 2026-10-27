using LearnStack.Application.Authorization;
using LearnStack.Application.Audit;
using LearnStack.Api.Tenancy;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Customization.Application.Authorization;
using LearnStack.Modules.Education.Application.Authorization;
using LearnStack.Modules.Identity.Infrastructure.Authorization;
using LearnStack.Modules.Identity.Infrastructure.Bindings;
using LearnStack.Modules.Tenancy.Application.Authorization;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Hosting;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LearnStack.Api.Authentication;

internal static class LiveAuthorizationRegistration
{
    internal static IServiceCollection AddLearnStackLiveAuthorization(this IServiceCollection services,
        IConfiguration configuration, DeploymentMode deploymentMode)
    {
        services.TryAddEnumerable([
            ServiceDescriptor.Singleton<IOperationPolicySource, TenancyOperationPolicySource>(),
            ServiceDescriptor.Singleton<IOperationPolicySource, CustomizationOperationPolicySource>(),
            ServiceDescriptor.Singleton<IOperationPolicySource, EducationOperationPolicySource>(),
        ]);
        services.AddIdentityAuthorizationReader();
        services.AddIdentityOperationPolicies();
        services.AddLearnStackOperationAuthorization();
        services.TryAddScoped<Func<OperationAuthorizationCoordinator>>(provider => () => provider.GetRequiredService<OperationAuthorizationCoordinator>());
        services.TryAddScoped<Func<AuthorizationEvidenceCoordinator>>(provider => () => provider.GetRequiredService<AuthorizationEvidenceCoordinator>());
        services.AddTrustedDeliveryOperations();
        services.AddHttpContextAccessor();
        services.TryAddSingleton<OperationHttpClassifier>();
        services.TryAddScoped<TenantAuthorizationCandidateSource>();
        services.TryAddScoped<IAuthorizationCandidateSource>(provider => provider.GetRequiredService<TenantAuthorizationCandidateSource>());
        services.TryAddScoped<IAuthorizationTransportSource, HttpAuthorizationTransportSource>();
        services.TryAddScoped<ConfirmedClaimAssertionSource>();
        services.TryAddScoped<IClaimContextAssertionRecorder, ClaimContextAssertionRecorder>();
        services.Replace(ServiceDescriptor.Scoped<ITenantMembershipReader, LiveTenantMembershipReader>());

        // Each type owns a private pool. Neither alias resolves a raw datasource.
        services.TryAddSingleton(_ => AuthorizationSourceConfiguration.Resolve(
            configuration.GetConnectionString("Default"), configuration.GetConnectionString("IdentityAuthorization"),
            configuration.GetConnectionString("AuthorizationEvidence"), deploymentMode == DeploymentMode.Development));
        services.TryAddSingleton(provider => new IdentityAuthorizationReadSource(
            provider.GetRequiredService<AuthorizationSourceConfiguration>().IdentityAuthorization));
        services.TryAddSingleton(provider => new AuthorizationEvidenceSource(
            provider.GetRequiredService<AuthorizationSourceConfiguration>().AuthorizationEvidence));
        services.TryAddSingleton<Func<IdentityAuthorizationReadSource>>(provider => () => provider.GetRequiredService<IdentityAuthorizationReadSource>());
        services.TryAddSingleton<Func<AuthorizationEvidenceSource>>(provider => () => provider.GetRequiredService<AuthorizationEvidenceSource>());
        services.TryAddScoped<IAuthorizationEvidenceSessionFactory>(provider =>
            new DeferredEvidenceSessions(() => provider.GetRequiredService<AuthorizationEvidenceSource>()));
        services.AddHostedService<TenantAuthorizationSourcesStartupService>();

        services.AddAuthorization(options =>
        {
            var declared = new AuthorizationPolicy([new DeclaredOperationRequirement()], []);
            options.DefaultPolicy = declared;
            options.FallbackPolicy = declared;
        });
        services.Replace(ServiceDescriptor.Singleton<IAuthorizationPolicyProvider, OperationAuthorizationPolicyProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IAuthorizationHandler, OperationAuthorizationHandler>());
        return services;
    }

    private sealed class DeferredEvidenceSessions : IAuthorizationEvidenceSessionFactory
    {
        private readonly Func<AuthorizationEvidenceSource> _source;
        internal DeferredEvidenceSessions(Func<AuthorizationEvidenceSource> source) => _source = source;
        public Task<IAuthorizationEvidenceSession?> OpenAsync(AuthorizationEvidenceOccurrence occurrence,
            CancellationToken cancellationToken = default) => _source().OpenAsync(occurrence, cancellationToken);
    }
}

/// <summary>Qualifies the two independent lanes against the actual qualified business database.</summary>
public sealed class TenantAuthorizationSourcesStartupService(IdentityAuthorityQualification qualification,
    Func<IdentityAuthorizationReadSource> authorization, Func<AuthorizationEvidenceSource> evidence) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await qualification.QualifyAsync(cancellationToken);
        var registration = qualification.Current
            ?? throw new InvalidOperationException("Identity authorization source qualification is unavailable.");
        try
        {
            var authorizationSource = authorization();
            var evidenceSource = evidence();
            var proof = await qualification.OpenDatabaseQualificationProofAsync(cancellationToken);
            var deadline = proof.DeadlineToken;
            await using (proof)
            {
                await authorizationSource.QualifyAsync(registration.AuthorityId, registration.Issuer, proof, deadline);
                await evidenceSource.QualifyAsync(registration.AuthorityId, registration.Issuer, proof, deadline);
                if (authorizationSource.QualifiedDatabaseName != qualification.QualifiedDatabaseName
                    || evidenceSource.QualifiedDatabaseName != qualification.QualifiedDatabaseName)
                    throw new InvalidOperationException("Identity authorization sources must use the qualified business database.");
            }
            deadline.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception failure) when (failure is not (OutOfMemoryException or StackOverflowException))
        {
            throw new InvalidOperationException("Identity authorization source qualification is unavailable or does not match deployment configuration.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
