using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;

namespace LearnStack.Modules.Identity.Application.Contracts.Federation;

/// <summary>How the administrator supplied the configuration (ADR-0099 § User experience).</summary>
public enum FederationSource { MetadataUrl = 1, DiscoveryUrl = 2, Manual = 3 }

/// <summary>The intent-first operations that perform a provider write.</summary>
public enum FederationPendingKind { Configure = 1, RotateSecret = 2, Rename = 3, TestStart = 4, Disable = 5, Delete = 6 }

public enum FederationConnectionStatus { Draft = 1, TestOpen = 2, TestPassed = 3, Active = 4, Disabled = 5 }

/// <summary>The Complete outcome the runner reports; a failed or uncertain write is settled, not rolled back.</summary>
public enum FederationApplyOutcome { Applied = 1, Failed = 2, Uncertain = 3 }

/// <summary>The closed failure reasons a settled operation records; never a provider message.</summary>
public enum FederationFailureReason
{
    ProviderRefused = 1,
    Drift = 2,
    OwnerMismatch = 3,
    EgressRefused = 4,
    ResubmitRequired = 5,
    ProviderUnavailable = 6,
    EgressUnavailable = 7,
}

/// <summary>What one federation request did; the HTTP status stays 200 for every settled outcome.</summary>
public enum FederationResultKind
{
    Applied = 1,
    Failed = 2,
    /// <summary>The provider outcome is unknown; the operation stays pending for Resume after its quiet window.</summary>
    Uncertain = 3,
    /// <summary>Complete could not be recorded (for example the actor lost authority); the operation stays pending.</summary>
    Pending = 4,
    /// <summary>A readback differed from the desired state; the connection returned to Draft.</summary>
    Drift = 5,
    /// <summary>A state change with no provider write (Enable, Activate, a test result) committed.</summary>
    Recorded = 6,
    /// <summary>The window had expired; the connection returned to Draft and nothing else changed.</summary>
    Expired = 7,
    /// <summary>A Complete for an operation that is no longer pending changed nothing.</summary>
    Superseded = 8,
}

/// <summary>The pending provider write a Begin or Resume recorded; no secret, assertion or token.</summary>
public sealed record FederationPendingOperation(Guid ConnectionId, Guid OperationId, FederationPendingKind Kind,
    FederationBrokerProtocol Protocol, string Alias, string DisplayName, bool Enabled, string ConfigurationDigest,
    string DesiredDigest, long Version)
{
    public override string ToString() => nameof(FederationPendingOperation);
}

/// <summary>
/// <paramref name="Corrective"/> is the disabling full-state write a failed or expired test, or a detail-load
/// readback, began in the same transaction (ADR-0099 § Lifecycle and test); the runner applies and completes it.
/// </summary>
public sealed record FederationOperationReceipt(Guid ConnectionId, long Version, FederationResultKind Outcome,
    FederationFailureReason? Reason = null, FederationPendingOperation? Corrective = null)
{
    public override string ToString() => nameof(FederationOperationReceipt);
}

/// <summary>
/// First or later Configure Begin. The configuration is the egress-resolved, nonsecret digest and identifiers
/// only; the secret and the full endpoint set stay in the runner's memory for the one provider write.
/// <paramref name="FreshSecret"/> says only whether the write carries a new secret (ADR-0099 Amendment 3).
/// </summary>
public sealed record BeginConfigureFederationCommand(long? ExpectedVersion, FederationBrokerProtocol Protocol, string? DisplayName,
    FederationSource Source, string IssuerOrEntityId, string? ClientId, string ConfigurationDigest, bool FreshSecret)
    : IRequest<Result<FederationPendingOperation>>;

public sealed record BeginRotateFederationSecretCommand(long ExpectedVersion) : IRequest<Result<FederationPendingOperation>>;
public sealed record BeginRenameFederationCommand(long ExpectedVersion, string DisplayName) : IRequest<Result<FederationPendingOperation>>;
public sealed record BeginFederationTestStartCommand(long ExpectedVersion) : IRequest<Result<FederationPendingOperation>>;
public sealed record BeginDisableFederationCommand(long ExpectedVersion) : IRequest<Result<FederationPendingOperation>>;
public sealed record BeginDeleteFederationCommand(long ExpectedVersion) : IRequest<Result<FederationPendingOperation>>;

