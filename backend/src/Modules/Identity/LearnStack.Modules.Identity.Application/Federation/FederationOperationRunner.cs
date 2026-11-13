using System.Collections.Immutable;
using LearnStack.Modules.Identity.Application.Contracts.Federation;
using LearnStack.SharedKernel.Entitlements;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using MediatR;

namespace LearnStack.Modules.Identity.Application.Federation;

/// <summary>
/// ADR-0099 § Intent-first operations budgets: unmeasured, judged limits inside the 10-second native deadline.
/// Egress covers discovery then JWKS (3 s), each database step 1 s, every provider call of one operation 2.5 s,
/// and Complete 1.5 s on a token detached from the request so a client abort still settles.
/// </summary>
public sealed record FederationOperationBudgets(TimeSpan Egress, TimeSpan DatabaseStep, TimeSpan Provider, TimeSpan Complete)
{
    public static FederationOperationBudgets Default { get; } =
        new(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(1.5));
}

/// <summary>
/// Configure input as the administrator supplied it: a discovery or metadata URL, or manual endpoints and
/// identifiers with at most two PEM certificates. The secret lives only here, in memory, for one provider write.
/// </summary>
public sealed record FederationConfigureInput(FederationBrokerProtocol Protocol, string? DisplayName, FederationSource Source,
    Uri? DocumentUrl, string? IssuerOrEntityId, Uri? AuthorizationEndpoint, Uri? TokenEndpoint, Uri? JwksEndpoint,
    Uri? UserInfoEndpoint, Uri? SingleSignOnEndpoint, ImmutableArray<string> SigningCertificates, string? ClientId,
    FederationClientAuthentication ClientAuthentication, FederationClientSecret? Secret)
{
    public override string ToString() => nameof(FederationConfigureInput);
}

/// <summary>One operation's settled receipt; the test attempt identifier is set only for an applied test start.</summary>
public sealed record FederationRun(FederationOperationReceipt Receipt, Guid? TestAttemptId = null)
{
    public override string ToString() => nameof(FederationRun);
}

public enum FederationProviderObservation { NotRead = 1, Matches = 2, Drift = 3, Unavailable = 4 }

public sealed record FederationConnectionDetail(FederationConnectionView? Connection, FederationProviderObservation Provider);

