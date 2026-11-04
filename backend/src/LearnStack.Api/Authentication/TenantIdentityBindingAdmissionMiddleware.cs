using LearnStack.Api.PublicReads;
using LearnStack.Api.Tenancy;
using LearnStack.Modules.Identity.Application.Contracts.Bindings;
using LearnStack.Modules.Identity.Infrastructure.Bindings;
using LearnStack.SharedKernel.Identity;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace LearnStack.Api.Authentication;

/// <summary>The API's read-only existing-binding seam; it grants no creation capability.</summary>
internal interface ITenantIdentityBindingResolver
{
    Task<IdentityBindingResult> ResolveExistingAsync(VerifiedProviderSubject subject,
        CancellationToken cancellationToken = default);
}

internal sealed class TenantIdentityBindingResolver(
    IdentityAuthorityQualification qualification,
    Func<IIdentityBindingReader> reader) : ITenantIdentityBindingResolver
{
    public Task<IdentityBindingResult> ResolveExistingAsync(VerifiedProviderSubject subject,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        var authority = qualification.Current;
        if (authority is null || !string.Equals(authority.Issuer, subject.Issuer, StringComparison.Ordinal))
            return Task.FromResult(IdentityBindingResult.Refused(IdentityBindingOutcome.AuthorityUnavailable));
        return reader().ResolveExistingAsync(new GlobalIdentitySubject(authority.AuthorityId, authority.Issuer, subject.Subject),
            subject.ClaimedActor, cancellationToken);
    }
}

/// <summary>Normalizes only a quota-admitted existing binding before ordinary tenant reconciliation.</summary>
internal sealed class TenantIdentityBindingAdmissionMiddleware(RequestDelegate next,
    ILogger<TenantIdentityBindingAdmissionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, ITenantIdentityBindingResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(resolver);
        var feature = context.Features.Get<TenantCredentialFeature>();
        context.Features.Set<NativeScopeEvidence>(null);
        if (feature is not null) feature.Identity = null;
        if (context.Features.Get<HostClassification>() is null)
        {
            await next(context);
            return;
        }

        var provenance = context.Features.Get<TenantSubjectAdmissionProvenance>();
        if (feature?.VerifiedSubject is not { } subject)
        {
            if (provenance is not null || context.Features.Get<SubjectAdmissionDecision>() is not null) Refuse(context);
            else await next(context);
            return;
        }

        if (provenance?.Matches(subject) is not true
            || context.Features.Get<SubjectAdmissionDecision>()?.IsAcquired is not true)
        {
            Refuse(context);
            return;
        }

        IdentityBindingResult binding;
        try
        {
            context.RequestAborted.ThrowIfCancellationRequested();
            binding = await resolver.ResolveExistingAsync(subject, context.RequestAborted);
            context.RequestAborted.ThrowIfCancellationRequested();
        }
        catch (NpgsqlException)
        {
            StoreUnavailable(context);
            return;
        }
        catch (TimeoutException)
        {
            StoreUnavailable(context);
            return;
        }

        if (binding.Outcome != IdentityBindingOutcome.Existing || binding.Actor is not { } actor)
        {
            if (binding.Outcome is IdentityBindingOutcome.StorageUnavailable or IdentityBindingOutcome.AuthorityUnavailable)
                StoreUnavailable(context);
            else Refuse(context);
            return;
        }

        feature.Identity = new ValidatedTenantIdentity(subject.Issuer, subject.Subject, actor,
            subject.TenantId, subject.OrganizationId);
        if (context.Features.Get<NativeSessionSourceFeature>() is not null)
        {
            var native = NativeScopeEvidence.TryMint(context, feature.Identity);
            if (native is null)
            {
                Refuse(context);
                return;
            }
            context.Features.Set(native);
        }
        await next(context);
    }

    private void StoreUnavailable(HttpContext context)
    {
        context.RequestAborted.ThrowIfCancellationRequested();
        TenantAuthenticationDiagnostics.Outcome(logger, TenantAuthenticationOutcome.BindingStoreUnavailable);
        Refuse(context);
    }

    internal static void Refuse(HttpContext context)
    {
        if (context.Features.Get<TenantCredentialFeature>() is { } feature) feature.Identity = null;
        context.Features.Set<NativeScopeEvidence>(null);
        PublicResponsePolicy.Apply(context.Response);
        context.Response.Headers.WWWAuthenticate = "Bearer";
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
    }
}

/// <summary>Qualifies the immutable deployment pair before the HTTP host accepts requests.</summary>
public sealed class TenantIdentityAuthorityStartupService(IdentityAuthorityQualification qualification) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => qualification.QualifyAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal static class TenantIdentityBindingAdmissionExtensions
{
    internal static IServiceCollection AddLearnStackTenantIdentityBindingAdmission(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<Func<IIdentityBindingReader>>(provider => () => provider.GetRequiredService<IIdentityBindingReader>());
        services.TryAddScoped<ITenantIdentityBindingResolver, TenantIdentityBindingResolver>();
        services.AddHostedService<TenantIdentityAuthorityStartupService>();
        return services;
    }

    internal static IApplicationBuilder UseLearnStackTenantIdentityBindingAdmission(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<TenantIdentityBindingAdmissionMiddleware>();
    }
}
