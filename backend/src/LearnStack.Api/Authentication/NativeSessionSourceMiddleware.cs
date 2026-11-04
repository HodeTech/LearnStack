using LearnStack.Api.PublicReads;
using LearnStack.Api.Tenancy;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Secrets;
using LearnStack.SharedKernel.Time;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace LearnStack.Api.Authentication;

/// <summary>Authenticates source and immutable selectors after subject quota, without resolving a binding or scope.</summary>
internal sealed class NativeSessionSourceMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, EffectiveHostAccessor hosts,
        NativeSessionSourcePolicy policy, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Features.Set<NativeSessionSourceFeature>(null);
        context.Features.Set<NativeScopeEvidence>(null);
        if (!NativeSessionSourcePolicy.HasNativeHeaders(context.Request.Headers))
        {
            await next(context);
            return;
        }

        PublicResponsePolicy.Apply(context.Response);
        context.RequestAborted.ThrowIfCancellationRequested();
        var host = context.Features.Get<HostClassification>();
        var subject = context.Features.Get<TenantCredentialFeature>()?.VerifiedSubject;
        if (subject is null)
        {
            Refuse(context, StatusCodes.Status404NotFound);
            return;
        }

        var decision = context.Features.Get<SubjectAdmissionDecision>();
        var provenance = context.Features.Get<TenantSubjectAdmissionProvenance>();
        if (GlobalSelfOperation.ForPath(context.Request.Path) is not null
            && !context.Request.Headers.ContainsKey(NativeSessionSourcePolicy.SecretHeader))
        {
            Refuse(context, StatusCodes.Status404NotFound);
            return;
        }
        if (host is null || decision?.IsAcquired is not true || provenance?.Matches(subject) is not true
            || !hosts.IsTrustedHop(context) || !string.Equals(hosts.For(context), host.Host, StringComparison.Ordinal)
            || !policy.Matches(context.Request.Headers[NativeSessionSourcePolicy.SecretHeader], clock.UtcNow))
        {
            Refuse(context, StatusCodes.Status401Unauthorized);
            return;
        }
        if (host.Class == HostClass.Platform)
        {
            Refuse(context, StatusCodes.Status404NotFound);
            return;
        }
        if (!NativeSessionSourcePolicy.HasOnlyKnownNativeHeaders(context.Request.Headers))
        {
            Refuse(context, StatusCodes.Status400BadRequest);
            return;
        }

        var operation = GlobalSelfOperation.ForPath(context.Request.Path);
        var hasTenant = context.Request.Headers.ContainsKey(NativeSessionSourcePolicy.TenantHeader);
        var hasOrganization = context.Request.Headers.ContainsKey(NativeSessionSourcePolicy.OrganizationHeader);
        TenantId? tenant = null;
        OrganizationId? organization = null;
        if (operation is not null)
        {
            if (hasTenant || hasOrganization)
            {
                Refuse(context, StatusCodes.Status400BadRequest);
                return;
            }
        }
        else
        {
            if (!TrySelector(context, NativeSessionSourcePolicy.TenantHeader, out var selectedTenant)
                || selectedTenant == TenantId.PlatformSentinel.Value
                || (hasOrganization && !TrySelector(context, NativeSessionSourcePolicy.OrganizationHeader, out _)))
            {
                Refuse(context, StatusCodes.Status400BadRequest);
                return;
            }
            tenant = TenantId.From(selectedTenant);
            if (hasOrganization)
            {
                _ = TrySelector(context, NativeSessionSourcePolicy.OrganizationHeader, out var selectedOrganization);
                organization = OrganizationId.From(selectedOrganization);
            }
            if (tenant != host.TenantId || (host.OrganizationId is { } branch && branch != organization)
                || (subject.TenantId is not null
                    && (subject.TenantId != tenant || subject.OrganizationId != organization)))
            {
                Refuse(context, StatusCodes.Status404NotFound);
                return;
            }
        }

        context.Features.Set(new NativeSessionSourceFeature(context, host, subject, decision, provenance,
            tenant, organization, operation, hosts, policy, clock));
        await next(context);
    }

    private static bool TrySelector(HttpContext context, string name, out Guid value)
    {
        value = Guid.Empty;
        var values = context.Request.Headers[name];
        return values.Count == 1 && values[0] is { } text
            && Guid.TryParseExact(text, "D", out value) && value != Guid.Empty
            && string.Equals(text, value.ToString("D"), StringComparison.Ordinal);
    }

    private static void Refuse(HttpContext context, int status)
    {
        PublicResponsePolicy.Apply(context.Response);
        context.Response.StatusCode = status;
        if (status == StatusCodes.Status401Unauthorized) context.Response.Headers.WWWAuthenticate = "Bearer";
    }
}

internal static class NativeSessionSourceRegistration
{
    internal static IServiceCollection AddLearnStackNativeSessionSource(this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.TryAddSingleton(IdentitySelfEntryOptions.FromConfiguration(configuration));
        services.TryAddSingleton(provider =>
        {
            var secrets = provider.GetRequiredService<ISecretProvider>();
            return NativeSessionSourcePolicy.Create(
                secrets.GetSecret(NativeSessionSourcePolicy.CurrentSecretKey),
                secrets.GetSecret(NativeSessionSourcePolicy.PreviousSecretKey),
                configuration[NativeSessionSourcePolicy.RetirementKey],
                provider.GetRequiredService<IOptions<TrustedHopOptions>>().Value.Secrets,
                provider.GetRequiredService<IClock>().UtcNow);
        });
        services.AddHostedService<NativeSessionSourceStartupService>();
        return services;
    }

    internal static IApplicationBuilder UseLearnStackNativeSessionSource(this IApplicationBuilder app) =>
        app.UseMiddleware<NativeSessionSourceMiddleware>();
}

internal sealed class NativeSessionSourceStartupService(NativeSessionSourcePolicy policy, IdentitySelfEntryOptions options,
    NativeEntryOriginOptions origins) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = origins;
        if (options.CreationEnabled && !policy.IsConfigured)
            throw new InvalidOperationException("Identity self-bootstrap requires a qualified native session source.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