/// <summary>
/// The one Identity application service around federation provider I/O. It is not a MediatR handler: it sends
/// top-level requests through <see cref="ISender"/>, each in its own frame and transaction, and calls the provider
/// and the egress fetcher only between them, when no database transaction is open (ADR-0099). Controllers stay
/// thin and call this service; nothing else holds <see cref="IFederationProvider"/>.
/// </summary>
public sealed class FederationOperationRunner(ISender sender, IFederationProvider provider, IFederationEgressFetcher egress,
    IFeatureFlags features, ITenantContext context, IGuidFactory ids, IClock clock, FederationOperationBudgets budgets)
{
    private const int MaximumUrlLength = 2048;

    public async Task<Result<FederationRun>> ConfigureAsync(FederationConfigureInput input, long? expectedVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!Shape(input, creating: expectedVersion is null)) return Fail<FederationRun>("lockey_validation_failed");
        if (!await EntitledAsync(input.Protocol, cancellationToken)) return Fail<FederationRun>("lockey_feature_disabled");
        var resolved = await ResolveAsync(input, cancellationToken);
        if (resolved.IsFailure) return Result<FederationRun>.Fail(resolved.Error!);
        var configuration = resolved.Value!;
        var begun = await StepAsync(new BeginConfigureFederationCommand(expectedVersion, input.Protocol, input.DisplayName, input.Source,
            configuration is FederationOidcConfiguration oidc ? oidc.Issuer : ((FederationSamlConfiguration)configuration).EntityId,
            (configuration as FederationOidcConfiguration)?.ClientId, FederationDigest.Configuration(configuration)), cancellationToken);
        return begun.IsFailure ? Result<FederationRun>.Fail(begun.Error!)
            : await ProviderThenCompleteAsync(begun.Value!, configuration, input.Secret, cancellationToken);
    }

    public async Task<Result<FederationRun>> RotateSecretAsync(long expectedVersion, FederationClientSecret secret, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var begun = await StepAsync(new BeginRotateFederationSecretCommand(expectedVersion), cancellationToken);
        return begun.IsFailure ? Result<FederationRun>.Fail(begun.Error!) : await ProviderThenCompleteAsync(begun.Value!, null, secret, cancellationToken);
    }

    public Task<Result<FederationRun>> RenameAsync(long expectedVersion, string displayName, CancellationToken cancellationToken) =>
        BeginThenWriteAsync(new BeginRenameFederationCommand(expectedVersion, displayName), cancellationToken);

    /// <summary>The Begin refuses an unentitled protocol before anything is pending; the egress guard runs before the enabling write.</summary>
    public Task<Result<FederationRun>> StartTestAsync(long expectedVersion, CancellationToken cancellationToken) =>
        BeginThenWriteAsync(new BeginFederationTestStartCommand(expectedVersion), cancellationToken);

    public Task<Result<FederationRun>> DisableAsync(long expectedVersion, CancellationToken cancellationToken) =>
        BeginThenWriteAsync(new BeginDisableFederationCommand(expectedVersion), cancellationToken);

    public Task<Result<FederationRun>> DeleteAsync(long expectedVersion, CancellationToken cancellationToken) =>
        BeginThenWriteAsync(new BeginDeleteFederationCommand(expectedVersion), cancellationToken);

    public async Task<Result<FederationRun>> EnableAsync(long expectedVersion, CancellationToken cancellationToken)
    {
        var enabled = await StepAsync(new EnableFederationCommand(expectedVersion), cancellationToken);
        return enabled.IsFailure ? Result<FederationRun>.Fail(enabled.Error!) : Result.Ok(new FederationRun(enabled.Value!));
    }

    /// <summary>
    /// Any current administrator settles a pending operation by readback. Configure completes only when the nonsecret
    /// readback equals the stored desired digest; an interrupted Rotate cannot be verified and asks for a resubmit.
    /// </summary>
    public async Task<Result<FederationRun>> ResumeAsync(long expectedVersion, CancellationToken cancellationToken)
    {
        var resumed = await StepAsync(new ResumeFederationOperationCommand(expectedVersion), cancellationToken);
        if (resumed.IsFailure) return Result<FederationRun>.Fail(resumed.Error!);
        var pending = resumed.Value!;
        var owner = Owner(pending);
        string? observed;
        using (var budget = Budget(budgets.Provider, cancellationToken))
        {
            try { observed = FederationDigest.Readback(owner, await provider.ReadAsync(owner, budget.Token)); }
            catch (OperationCanceledException) { observed = null; }
        }
        if (observed is null) return await CompleteAsync(pending, FederationApplyOutcome.Uncertain, null, null, null);
        var matches = string.Equals(observed, pending.DesiredDigest, StringComparison.Ordinal);
        return pending.Kind switch
        {
            FederationPendingKind.RotateSecret => await CompleteAsync(pending, FederationApplyOutcome.Failed, observed, null,
                FederationFailureReason.ResubmitRequired),
            FederationPendingKind.Configure when !matches => await CompleteAsync(pending, FederationApplyOutcome.Failed, observed, null,
                FederationFailureReason.ResubmitRequired),
            _ when !matches => await CompleteAsync(pending, FederationApplyOutcome.Failed, observed, null, FederationFailureReason.Drift),
            FederationPendingKind.TestStart => await CompleteAsync(pending, FederationApplyOutcome.Applied, observed, ids.NewUuidV7(), null),
            _ => await CompleteAsync(pending, FederationApplyOutcome.Applied, observed, null, null),
        };
    }

    /// <summary>Activate performs no provider write: its readback and egress check run before its state transaction.</summary>
    public async Task<Result<FederationRun>> ActivateAsync(long expectedVersion, CancellationToken cancellationToken)
    {
        var observation = await ObserveAsync(expectedVersion, cancellationToken);
        if (observation.IsFailure) return Result<FederationRun>.Fail(observation.Error!);
        var (view, readback, observed) = observation.Value;
        if (!await EntitledAsync(view.Protocol, cancellationToken)) return Fail<FederationRun>("lockey_feature_disabled");
        if (observed is null) return Fail<FederationRun>("lockey_dependency_unavailable");
        if (string.Equals(observed, view.DesiredDigest, StringComparison.Ordinal))
        {
            var admitted = await AdmitEndpointsAsync(readback.State!.Configuration, cancellationToken);
            if (admitted is not null) return Fail<FederationRun>(admitted == FederationEgressStatus.Refused
                ? "lockey_business_rule_violation" : "lockey_dependency_unavailable");
        }
        var activated = await StepAsync(new ActivateFederationCommand(expectedVersion, observed), cancellationToken);
        return activated.IsFailure ? Result<FederationRun>.Fail(activated.Error!) : Result.Ok(new FederationRun(activated.Value!));
    }

    /// <summary>Server-only test result: the BFF's callback reports the attempt it holds; the readback precedes the record.</summary>
    public async Task<Result<FederationRun>> RecordTestResultAsync(long expectedVersion, Guid attempt, bool passed, CancellationToken cancellationToken)
    {
        var observation = await ObserveAsync(expectedVersion, cancellationToken);
        if (observation.IsFailure) return Result<FederationRun>.Fail(observation.Error!);
        var recorded = await StepAsync(new RecordFederationTestResultCommand(expectedVersion, attempt, passed, observation.Value.Observed), cancellationToken);
        return recorded.IsFailure ? Result<FederationRun>.Fail(recorded.Error!) : Result.Ok(new FederationRun(recorded.Value!));
    }

    /// <summary>
    /// The Studio detail load: one database read, at most one provider readback under the request deadline, and a
    /// recorded drift or expired window when the readback shows one. A pending operation is not read through.
    /// </summary>
    public async Task<Result<FederationConnectionDetail>> ReadAsync(CancellationToken cancellationToken)
    {
        var read = await StepAsync(new GetFederationConnectionQuery(), cancellationToken);
        if (read.IsFailure) return Result<FederationConnectionDetail>.Fail(read.Error!);
        if (read.Value!.Connection is not { Pending: null } view)
            return Result.Ok(new FederationConnectionDetail(read.Value.Connection, FederationProviderObservation.NotRead));
        var owner = new FederationBrokerOwner(context.TenantId, view.ConnectionId, view.Alias);
        string? observed;
        using (var budget = Budget(budgets.Provider, cancellationToken))
        {
            try { observed = FederationDigest.Readback(owner, await provider.ReadAsync(owner, budget.Token)); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { observed = null; }
        }
        var status = observed is null ? FederationProviderObservation.Unavailable
            : string.Equals(observed, view.DesiredDigest, StringComparison.Ordinal) ? FederationProviderObservation.Matches
            : FederationProviderObservation.Drift;
        var expired = view.Status is FederationConnectionStatus.TestOpen or FederationConnectionStatus.TestPassed
            && view.TestExpiresAt is { } expires && clock.UtcNow >= expires;
        var newDrift = status == FederationProviderObservation.Drift && !string.Equals(observed, view.LastProviderDigest, StringComparison.Ordinal);
        if (!expired && !newDrift) return Result.Ok(new FederationConnectionDetail(view, status));
        var reconciled = await StepAsync(new ReconcileFederationConnectionCommand(view.Version, observed), cancellationToken);
        if (reconciled.IsFailure) return Result.Ok(new FederationConnectionDetail(view, status));
        var reread = await StepAsync(new GetFederationConnectionQuery(), cancellationToken);
        return reread.IsFailure ? Result<FederationConnectionDetail>.Fail(reread.Error!)
            : Result.Ok(new FederationConnectionDetail(reread.Value!.Connection, status));
    }

    private async Task<Result<FederationRun>> BeginThenWriteAsync(IRequest<Result<FederationPendingOperation>> begin, CancellationToken cancellationToken)
    {
        var begun = await StepAsync(begin, cancellationToken);
        return begun.IsFailure ? Result<FederationRun>.Fail(begun.Error!) : await ProviderThenCompleteAsync(begun.Value!, null, null, cancellationToken);
    }

    /// <summary>
    /// The provider half: with no transaction open, under one provider budget, write the full desired state (or delete),
    /// read it back and compare digests. Any budget overrun or unknown result is uncertain. Then Complete.
    /// </summary>
    private async Task<Result<FederationRun>> ProviderThenCompleteAsync(FederationPendingOperation pending, FederationBrokerConfiguration? configuration,
        FederationClientSecret? secret, CancellationToken cancellationToken)
    {
        var owner = Owner(pending);
        FederationApplyOutcome outcome;
        string? observed = null;
        FederationFailureReason? reason = null;
        using (var budget = Budget(budgets.Provider, cancellationToken))
        {
            try
            {
                (outcome, observed, reason) = await ProviderAsync(pending, owner, configuration, secret, budget.Token, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                (outcome, observed, reason) = (FederationApplyOutcome.Uncertain, null, null);
            }
        }
        var attempt = outcome == FederationApplyOutcome.Applied && pending.Kind == FederationPendingKind.TestStart ? ids.NewUuidV7() : (Guid?)null;
        return await CompleteAsync(pending, outcome, observed, attempt, reason);
    }

    private async Task<(FederationApplyOutcome, string?, FederationFailureReason?)> ProviderAsync(FederationPendingOperation pending,
        FederationBrokerOwner owner, FederationBrokerConfiguration? configuration, FederationClientSecret? secret, CancellationToken budget,
        CancellationToken request)
    {
        if (pending.Kind == FederationPendingKind.Delete)
            return await SettleAsync(await provider.DeleteAsync(owner, budget), pending, owner, budget);
        if (configuration is null)
        {
            // A full-state rewrite starts from the owned representation, which must still be the configuration recorded.
            var current = await provider.ReadAsync(owner, budget);
            var observed = FederationDigest.Readback(owner, current);
            if (current.Status == FederationReadbackStatus.Unavailable) return (FederationApplyOutcome.Failed, null, FederationFailureReason.ProviderUnavailable);
            if (current.Status == FederationReadbackStatus.Foreign) return (FederationApplyOutcome.Failed, null, FederationFailureReason.OwnerMismatch);
            if (current.Status != FederationReadbackStatus.Present || current.MapperCount != 0 || !current.ProfileConforms
                || current.State!.Owner != owner || current.State.Protocol != pending.Protocol
                || !string.Equals(FederationDigest.Configuration(current.State.Configuration), pending.ConfigurationDigest, StringComparison.Ordinal))
                return (FederationApplyOutcome.Failed, observed, FederationFailureReason.Drift);
            configuration = current.State.Configuration;
        }
        if (pending.Kind == FederationPendingKind.TestStart)
        {
            var admitted = await AdmitEndpointsAsync(configuration, request);
            if (admitted is not null) return (FederationApplyOutcome.Failed, null, admitted == FederationEgressStatus.Refused
                ? FederationFailureReason.EgressRefused : FederationFailureReason.EgressUnavailable);
        }
        var enabled = pending.Kind == FederationPendingKind.TestStart || pending.Kind == FederationPendingKind.Rename && pending.Enabled;
        var state = new FederationBrokerState(owner, pending.DisplayName, enabled, configuration);
        // The write is exactly the state the Begin committed to; anything else is refused before the provider sees it.
        if (!string.Equals(FederationDigest.Desired(state), pending.DesiredDigest, StringComparison.Ordinal))
            return (FederationApplyOutcome.Failed, null, FederationFailureReason.Drift);
        return await SettleAsync(await provider.ApplyAsync(new FederationBrokerWrite(state, secret), budget), pending, owner, budget);
    }

    private async Task<(FederationApplyOutcome, string?, FederationFailureReason?)> SettleAsync(FederationProviderResult written,
        FederationPendingOperation pending, FederationBrokerOwner owner, CancellationToken budget)
    {
        switch (written.Status)
        {
            case FederationProviderStatus.Refused: return (FederationApplyOutcome.Failed, null, FederationFailureReason.ProviderRefused);
            case FederationProviderStatus.Foreign: return (FederationApplyOutcome.Failed, null, FederationFailureReason.OwnerMismatch);
            case FederationProviderStatus.Applied: break;
            default: return (FederationApplyOutcome.Uncertain, null, null);
        }
        // Applied means the readback equalled the desired digest; anything else is drift, not success.
        var observed = FederationDigest.Readback(owner, await provider.ReadAsync(owner, budget));
        return observed is null ? (FederationApplyOutcome.Uncertain, null, null)
            : string.Equals(observed, pending.DesiredDigest, StringComparison.Ordinal) ? (FederationApplyOutcome.Applied, observed, null)
            : (FederationApplyOutcome.Failed, observed, FederationFailureReason.Drift);
    }

    /// <summary>Complete on a detached token: a client abort still settles. A refused Complete leaves the operation pending.</summary>
    private async Task<Result<FederationRun>> CompleteAsync(FederationPendingOperation pending, FederationApplyOutcome outcome, string? observed,
        Guid? attempt, FederationFailureReason? reason)
    {
        using var detached = new CancellationTokenSource(budgets.Complete);
        Result<FederationOperationReceipt> completed;
        try
        {
            completed = await sender.Send(new CompleteFederationOperationCommand(pending.ConnectionId, pending.OperationId, outcome, observed,
                attempt, outcome == FederationApplyOutcome.Failed ? reason : null), detached.Token);
        }
        catch (OperationCanceledException)
        {
            completed = Fail<FederationOperationReceipt>("lockey_dependency_unavailable");
        }
        if (completed.IsFailure)
            return Result.Ok(new FederationRun(new FederationOperationReceipt(pending.ConnectionId, pending.Version, FederationResultKind.Pending)));
        return Result.Ok(new FederationRun(completed.Value!, completed.Value!.Outcome == FederationResultKind.Applied ? attempt : null));
    }

    private async Task<Result<(FederationConnectionView View, FederationBrokerReadback Readback, string? Observed)>> ObserveAsync(
        long expectedVersion, CancellationToken cancellationToken)
    {
        var read = await StepAsync(new GetFederationConnectionQuery(), cancellationToken);
        if (read.IsFailure) return Result<(FederationConnectionView, FederationBrokerReadback, string?)>.Fail(read.Error!);
        if (read.Value!.Connection is not { } view) return Fail<(FederationConnectionView, FederationBrokerReadback, string?)>("lockey_not_found");
        if (view.Version != expectedVersion) return Fail<(FederationConnectionView, FederationBrokerReadback, string?)>("lockey_concurrency_conflict");
        var owner = new FederationBrokerOwner(context.TenantId, view.ConnectionId, view.Alias);
        FederationBrokerReadback readback;
        using (var budget = Budget(budgets.Provider, cancellationToken))
        {
            try { readback = await provider.ReadAsync(owner, budget.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { readback = FederationBrokerReadback.Unavailable; }
        }
        return Result.Ok((view, readback, FederationDigest.Readback(owner, readback)));
    }

    /// <summary>The address-class guard over every endpoint the provider will contact; null when all are admitted.</summary>
    private async Task<FederationEgressStatus?> AdmitEndpointsAsync(FederationBrokerConfiguration configuration, CancellationToken cancellationToken)
    {
        using var budget = Budget(budgets.Egress, cancellationToken);
        try
        {
            foreach (var endpoint in configuration.Endpoints)
            {
                var admitted = await egress.CheckEndpointAsync(endpoint, budget.Token);
                if (admitted.Status != FederationEgressStatus.Admitted) return admitted.Status;
            }
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return FederationEgressStatus.Unavailable; }
    }

    /// <summary>Configure's egress: discovery then JWKS, or metadata, or manual endpoints, all inside one 3-second budget.</summary>
    private async Task<Result<FederationBrokerConfiguration>> ResolveAsync(FederationConfigureInput input, CancellationToken cancellationToken)
    {
        using var budget = Budget(budgets.Egress, cancellationToken);
        try
        {
            FederationBrokerConfiguration configuration;
            if (input.Protocol == FederationBrokerProtocol.Oidc)
            {
                string issuer;
                Uri authorization, token, jwks;
                Uri? userInfo;
                if (input.Source == FederationSource.DiscoveryUrl)
                {
                    var discovery = await egress.FetchOidcDiscoveryAsync(input.DocumentUrl!, budget.Token);
                    if (discovery.Status != FederationEgressStatus.Admitted) return Egress<FederationBrokerConfiguration>(discovery.Status);
                    (issuer, authorization, token, jwks, userInfo) = (discovery.Value!.Issuer, discovery.Value.AuthorizationEndpoint,
                        discovery.Value.TokenEndpoint, discovery.Value.JwksEndpoint, discovery.Value.UserInfoEndpoint);
                    if (!Issuer(issuer) || !Https(authorization) || !Https(token) || !Https(jwks) || userInfo is not null && !Https(userInfo))
                        return Fail<FederationBrokerConfiguration>("lockey_validation_failed");
                }
                else (issuer, authorization, token, jwks, userInfo) = (input.IssuerOrEntityId!, input.AuthorizationEndpoint!, input.TokenEndpoint!,
                    input.JwksEndpoint!, input.UserInfoEndpoint);
                var keys = await egress.FetchJwksAsync(jwks, budget.Token);
                if (keys.Status != FederationEgressStatus.Admitted) return Egress<FederationBrokerConfiguration>(keys.Status);
                if (keys.Value!.SigningKeyCount < 1) return Fail<FederationBrokerConfiguration>("lockey_validation_failed");
                configuration = new FederationOidcConfiguration(issuer, input.ClientId!, input.ClientAuthentication, authorization, token, jwks, userInfo);
            }
            else
            {
                string entity;
                Uri singleSignOn;
                ImmutableArray<string> certificates;
                if (input.Source == FederationSource.MetadataUrl)
                {
                    var metadata = await egress.FetchSamlMetadataAsync(input.DocumentUrl!, budget.Token);
                    if (metadata.Status != FederationEgressStatus.Admitted) return Egress<FederationBrokerConfiguration>(metadata.Status);
                    (entity, singleSignOn, certificates) = (metadata.Value!.EntityId, metadata.Value.SingleSignOnEndpoint, metadata.Value.SigningCertificates);
                    if (!VisibleAscii(entity) || !Https(singleSignOn) || !Certificates(certificates))
                        return Fail<FederationBrokerConfiguration>("lockey_validation_failed");
                }
                else (entity, singleSignOn, certificates) = (input.IssuerOrEntityId!, input.SingleSignOnEndpoint!, input.SigningCertificates);
                configuration = new FederationSamlConfiguration(entity, singleSignOn, certificates);
            }
            foreach (var endpoint in configuration.Endpoints.Where(endpoint => configuration is not FederationOidcConfiguration oidc
                || endpoint != oidc.JwksEndpoint))
            {
                var admitted = await egress.CheckEndpointAsync(endpoint, budget.Token);
                if (admitted.Status != FederationEgressStatus.Admitted) return Egress<FederationBrokerConfiguration>(admitted.Status);
            }
            return Result.Ok(configuration);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Fail<FederationBrokerConfiguration>("lockey_dependency_unavailable");
        }
    }

    private static bool Shape(FederationConfigureInput input, bool creating)
    {
        if (!Enum.IsDefined(input.Protocol) || !Enum.IsDefined(input.Source) || !Enum.IsDefined(input.ClientAuthentication)
            || input.SigningCertificates.IsDefault || creating && input.DisplayName is null)
            return false;
        var document = input.Source != FederationSource.Manual;
        if (document != (input.DocumentUrl is not null) || input.DocumentUrl is { } url && !Https(url)) return false;
        if (input.Protocol == FederationBrokerProtocol.Oidc)
        {
            if (input.Source == FederationSource.MetadataUrl || !VisibleAscii(input.ClientId, 255) || input.SingleSignOnEndpoint is not null
                || !input.SigningCertificates.IsEmpty || creating && input.Secret is null)
                return false;
            return document
                ? input.IssuerOrEntityId is null && input.AuthorizationEndpoint is null && input.TokenEndpoint is null
                    && input.JwksEndpoint is null && input.UserInfoEndpoint is null
                : Issuer(input.IssuerOrEntityId) && Https(input.AuthorizationEndpoint) && Https(input.TokenEndpoint)
                    && Https(input.JwksEndpoint) && (input.UserInfoEndpoint is null || Https(input.UserInfoEndpoint));
        }
        if (input.Source == FederationSource.DiscoveryUrl || input.ClientId is not null || input.Secret is not null
            || input.AuthorizationEndpoint is not null || input.TokenEndpoint is not null || input.JwksEndpoint is not null
            || input.UserInfoEndpoint is not null)
            return false;
        return document
            ? input.IssuerOrEntityId is null && input.SingleSignOnEndpoint is null && input.SigningCertificates.IsEmpty
            : VisibleAscii(input.IssuerOrEntityId) && Https(input.SingleSignOnEndpoint) && Certificates(input.SigningCertificates);
    }

    private static bool Certificates(ImmutableArray<string> certificates) => !certificates.IsDefault
        && certificates.Length is >= 1 and <= FederationCertificates.MaximumCount
        && certificates.All(certificate => FederationCertificates.Fingerprint(certificate) is not null)
        && certificates.Select(FederationCertificates.Fingerprint).Distinct(StringComparer.Ordinal).Count() == certificates.Length;

    /// <summary>An OIDC issuer has no query, fragment or userinfo; the aggregate and the storage CHECK refuse the same.</summary>
    private static bool Issuer(string? value) => VisibleAscii(value) && Uri.TryCreate(value, UriKind.Absolute, out var issuer)
        && issuer.Scheme == Uri.UriSchemeHttps && issuer.UserInfo.Length == 0 && value!.IndexOfAny(['?', '#']) < 0;

    private static bool Https(Uri? value) => value is { IsAbsoluteUri: true } uri && uri.Scheme == Uri.UriSchemeHttps
        && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 && uri.OriginalString.Length <= MaximumUrlLength;

    private static bool VisibleAscii(string? value, int maximum = MaximumUrlLength) => value is { Length: > 0 } text
        && text.Length <= maximum && text.All(character => character is >= '!' and <= '~');

    private async Task<Result<T>> StepAsync<T>(IRequest<Result<T>> request, CancellationToken cancellationToken)
    {
        using var step = Budget(budgets.DatabaseStep, cancellationToken);
        try { return await sender.Send(request, step.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Fail<T>("lockey_dependency_unavailable");
        }
    }

    private Task<bool> EntitledAsync(FederationBrokerProtocol protocol, CancellationToken cancellationToken) =>
        features.IsEnabledAsync(protocol == FederationBrokerProtocol.Oidc ? FeatureKeys.SsoOidc : FeatureKeys.SsoSaml, cancellationToken);

    private FederationBrokerOwner Owner(FederationPendingOperation pending) => new(context.TenantId, pending.ConnectionId, pending.Alias);

    private static CancellationTokenSource Budget(TimeSpan limit, CancellationToken cancellationToken)
    {
        var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(limit);
        return budget;
    }

    private static Result<T> Egress<T>(FederationEgressStatus status) =>
        Fail<T>(status == FederationEgressStatus.Refused ? "lockey_validation_failed" : "lockey_dependency_unavailable");

    private static Result<T> Fail<T>(string key) => Result<T>.Fail(new Error(LocalizedMessage.Of(key)));
}
