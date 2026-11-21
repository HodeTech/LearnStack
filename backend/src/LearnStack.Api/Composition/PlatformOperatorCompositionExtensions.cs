using LearnStack.Api.Authentication;
using LearnStack.Api.Versioning;
using LearnStack.Infrastructure.PlatformOperator;
using LearnStack.Modules.Identity.Application.Contracts.OperatorActs;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Hosting;
using LearnStack.SharedKernel.Platform;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace LearnStack.Api.Composition;

/// <summary>Registers the platform operator authority: gate, grants, scheme (ADR-0101).</summary>
internal static class PlatformOperatorCompositionExtensions
{
    internal static IServiceCollection AddLearnStackPlatformOperatorAuthority(
        this IServiceCollection services, IConfiguration configuration, DeploymentMode deploymentMode)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // The gate and the grants are always registered: with no grant, or no issuer, every
        // act is refused, which is the closed default. Only the scheme and the routes depend
        // on the issuer being configured.
        services.AddOptions<PlatformOperatorGrantOptions>()
            .Bind(configuration.GetSection(PlatformOperatorGrantOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<PlatformOperatorGrantOptions>>(
            new PlatformOperatorGrantOptionsValidator(deploymentMode));
        services.TryAddSingleton<IPlatformOperatorGrantSource, ConfigurationPlatformOperatorGrantSource>();
        services.TryAddSingleton<IPlatformOperatorGate, PlatformOperatorGate>();

        // The five onboarding acts the operator may run through the ordinary pipeline on the application
        // role (ADR-0102 § 2): the closed list the executor and the operator source both judge by.
        services.AddPlatformOperatorActs(new PlatformOperatorActCatalog(
        [
            Act<OperatorProvisionTenantCommand>(PlatformOperations.TenantProvision),
            Act<OperatorMapHostCommand>(PlatformOperations.HostMap),
            Act<OperatorInitializeRoleCommand>(PlatformOperations.RoleInitialize),
            Act<OperatorCreateFirstAdminCommand>(PlatformOperations.FirstAdminCreate),
            Act<OperatorRecoverAdminCommand>(PlatformOperations.AdminRecover),
        ]));

        var settings = PlatformOperatorSettings.FromConfiguration(configuration, deploymentMode);
        if (settings is not null)
        {
            services.AddLearnStackPlatformOperatorAuthentication(settings);
            services.AddLearnStackPlatformOperatorOpenApiDocument();
        }

        return services;
    }

    private static KeyValuePair<Type, PlatformOperatorAct> Act<TRequest>(PlatformOperation operation) =>
        new(typeof(TRequest), new PlatformOperatorAct(operation));
}
