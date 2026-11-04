using LearnStack.Api.Authentication;
using LearnStack.Modules.Tenancy.Application.Contracts.Settings;
using LearnStack.Infrastructure.Email;
using LearnStack.Infrastructure.Email.Smtp;
using LearnStack.Infrastructure.Resilience;
using LearnStack.SharedKernel.Email;
using LearnStack.SharedKernel.Resilience;
using LearnStack.SharedKernel.Secrets;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LearnStack.Api.Composition;

internal static class InvitationEmailComposition
{
    internal static IServiceCollection AddInvitationEmailDelivery(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton<IInvitationEntryOriginPolicy, InvitationEntryOriginPolicy>();
        var defaults = new Dictionary<string, string?>
        {
            ["Resilience:email:Retry:Enabled"] = "false",
            ["Resilience:email:Retry:MaxRetryAttempts"] = "0",
            ["Resilience:email:Timeout:Enabled"] = "true",
            ["Resilience:email:Timeout:TotalSeconds"] = "10",
            ["Resilience:email:Bulkhead:MaxConcurrency"] = "4",
            ["Resilience:email:Bulkhead:QueueLength"] = "0",
        };
        var policyConfiguration = new ConfigurationBuilder().AddInMemoryCollection(defaults).AddConfiguration(configuration).Build();
        var policy = policyConfiguration.GetSection("Resilience:email").Get<ResilienceOptions>()!;
        if (policy.Retry.Enabled || policy.Retry.MaxRetryAttempts != 0 || !policy.Timeout.Enabled
            || policy.Timeout.TotalSeconds is <= 0 or > 60 || !double.IsFinite(policy.Timeout.TotalSeconds)
            || !policy.CircuitBreaker.Enabled || policy.Bulkhead is not { MaxConcurrency: >= 1 and <= 4, QueueLength: 0 })
            throw new InvalidOperationException("Invitation email requires finite resilience without send retries.");
        services.AddProviderResilience<IEmailProvider>(policyConfiguration, "email");
        var connectionString = configuration.GetConnectionString("Default");
        services.TryAddSingleton(_ => new InvitationSmtpProviderDataSource(connectionString
            ?? throw new InvalidOperationException("Invitation SMTP provider database is not configured.")));
        services.TryAddSingleton<InvitationEmailCipher>();
        services.TryAddScoped<InvitationEmailEnvelopeWriter>();
        services.TryAddScoped<InvitationEmailDatabasePrivacy>();
        services.TryAddSingleton(provider =>
        {
            var secrets = provider.GetRequiredService<ISecretProvider>();
            var development = provider.GetRequiredService<IHostEnvironment>().IsDevelopment();
            if (!Enum.TryParse<SmtpEmailTlsMode>(configuration["InvitationEmail:Smtp:Tls"], out var tls)
                || !int.TryParse(configuration["InvitationEmail:Smtp:Port"], out var port))
                throw new InvalidOperationException("Invitation SMTP configuration is missing or invalid.");
            return new SmtpEmailProviderOptions(configuration["InvitationEmail:Smtp:Host"] ?? "", port,
                configuration["InvitationEmail:Smtp:Sender"] ?? "", tls,
                secrets.GetSecret("InvitationEmail:Smtp:Username"), secrets.GetSecret("InvitationEmail:Smtp:Password"),
                AllowDevelopmentPlaintext: development && tls == SmtpEmailTlsMode.DevelopmentPlaintext);
        });
        services.TryAddSingleton<IEmailProvider, SmtpEmailProvider>();
        services.TryAddScoped<InvitationSmtpDelivery>();
        services.TryAddScoped<IInvitationEmailStatusReader, InvitationEmailStatusReader>();
        services.AddHostedService<InvitationEmailStartupService>();
        return services;
    }
}

internal sealed class InvitationEntryOriginPolicy(NativeEntryOriginOptions origins) : IInvitationEntryOriginPolicy
{
    public string ForHost(string normalizedHost) => origins.ForHost(normalizedHost);
}
