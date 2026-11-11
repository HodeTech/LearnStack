using LearnStack.Api.Identity;
using LearnStack.Infrastructure.Identity.ProviderEvents;
using LearnStack.Infrastructure.Resilience;
using LearnStack.Modules.Identity.Application.Contracts.ProviderEvents;
using LearnStack.Modules.Identity.Application.ProviderEvents;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Resilience;
using LearnStack.SharedKernel.Secrets;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LearnStack.Api.Composition;

internal static class ProviderEventComposition
{
    internal static IServiceCollection AddLearnStackProviderEventMirror(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddScoped<ProviderEventProductionBinding>();
        services.TryAddScoped<IProviderEventTargetPublisher, ProviderEventTargetPublisher>();
        var policy = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Resilience:identityEvents:Retry:Enabled"] = "false",
            ["Resilience:identityEvents:Retry:MaxRetryAttempts"] = "0",
            ["Resilience:identityEvents:Timeout:TotalSeconds"] = "5",
            ["Resilience:identityEvents:Bulkhead:MaxConcurrency"] = "1",
            ["Resilience:identityEvents:Bulkhead:QueueLength"] = "0",
        }).Build();
        services.AddProviderResilience<IIdentityEventProvider>(policy, "identityEvents");
        services.TryAddSingleton(sp =>
        {
            if (!Guid.TryParseExact(configuration["Identity:AuthorityId"], "D", out var authority) || authority == Guid.Empty)
                throw new InvalidOperationException("Identity event source authority is not configured.");
            return new IdentityEventProviderOptions(IdentityAuthorityId.From(authority), configuration["Authentication:Tenant:Issuer"] ?? "",
                configuration["IdentityEvents:RealmId"] ?? "", sp.GetRequiredService<IHostEnvironment>().IsDevelopment());
        });
        services.TryAddSingleton<IIdentityEventProvider>(sp => IdentityEventProviderFactory.Create(sp.GetRequiredService<IdentityEventProviderOptions>(),
            sp.GetRequiredService<ISecretProvider>(), sp.GetRequiredService<IProviderResilience<IIdentityEventProvider>>()));
        var ingressConnection = configuration.GetConnectionString("IdentityIngress");
        services.TryAddSingleton(_ => new ProviderEventIntakeSource(ingressConnection
            ?? throw new InvalidOperationException("Identity event machine database is not configured.")));
        services.TryAddSingleton<ProviderEventScanner>();
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(ProviderEventMirrorHealth)))
        {
            services.TryAddSingleton<ProviderEventMirrorHealth>();
            services.AddHealthChecks().AddCheck<ProviderEventMirrorHealth>(ProviderEventMirrorHealth.Name);
        }
        services.AddHostedService<ProviderEventMirrorService>();
        return services;
    }
}
