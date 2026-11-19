using LearnStack.Api.Authentication;
using LearnStack.Api.Versioning;
using LearnStack.Infrastructure.PlatformOperator;
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

        var settings = PlatformOperatorSettings.FromConfiguration(configuration, deploymentMode);
        if (settings is not null)
        {
            services.AddLearnStackPlatformOperatorAuthentication(settings);
            services.AddLearnStackPlatformOperatorOpenApiDocument();
        }

        return services;
    }
}
