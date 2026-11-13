using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Application.Contracts.Federation;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Entitlements;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using MediatR;

namespace LearnStack.Modules.Identity.Application.Federation;

/// <summary>
/// ADR-0099's committed steps around a provider write. Each request is its own transaction: it authorizes,
/// takes the Identity tenant fence and the connection's row lock, applies exactly the aggregate's own domain
/// method and saves before any further domain step, because the storage guard admits one row version per UPDATE.
/// None of these handlers touches the provider; the runner does, between two of them, with no transaction open.
/// </summary>
public sealed class FederationCommandHandlers(IFederationConnectionStore store, FederationAuthorization authorization,
    ITenantContext context, IIdentityMutationLocks locks, IFeatureFlags features, IAuditSubject subject, IGuidFactory ids, IClock clock)
    : IRequestHandler<BeginConfigureFederationCommand, Result<FederationPendingOperation>>,
      IRequestHandler<BeginRotateFederationSecretCommand, Result<FederationPendingOperation>>,
      IRequestHandler<BeginRenameFederationCommand, Result<FederationPendingOperation>>,
      IRequestHandler<BeginFederationTestStartCommand, Result<FederationPendingOperation>>,
      IRequestHandler<BeginDisableFederationCommand, Result<FederationPendingOperation>>,
      IRequestHandler<BeginDeleteFederationCommand, Result<FederationPendingOperation>>,
      IRequestHandler<ResumeFederationOperationCommand, Result<FederationPendingOperation>>,
      IRequestHandler<CompleteFederationOperationCommand, Result<FederationOperationReceipt>>,
      IRequestHandler<EnableFederationCommand, Result<FederationOperationReceipt>>,
      IRequestHandler<ActivateFederationCommand, Result<FederationOperationReceipt>>,
      IRequestHandler<RecordFederationTestResultCommand, Result<FederationOperationReceipt>>,
      IRequestHandler<ReconcileFederationConnectionCommand, Result<FederationOperationReceipt>>
{
    public Task<Result<FederationPendingOperation>> Handle(BeginConfigureFederationCommand request, CancellationToken cancellationToken) =>
        ActAsync(request, (connection, now, actor) => ConfigureAsync(request, connection, now, actor, cancellationToken), cancellationToken,
            Entitlement(request.Protocol));

    public Task<Result<FederationPendingOperation>> Handle(BeginRotateFederationSecretCommand request, CancellationToken cancellationToken) =>
        ActAsync(request, async (connection, now, actor) =>
        {
            if (Current(connection, request.ExpectedVersion) is { } refused) return Result<FederationPendingOperation>.Fail(refused);
            // Rotation is a Configure with a new secret; a SAML broker has no client secret.
            if (connection!.Protocol != FederationProtocol.Oidc || !await EntitledAsync(FederationMapping.Map(connection.Protocol), cancellationToken))
                return Refuse<FederationPendingOperation>(connection.Protocol != FederationProtocol.Oidc
                    ? "lockey_business_rule_violation" : "lockey_feature_disabled");
            var desired = Desired(connection, connection.DisplayName, enabled: false);
            var operation = NewOperation();
            var begun = connection.BeginConfigure(FederationOperationKind.RotateSecret, new FederationConfiguration(connection.SourceKind,
                connection.IssuerOrEntityId, connection.ClientId, connection.ConfigurationDigest, desired), operation, now, actor);
            return await PendingAsync(connection, begun, cancellationToken);
        }, cancellationToken);

    public Task<Result<FederationPendingOperation>> Handle(BeginRenameFederationCommand request, CancellationToken cancellationToken) =>
        ActAsync(request, async (connection, now, actor) =>
        {
            if (Current(connection, request.ExpectedVersion) is { } refused) return Result<FederationPendingOperation>.Fail(refused);
            if (!FederationDisplayName.TryNormalize(request.DisplayName, out var name)) return Refuse<FederationPendingOperation>("lockey_validation_failed");
            await ExpireIfDueAsync(connection!, now, actor, cancellationToken);
            var begun = connection!.BeginRename(name, Desired(connection, name, FederationMapping.ProviderEnabled(connection.State)), NewOperation(), now, actor);
            return await PendingAsync(connection, begun, cancellationToken);
        }, cancellationToken);

    public Task<Result<FederationPendingOperation>> Handle(BeginFederationTestStartCommand request, CancellationToken cancellationToken) =>
        ActAsync(request, async (connection, now, actor) =>
        {
            if (Current(connection, request.ExpectedVersion) is { } refused) return Result<FederationPendingOperation>.Fail(refused);
            if (!await EntitledAsync(FederationMapping.Map(connection!.Protocol), cancellationToken)) return Refuse<FederationPendingOperation>("lockey_feature_disabled");
            var begun = connection.BeginTestStart(Desired(connection, connection.DisplayName, enabled: true), NewOperation(), now, actor);
            return await PendingAsync(connection, begun, cancellationToken);
        }, cancellationToken);

    public Task<Result<FederationPendingOperation>> Handle(BeginDisableFederationCommand request, CancellationToken cancellationToken) =>
        ActAsync(request, async (connection, now, actor) =>
        {
            if (Current(connection, request.ExpectedVersion) is { } refused) return Result<FederationPendingOperation>.Fail(refused);
            var begun = connection!.BeginDisable(Desired(connection, connection.DisplayName, enabled: false), NewOperation(), now, actor);
            return await PendingAsync(connection, begun, cancellationToken);
        }, cancellationToken);

    public Task<Result<FederationPendingOperation>> Handle(BeginDeleteFederationCommand request, CancellationToken cancellationToken) =>
        ActAsync(request, async (connection, now, actor) =>
        {
            if (Current(connection, request.ExpectedVersion) is { } refused) return Result<FederationPendingOperation>.Fail(refused);
            var begun = connection!.BeginDelete(FederationDigest.Absent, NewOperation(), now, actor);
            return await PendingAsync(connection, begun, cancellationToken);
        }, cancellationToken);

    public Task<Result<FederationPendingOperation>> Handle(ResumeFederationOperationCommand request, CancellationToken cancellationToken) =>
        ActAsync(request, (connection, now, _) =>
        {
            if (Current(connection, request.ExpectedVersion) is { } refused) return Task.FromResult(Result<FederationPendingOperation>.Fail(refused));
            if (!connection!.HasPendingOperation) return Task.FromResult(Refuse<FederationPendingOperation>("lockey_business_rule_violation"));
            // Before the quiet window ends the first writer may still land; Resume is a conflict until then.
            if (!connection.CanResume(now)) return Task.FromResult(Refuse<FederationPendingOperation>("lockey_concurrency_conflict"));
            return Task.FromResult(Result.Ok(Pending(connection)));
        }, cancellationToken);

    public Task<Result<FederationOperationReceipt>> Handle(CompleteFederationOperationCommand request, CancellationToken cancellationToken) =>
        ActAsync(request, async (connection, now, actor) =>
        {
            // A superseded identifier changes nothing; this request's MUST row records that it arrived.
            if (connection is null || connection.Id.Value != request.ConnectionId || connection.PendingOperationId?.Value != request.OperationId)
                return Result.Ok(new FederationOperationReceipt(request.ConnectionId, connection?.Id.Value == request.ConnectionId
                    ? connection.Version : 0, FederationResultKind.Superseded));
            var outcome = request.Outcome switch
            {
                FederationApplyOutcome.Applied => FederationOperationOutcome.Applied,
                FederationApplyOutcome.Failed => FederationOperationOutcome.Failed,
                FederationApplyOutcome.Uncertain => FederationOperationOutcome.Uncertain,
                _ => (FederationOperationOutcome)0,
            };
            if (!Enum.IsDefined(outcome) || request.TestAttemptId is { } raw && raw.Version != 7)
                return Refuse<FederationOperationReceipt>("lockey_validation_failed");
            var attempt = request.TestAttemptId is { } value ? FederationTestAttemptId.From(value) : (FederationTestAttemptId?)null;
            var completed = connection.Complete(FederationOperationId.From(request.OperationId), outcome, request.ProviderDigest, attempt, now, actor);
            if (completed.IsFailure) return Refuse<FederationOperationReceipt>("lockey_business_rule_violation");
            if (!completed.Value) return Result.Ok(new FederationOperationReceipt(connection.Id.Value, connection.Version, FederationResultKind.Superseded));
            await store.UpdateAsync(connection, cancellationToken);
            if (outcome == FederationOperationOutcome.Failed && request.ProviderDigest is { } observed
                && connection.State != FederationConnectionState.Deleted
                && !string.Equals(observed, connection.DesiredDigest, StringComparison.Ordinal))
                return await DriftAsync(connection, observed, now, actor, cancellationToken);
            return Result.Ok(new FederationOperationReceipt(connection.Id.Value, connection.Version, outcome switch
            {
                FederationOperationOutcome.Applied => FederationResultKind.Applied,
                FederationOperationOutcome.Failed => FederationResultKind.Failed,
                _ => FederationResultKind.Uncertain,
            }, outcome == FederationOperationOutcome.Failed ? request.Reason : null));
        }, cancellationToken);

    public Task<Result<FederationOperationReceipt>> Handle(EnableFederationCommand request, CancellationToken cancellationToken) =>
        ActAsync(request, async (connection, now, actor) =>
        {
            if (Current(connection, request.ExpectedVersion) is { } refused) return Result<FederationOperationReceipt>.Fail(refused);
            if (connection!.Enable(now, actor).IsFailure) return Refuse<FederationOperationReceipt>("lockey_business_rule_violation");
            await store.UpdateAsync(connection, cancellationToken);
            return Result.Ok(new FederationOperationReceipt(connection.Id.Value, connection.Version, FederationResultKind.Recorded));
        }, cancellationToken);

    public Task<Result<FederationOperationReceipt>> Handle(ActivateFederationCommand request, CancellationToken cancellationToken) =>
        ActAsync(request, async (connection, now, actor) =>
        {
            if (Current(connection, request.ExpectedVersion) is { } refused) return Result<FederationOperationReceipt>.Fail(refused);
            if (!await EntitledAsync(FederationMapping.Map(connection!.Protocol), cancellationToken)) return Refuse<FederationOperationReceipt>("lockey_feature_disabled");
            if (await ExpireIfDueAsync(connection, now, actor, cancellationToken))
                return Result.Ok(new FederationOperationReceipt(connection.Id.Value, connection.Version, FederationResultKind.Expired));
            if (connection.State == FederationConnectionState.TestPassed && !connection.HasPendingOperation
                && IsNewDrift(connection, request.ProviderDigest))
                return await DriftAsync(connection, request.ProviderDigest, now, actor, cancellationToken);
            if (connection.Activate(request.ProviderDigest, now, actor).IsFailure) return Refuse<FederationOperationReceipt>("lockey_business_rule_violation");
            await store.UpdateAsync(connection, cancellationToken);
            return Result.Ok(new FederationOperationReceipt(connection.Id.Value, connection.Version, FederationResultKind.Recorded));
        }, cancellationToken);

    public Task<Result<FederationOperationReceipt>> Handle(RecordFederationTestResultCommand request, CancellationToken cancellationToken) =>
        ActAsync(request, async (connection, now, actor) =>
        {
            if (Current(connection, request.ExpectedVersion) is { } refused) return Result<FederationOperationReceipt>.Fail(refused);
            if (request.TestAttemptId.Version != 7) return Refuse<FederationOperationReceipt>("lockey_validation_failed");
            // An indeterminate readback records nothing; the window stays open for a retry inside it.
            if (request.ProviderDigest is not { } observed) return Refuse<FederationOperationReceipt>("lockey_dependency_unavailable");
            if (await ExpireIfDueAsync(connection!, now, actor, cancellationToken))
                return Result.Ok(new FederationOperationReceipt(connection!.Id.Value, connection.Version, FederationResultKind.Expired));
            if (connection!.State == FederationConnectionState.TestOpen && !connection.HasPendingOperation && IsNewDrift(connection, observed))
                return await DriftAsync(connection, observed, now, actor, cancellationToken);
            var recorded = connection.RecordTestResult(FederationTestAttemptId.From(request.TestAttemptId), request.Passed, now, actor);
            if (recorded.IsFailure) return Refuse<FederationOperationReceipt>("lockey_business_rule_violation");
            await store.UpdateAsync(connection, cancellationToken);
            return Result.Ok(new FederationOperationReceipt(connection.Id.Value, connection.Version,
                request.Passed ? FederationResultKind.Recorded : FederationResultKind.Failed));
        }, cancellationToken);

    public Task<Result<FederationOperationReceipt>> Handle(ReconcileFederationConnectionCommand request, CancellationToken cancellationToken) =>
        ActAsync(request, async (connection, now, actor) =>
        {
            if (Current(connection, request.ExpectedVersion) is { } refused) return Result<FederationOperationReceipt>.Fail(refused);
            if (connection!.HasPendingOperation) return Refuse<FederationOperationReceipt>("lockey_concurrency_conflict");
            if (await ExpireIfDueAsync(connection, now, actor, cancellationToken))
                return Result.Ok(new FederationOperationReceipt(connection.Id.Value, connection.Version, FederationResultKind.Expired));
            if (request.ProviderDigest is { } observed && IsNewDrift(connection, observed))
                return await DriftAsync(connection, observed, now, actor, cancellationToken);
            return Refuse<FederationOperationReceipt>("lockey_business_rule_violation");
        }, cancellationToken);

    /// <summary>The provider holds a definitive state other than the desired one, not already recorded.</summary>
    internal static bool IsNewDrift(FederationConnection connection, string observed) =>
        FederationConnection.IsDigest(observed) && !string.Equals(observed, connection.DesiredDigest, StringComparison.Ordinal)
        && !string.Equals(observed, connection.LastProviderDigest, StringComparison.Ordinal);

    internal static FederationPendingOperation Pending(FederationConnection connection) => new(connection.Id.Value,
        connection.PendingOperationId!.Value.Value, (FederationPendingKind)(int)connection.PendingOperationKind!.Value, FederationMapping.Map(connection.Protocol),
        connection.Alias, connection.DisplayName, FederationMapping.ProviderEnabled(connection.State), connection.ConfigurationDigest,
        connection.PendingDesiredDigest!, connection.Version);

    private async Task<Result<FederationPendingOperation>> ConfigureAsync(BeginConfigureFederationCommand request, FederationConnection? connection,
        DateTimeOffset now, UserId actor, CancellationToken cancellationToken)
    {
        var protocol = request.Protocol == FederationBrokerProtocol.Oidc ? FederationProtocol.Oidc : FederationProtocol.Saml;
        var source = (FederationSourceKind)(int)request.Source;
        if (!Enum.IsDefined(request.Protocol) || !Enum.IsDefined(source) || !FederationConnection.IsDigest(request.ConfigurationDigest))
            return Refuse<FederationPendingOperation>("lockey_validation_failed");
        if (connection is null)
        {
            // A unique-per-tenant row turns a racing create into a recoverable conflict.
            if (request.ExpectedVersion is not null) return Refuse<FederationPendingOperation>("lockey_concurrency_conflict");
            if (!FederationDisplayName.TryNormalize(request.DisplayName, out var name)) return Refuse<FederationPendingOperation>("lockey_validation_failed");
            var id = FederationConnectionId.From(ids.NewUuidV7());
            var desired = FederationDigest.Desired(FederationConnection.DeriveAlias(id), request.Protocol, request.ConfigurationDigest, name, false);
            var created = FederationConnection.Create(id, context.TenantId, protocol, name, new FederationConfiguration(source,
                request.IssuerOrEntityId, request.ClientId, request.ConfigurationDigest, desired), NewOperation(), now, actor);
            if (created.IsFailure) return Refuse<FederationPendingOperation>("lockey_business_rule_violation");
            subject.Designate(created.Value!);
            await store.AddAsync(created.Value!, cancellationToken);
            return Result.Ok(Pending(created.Value!));
        }
        if (Current(connection, request.ExpectedVersion ?? -1) is { } refused) return Result<FederationPendingOperation>.Fail(refused);
        // Protocol and display name are not Configure's to change: a new protocol needs a new connection, a new name a Rename.
        if (connection.Protocol != protocol || request.DisplayName is { } supplied
            && (!FederationDisplayName.TryNormalize(supplied, out var same) || same != connection.DisplayName))
            return Refuse<FederationPendingOperation>("lockey_business_rule_violation");
        var configured = FederationDigest.Desired(connection.Alias, request.Protocol, request.ConfigurationDigest, connection.DisplayName, false);
        var begun = connection.BeginConfigure(FederationOperationKind.Configure, new FederationConfiguration(source, request.IssuerOrEntityId,
            request.ClientId, request.ConfigurationDigest, configured), NewOperation(), now, actor);
        return await PendingAsync(connection, begun, cancellationToken);
    }

    private async Task<Result<TResult>> ActAsync<TResult>(object request,
        Func<FederationConnection?, DateTimeOffset, UserId, Task<Result<TResult>>> act, CancellationToken cancellationToken,
        FederationBrokerProtocol? entitlement = null)
    {
        var admission = await authorization.AdmitAsync(request, cancellationToken);
        if (admission.IsFailure) return Result<TResult>.Fail(admission.Error!);
        if (entitlement is { } protocol && !await EntitledAsync(protocol, cancellationToken)) return Refuse<TResult>("lockey_feature_disabled");
        try
        {
            var plan = new IdentityMutationLockPlan([], context.TenantId, IdentityTenantLockMode.Shared);
            await locks.AcquireAsync(plan, cancellationToken);
            var connection = await store.FindLiveLockedAsync(cancellationToken);
            var check = await authorization.RecheckAsync(request, admission.Value!, cancellationToken);
            if (check.IsFailure) return Result<TResult>.Fail(check.Error!);
            locks.RequireCurrent(plan);
            if (connection is not null) subject.Designate(connection);
            return await act(connection, clock.UtcNow, context.UserId!.Value);
        }
        catch (IdentityFoundationStorageException failure)
        {
            return Refuse<TResult>(failure.Failure == IdentityFoundationStorageFailure.Conflict ? "lockey_concurrency_conflict" : "lockey_dependency_unavailable");
        }
        catch (IdentityMutationLockException) { return Refuse<TResult>("lockey_dependency_unavailable"); }
    }

    private async Task<Result<FederationPendingOperation>> PendingAsync(FederationConnection connection, Result<None> begun, CancellationToken cancellationToken)
    {
        // A pending write, a state the kind does not admit or a stale stamp is the aggregate's refusal, not an exception.
        if (begun.IsFailure) return Refuse<FederationPendingOperation>(connection.HasPendingOperation
            ? "lockey_concurrency_conflict" : "lockey_business_rule_violation");
        await store.UpdateAsync(connection, cancellationToken);
        return Result.Ok(Pending(connection));
    }

    private async Task<Result<FederationOperationReceipt>> DriftAsync(FederationConnection connection, string observed, DateTimeOffset now,
        UserId actor, CancellationToken cancellationToken)
    {
        if (connection.RecordDrift(observed, now, actor).IsFailure) return Refuse<FederationOperationReceipt>("lockey_business_rule_violation");
        await store.UpdateAsync(connection, cancellationToken);
        return Result.Ok(new FederationOperationReceipt(connection.Id.Value, connection.Version, FederationResultKind.Drift, FederationFailureReason.Drift));
    }

    /// <summary>An expired TestOpen or TestPassed window returns to Draft as its own saved step.</summary>
    private async Task<bool> ExpireIfDueAsync(FederationConnection connection, DateTimeOffset now, UserId actor, CancellationToken cancellationToken)
    {
        if (connection.HasPendingOperation || connection.State is not (FederationConnectionState.TestOpen or FederationConnectionState.TestPassed)
            || connection.TestExpiresAt is not { } expires || now < expires || connection.ExpireTest(now, actor).IsFailure)
            return false;
        await store.UpdateAsync(connection, cancellationToken);
        return true;
    }

    private Task<bool> EntitledAsync(FederationBrokerProtocol protocol, CancellationToken cancellationToken) =>
        features.IsEnabledAsync(protocol == FederationBrokerProtocol.Oidc ? FeatureKeys.SsoOidc : FeatureKeys.SsoSaml, cancellationToken);

    private static FederationBrokerProtocol? Entitlement(FederationBrokerProtocol protocol) => Enum.IsDefined(protocol) ? protocol : null;

    private static string Desired(FederationConnection connection, string displayName, bool enabled) =>
        FederationDigest.Desired(connection.Alias, FederationMapping.Map(connection.Protocol), connection.ConfigurationDigest, displayName, enabled);

    private static Error? Current(FederationConnection? connection, long expectedVersion) =>
        connection is null ? Refusal("lockey_not_found") : connection.Version != expectedVersion ? Refusal("lockey_concurrency_conflict") : null;

    private FederationOperationId NewOperation() => FederationOperationId.From(ids.NewUuidV7());

    private static Error Refusal(string key) => FederationAuthorization.Fail<None>(key).Error!;

    private static Result<T> Refuse<T>(string key) => FederationAuthorization.Fail<T>(key);
}

/// <summary>Dependency-free projections shared by the federation handlers and reads.</summary>
internal static class FederationMapping
{
    /// <summary>The provider is enabled exactly while a test window is open, the test has passed, or the connection is Active.</summary>
    internal static bool ProviderEnabled(FederationConnectionState state) =>
        state is FederationConnectionState.TestOpen or FederationConnectionState.TestPassed or FederationConnectionState.Active;

    internal static FederationBrokerProtocol Map(FederationProtocol protocol) =>
        protocol == FederationProtocol.Oidc ? FederationBrokerProtocol.Oidc : FederationBrokerProtocol.Saml;
}
