using LearnStack.Modules.Identity.Application.Contracts.Entry;
using LearnStack.Modules.Identity.Infrastructure.Bindings;
using LearnStack.Modules.Tenancy.Application.Contracts.IdentityLifecycle;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LearnStack.Api.Authentication;

internal sealed class IdentitySelfEntryOptions
{
    internal const string EnabledKey = "Identity:SelfBootstrap:Enabled";
    private IdentitySelfEntryOptions(bool enabled) => CreationEnabled = enabled;
    internal bool CreationEnabled { get; }

    internal static IdentitySelfEntryOptions FromConfiguration(IConfiguration configuration)
    {
        var value = configuration[EnabledKey];
        if (value is null) return new(false);
        if (!bool.TryParse(value, out var enabled))
            throw new InvalidOperationException("Identity self-bootstrap configuration is invalid.");
        return new(enabled);
    }
}

/// <summary>Authorizes only the exact current global-self invocation and its verified optional actor evidence.</summary>
internal sealed class HttpIdentitySelfEntryAdmission(IHttpContextAccessor accessor,
    IdentityAuthorityQualification qualification, IdentitySelfEntryOptions options)
    : IIdentitySelfEntryAdmission, INativeSessionAssociationSource
{
    private readonly HttpContext? _request = accessor.HttpContext;
    public bool CreationEnabled => options.CreationEnabled;

    public bool Allows(GlobalIdentitySubject subject, IdentitySelfOperation operation, UserId? claimedActor)
    {
        ArgumentNullException.ThrowIfNull(subject);
        var http = accessor.HttpContext;
        return http is not null && ReferenceEquals(http, _request) && qualification.Current?.Matches(subject) is true
            && http.Features.Get<NativeSessionSourceFeature>() is { GlobalOperation: { } descriptor } source
            && descriptor.Kind == operation && string.Equals(http.Request.Method, descriptor.Method, StringComparison.Ordinal)
            && source.IsCurrent(http)
            && http.Features.Get<TenantCredentialFeature>()?.VerifiedSubject is { } verified
            && verified.Issuer == subject.Issuer && verified.Subject == subject.Subject
            && verified.ClaimedActor == claimedActor;
    }

    internal NativeEntrySubject? CurrentSubject(HttpContext request, IdentitySelfOperation operation)
    {
        var http = accessor.HttpContext;
        if (http is null || !ReferenceEquals(http, request) || !ReferenceEquals(http, _request)
            || http.Features.Get<TenantCredentialFeature>()?.VerifiedSubject is not { } verified
            || qualification.Current is not { } authority || authority.Issuer != verified.Issuer)
            return null;
        var subject = new GlobalIdentitySubject(authority.AuthorityId, verified.Issuer, verified.Subject);
        return Allows(subject, operation, verified.ClaimedActor) ? new(subject, verified.ClaimedActor) : null;
    }

    public NativeSessionAssociation? Capture(UserId actor, TenantId tenant, OrganizationId? organization)
    {
        var http = accessor.HttpContext;
        if (http is null || !ReferenceEquals(http, _request)
            || http.Features.Get<NativeScopeEvidence>() is not { } native || !native.IsCurrent(http)
            || native.Actor != actor || native.Tenant != tenant || native.Organization != organization
            || http.Features.Get<TenantCredentialFeature>()?.VerifiedSubject is not { } verified
            || qualification.Current is not { } authority || authority.Issuer != verified.Issuer)
            return null;
        return new(authority.AuthorityId, IdentitySubjectKey.Compute(authority.AuthorityId, verified.Subject));
    }
}

internal sealed record NativeEntrySubject(GlobalIdentitySubject Subject, UserId? ClaimedActor)
{
    public override string ToString() => nameof(NativeEntrySubject);
}

internal static class IdentitySelfEntryRegistration
{
    internal static IServiceCollection AddLearnStackIdentitySelfEntry(this IServiceCollection services)
    {
        services.AddScoped<HttpIdentitySelfEntryAdmission>();
        // Like ordinary binding resolution, unclassified/non-self traffic must
        // never instantiate the SQL owner merely to pass the middleware chain.
        services.AddScoped<Func<IIdentitySelfStateReader>>(provider => () => provider.GetRequiredService<IIdentitySelfStateReader>());
        services.AddScoped<Func<IIdentitySelfBootstrapper>>(provider => () => provider.GetRequiredService<IIdentitySelfBootstrapper>());
        services.AddScoped<Func<IIdentitySelfDiscovery>>(provider => () => provider.GetRequiredService<IIdentitySelfDiscovery>());
        services.AddScoped<Func<IOwnMembershipScopeLabels>>(provider => () => provider.GetRequiredService<IOwnMembershipScopeLabels>());
        services.Replace(ServiceDescriptor.Scoped<IIdentitySelfEntryAdmission>(provider => provider.GetRequiredService<HttpIdentitySelfEntryAdmission>()));
        services.Replace(ServiceDescriptor.Scoped<INativeSessionAssociationSource>(provider => provider.GetRequiredService<HttpIdentitySelfEntryAdmission>()));
        services.AddSingleton(provider => new NativeEntryOriginOptions(provider.GetRequiredService<IConfiguration>()));
        return services;
    }
}
