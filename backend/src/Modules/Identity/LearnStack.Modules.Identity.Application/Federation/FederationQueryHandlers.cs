using LearnStack.Modules.Identity.Application.Contracts.Federation;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Entitlements;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Modules.Identity.Application.Federation;

/// <summary>The administrator's nonsecret connection detail; audit Off because it holds no personal data (Standard 18).</summary>
public sealed class GetFederationConnectionQueryHandler(IFederationConnectionStore store, FederationAuthorization authorization)
    : IRequestHandler<GetFederationConnectionQuery, Result<FederationConnectionRead>>
{
    public async Task<Result<FederationConnectionRead>> Handle(GetFederationConnectionQuery request, CancellationToken cancellationToken)
    {
        var admission = await authorization.AdmitAsync(request, cancellationToken);
        if (admission.IsFailure) return Result<FederationConnectionRead>.Fail(admission.Error!);
        var connection = await store.ReadLiveAsync(cancellationToken);
        return Result.Ok(new FederationConnectionRead(connection is null ? null : View(connection)));
    }

    internal static FederationConnectionView View(FederationConnection connection) => new(connection.Id.Value,
        FederationMapping.Map(connection.Protocol), connection.Alias, connection.DisplayName, connection.IssuerOrEntityId,
        (FederationConnectionStatus)(int)connection.State, (FederationSource)(int)connection.SourceKind, connection.ClientId,
        connection.ConfigurationRevision, connection.ConfigurationDigest, connection.DesiredDigest, connection.LastProviderDigest,
        connection.PendingOperationKind is { } kind
            ? new FederationPendingView((FederationPendingKind)(int)kind, connection.PendingStartedAt!.Value, connection.PendingQuietUntil!.Value)
            : null,
        connection.TestExpiresAt, connection.Version);
}

/// <summary>
/// ADR-0099 § Sign-in exposure: the host tenant's Active, entitled connection, or nothing. LearnStack's database
/// only, never the provider; the alias is a provider hint, not a secret.
/// </summary>
public sealed class GetPublicSignInQueryHandler(IPublicSignInReader reader, IFeatureFlags features)
    : IRequestHandler<GetPublicSignInQuery, Result<PublicSignIn>>
{
    public async Task<Result<PublicSignIn>> Handle(GetPublicSignInQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        // The read accepts no query parameters, exactly as the site bootstrap does.
        if (request.HasQuery) return Result<PublicSignIn>.Fail(new Error(LocalizedMessage.Of("lockey_validation_failed")));
        if (await reader.ReadActiveAsync(cancellationToken) is not { } option) return Result.Ok(new PublicSignIn(null));
        var key = option.Protocol == FederationBrokerProtocol.Oidc ? FeatureKeys.SsoOidc : FeatureKeys.SsoSaml;
        return Result.Ok(new PublicSignIn(await features.IsEnabledAsync(key, cancellationToken)
            ? new PublicSignInFederation(option.Alias, option.DisplayName) : null));
    }
}