/// <summary>Any current administrator settles a pending operation once its quiet window has passed.</summary>
public sealed record ResumeFederationOperationCommand(long ExpectedVersion) : IRequest<Result<FederationPendingOperation>>;

/// <summary>
/// The shared Complete: an ordinary protected internal request with no HTTP route. The test attempt identifier is
/// minted by the server and never leaves it except to the BFF's server-side test owner.
/// </summary>
public sealed record CompleteFederationOperationCommand(Guid ConnectionId, Guid OperationId, FederationApplyOutcome Outcome,
    string? ProviderDigest, Guid? TestAttemptId, FederationFailureReason? Reason) : IRequest<Result<FederationOperationReceipt>>;

public sealed record EnableFederationCommand(long ExpectedVersion) : IRequest<Result<FederationOperationReceipt>>;

/// <summary>
/// Activate's state transition makes no provider write; its readback and egress check ran before this transaction. An
/// expired window begins the corrective disabling write instead (ADR-0099 Amendment 4).
/// </summary>
public sealed record ActivateFederationCommand(long ExpectedVersion, string ProviderDigest)
    : IRequest<Result<FederationOperationReceipt>>;

/// <summary>
/// The test callback records the closed result of the current window's attempt. Only the BFF's server-side test owner
/// reaches it: the route requires the federation.test_record token scope (ADR-0099 Amendment 6).
/// </summary>
public sealed record RecordFederationTestResultCommand(long ExpectedVersion, Guid TestAttemptId, bool Passed, string? ProviderDigest)
    : IRequest<Result<FederationOperationReceipt>>
{
    public override string ToString() => nameof(RecordFederationTestResultCommand);
}

/// <summary>
/// A detail load's readback: records drift or an expired test window and, when the provider does not hold the
/// connection's state, begins the corrective disabling write the runner then applies (ADR-0099 § Provider writes).
/// <paramref name="ProviderConfigurationDigest"/> is the configuration digest of an owned, mapper-free, conforming
/// broker as read back, which a refused or uncertain Configure can leave different from the connection's own; a
/// disabling write keeps that configuration and changes only the enabled flag.
/// </summary>
public sealed record ReconcileFederationConnectionCommand(long ExpectedVersion, string? ProviderDigest, string? ProviderConfigurationDigest = null)
    : IRequest<Result<FederationOperationReceipt>>;

public sealed record GetFederationConnectionQuery() : IRequest<Result<FederationConnectionRead>>;

public sealed record FederationPendingView(FederationPendingKind Kind, DateTimeOffset StartedAt, DateTimeOffset QuietUntil);

/// <summary>Nonsecret connection detail; no actor, attempt identifier, secret or certificate.</summary>
public sealed record FederationConnectionView(Guid ConnectionId, FederationBrokerProtocol Protocol, string Alias, string DisplayName,
    string IssuerOrEntityId, FederationConnectionStatus Status, FederationSource Source, string? ClientId, long ConfigurationRevision,
    string ConfigurationDigest, string DesiredDigest, string? LastProviderDigest, FederationPendingView? Pending,
    DateTimeOffset? TestExpiresAt, long Version)
{
    public override string ToString() => nameof(FederationConnectionView);
}

public sealed record FederationConnectionRead(FederationConnectionView? Connection);

/// <summary>Anonymous, host-bound sign-in option (ADR-0099 § Sign-in exposure); LearnStack's database only.</summary>
[PublicSurface]
public sealed record GetPublicSignInQuery(bool HasQuery) : IRequest<Result<PublicSignIn>>;

public sealed record PublicSignInFederation(string Alias, string DisplayName);

public sealed record PublicSignIn(PublicSignInFederation? Federation);
