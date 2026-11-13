using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;

namespace LearnStack.Modules.Identity.Domain;

public enum FederationProtocol { Oidc = 1, Saml = 2 }

/// <summary>ADR-0099 lifecycle. <see cref="Deleted"/> is the terminal tombstone (ADR-0099 Amendment 1).</summary>
public enum FederationConnectionState { Draft = 1, TestOpen = 2, TestPassed = 3, Active = 4, Disabled = 5, Deleted = 6 }

public enum FederationSourceKind { MetadataUrl = 1, DiscoveryUrl = 2, Manual = 3 }

/// <summary>The intent-first operations that perform a provider write (ADR-0099 § Intent-first operations).</summary>
public enum FederationOperationKind { Configure = 1, RotateSecret = 2, Rename = 3, TestStart = 4, Disable = 5, Delete = 6 }

public enum FederationOperationOutcome { Applied = 1, Failed = 2, Uncertain = 3 }

public enum FederationTestResult { Pending = 1, Passed = 2 }

/// <summary>Nonsecret desired configuration for one Configure or Rotate secret Begin.</summary>
public sealed record FederationConfiguration(FederationSourceKind SourceKind, string IssuerOrEntityId, string? ClientId,
    string ConfigurationDigest, string DesiredDigest)
{
    public override string ToString() => nameof(FederationConfiguration);
}

/// <summary>
/// One tenant's confined realm broker (ADR-0099). The row stores no secret, token, assertion or certificate;
/// actors are pseudonymous identifiers. Expected refusals are <see cref="Result"/> failures, never exceptions.
/// </summary>
[TenantOwned]
public sealed class FederationConnection : AuditableEntity<FederationConnectionId>, IAggregateRoot<FederationConnectionId>, ITenantOwned
{
    public const string AliasPrefix = "lsf-";
    public const int AliasLength = 36;
    public const int MaximumIssuerLength = 2048;
    public const int MaximumClientIdLength = 255;
    public const int DigestLength = 64;
    /// <summary>Judged bound beyond the adapter's maximum call duration; not measured (ADR-0099).</summary>
    public static readonly TimeSpan QuietWindow = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan TestWindow = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan ActivationWindow = TimeSpan.FromMinutes(10);

    private FederationConnection() { }

    public TenantId TenantId { get; private set; }
    public FederationProtocol Protocol { get; private set; }
    public string Alias { get; private set; } = string.Empty;
    public string DisplayName { get; private set; } = string.Empty;
    public string IssuerOrEntityId { get; private set; } = string.Empty;
    public FederationConnectionState State { get; private set; }
    public FederationSourceKind SourceKind { get; private set; }
    public string? ClientId { get; private set; }
    public long ConfigurationRevision { get; private set; }
    public string ConfigurationDigest { get; private set; } = string.Empty;
    public string DesiredDigest { get; private set; } = string.Empty;
    public string? LastProviderDigest { get; private set; }
    public FederationOperationKind? PendingOperationKind { get; private set; }
    public FederationOperationId? PendingOperationId { get; private set; }
    public UserId? PendingActor { get; private set; }
    public DateTimeOffset? PendingStartedAt { get; private set; }
    public DateTimeOffset? PendingQuietUntil { get; private set; }
    public string? PendingDesiredDigest { get; private set; }
    public FederationTestAttemptId? TestAttemptId { get; private set; }
    public UserId? TestActor { get; private set; }
    public long? TestRevision { get; private set; }
    public DateTimeOffset? TestExpiresAt { get; private set; }
    public FederationTestResult? TestResult { get; private set; }
    public DateTimeOffset? TestPassedAt { get; private set; }

    public bool HasPendingOperation => PendingOperationId is not null;

    /// <summary>The broker callback path: fixed for the connection's life and derived from its identifier only.</summary>
    public static string DeriveAlias(FederationConnectionId id) => AliasPrefix + id.Value.ToString("N");

    /// <summary>First Configure Begin: a Draft row with its pending provider write.</summary>
    public static Result<FederationConnection> Create(FederationConnectionId id, TenantId tenant, FederationProtocol protocol,
        string displayName, FederationConfiguration configuration, FederationOperationId operation, DateTimeOffset at, UserId actor)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!ValidId(id) || !StronglyTypedId.IsAssigned(tenant)
            || tenant == TenantId.PlatformSentinel || !Enum.IsDefined(protocol) || !ValidActor(actor) || !ValidInstant(at)
            || !FederationDisplayName.TryNormalize(displayName, out var name) || !ValidConfiguration(protocol, configuration)
            || !ValidId(operation))
            return Result<FederationConnection>.Fail(IdentityFailures.Refused());
        var connection = new FederationConnection
        {
            Id = id,
            TenantId = tenant,
            Protocol = protocol,
            Alias = DeriveAlias(id),
            DisplayName = name,
            IssuerOrEntityId = configuration.IssuerOrEntityId,
            State = FederationConnectionState.Draft,
            SourceKind = configuration.SourceKind,
            ClientId = configuration.ClientId,
            ConfigurationRevision = 1,
            ConfigurationDigest = configuration.ConfigurationDigest,
            DesiredDigest = configuration.DesiredDigest,
        };
        connection.MarkCreated(at, actor);
        connection.SetPending(FederationOperationKind.Configure, operation, configuration.DesiredDigest, at, actor);
        return Result<FederationConnection>.Ok(connection);
    }

    /// <summary>Configure or Rotate secret: a new desired configuration, broker disabled, any test cleared.</summary>
    public Result<None> BeginConfigure(FederationOperationKind kind, FederationConfiguration configuration,
        FederationOperationId operation, DateTimeOffset at, UserId actor)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (kind is not (FederationOperationKind.Configure or FederationOperationKind.RotateSecret) || !CanBegin(operation, at, actor)
            || !ValidConfiguration(Protocol, configuration) || ConfigurationRevision == long.MaxValue)
            return IdentityFailures.BusinessRule();
        // Changing the issuer or SAML entity identifier is not an edit: it requires a new connection.
        if (!string.Equals(configuration.IssuerOrEntityId, IssuerOrEntityId, StringComparison.Ordinal))
            return IdentityFailures.BusinessRule();
        MarkUpdated(at, actor);
        State = FederationConnectionState.Draft;
        ClearTest();
        SourceKind = configuration.SourceKind;
        ClientId = configuration.ClientId;
        ConfigurationRevision++;
        ConfigurationDigest = configuration.ConfigurationDigest;
        DesiredDigest = configuration.DesiredDigest;
        SetPending(kind, operation, configuration.DesiredDigest, at, actor);
        return Result<None>.Ok(None.Value);
    }

    /// <summary>Rename writes the display name with the broker's current enabled value; state is unchanged.</summary>
    public Result<None> BeginRename(string displayName, string desiredDigest, FederationOperationId operation, DateTimeOffset at, UserId actor)
    {
        if (!CanBegin(operation, at, actor) || !ValidDigest(desiredDigest)
            || !FederationDisplayName.TryNormalize(displayName, out var name))
            return IdentityFailures.BusinessRule();
        MarkUpdated(at, actor);
        DisplayName = name;
        DesiredDigest = desiredDigest;
        SetPending(FederationOperationKind.Rename, operation, desiredDigest, at, actor);
        return Result<None>.Ok(None.Value);
    }

    /// <summary>Test start is allowed from Draft only; the window opens when the enabling write applies.</summary>
    public Result<None> BeginTestStart(string desiredDigest, FederationOperationId operation, DateTimeOffset at, UserId actor)
    {
        if (State != FederationConnectionState.Draft || !CanBegin(operation, at, actor) || !ValidDigest(desiredDigest))
            return IdentityFailures.BusinessRule();
        MarkUpdated(at, actor);
        DesiredDigest = desiredDigest;
        SetPending(FederationOperationKind.TestStart, operation, desiredDigest, at, actor);
        return Result<None>.Ok(None.Value);
    }

    /// <summary>Disable moves Active to Disabled before the provider write, so no host admits it meanwhile.</summary>
    public Result<None> BeginDisable(string desiredDigest, FederationOperationId operation, DateTimeOffset at, UserId actor)
    {
        if (State != FederationConnectionState.Active || !CanBegin(operation, at, actor) || !ValidDigest(desiredDigest))
            return IdentityFailures.BusinessRule();
        MarkUpdated(at, actor);
        State = FederationConnectionState.Disabled;
        DesiredDigest = desiredDigest;
        SetPending(FederationOperationKind.Disable, operation, desiredDigest, at, actor);
        return Result<None>.Ok(None.Value);
    }

    /// <summary>Delete stops admission first; the tombstone follows only a confirmed provider absence.</summary>
    public Result<None> BeginDelete(string desiredDigest, FederationOperationId operation, DateTimeOffset at, UserId actor)
    {
        if (!CanBegin(operation, at, actor) || !ValidDigest(desiredDigest)) return IdentityFailures.BusinessRule();
        MarkUpdated(at, actor);
        if (State is FederationConnectionState.TestOpen or FederationConnectionState.TestPassed or FederationConnectionState.Active)
        {
            State = FederationConnectionState.Draft;
            ClearTest();
        }
        DesiredDigest = desiredDigest;
        SetPending(FederationOperationKind.Delete, operation, desiredDigest, at, actor);
        return Result<None>.Ok(None.Value);
    }

    /// <summary>
    /// Complete settles exactly the pending operation. A superseded identifier changes nothing and reports
    /// <see langword="false"/>. Uncertain keeps the operation pending for Resume after the quiet window.
    /// </summary>
    public Result<bool> Complete(FederationOperationId operation, FederationOperationOutcome outcome, string? providerDigest,
        FederationTestAttemptId? attempt, DateTimeOffset at, UserId actor)
    {
        if (IsDeleted || State == FederationConnectionState.Deleted || !ValidActor(actor) || !ValidInstant(at)
            || !Enum.IsDefined(outcome))
            return Result<bool>.Fail(IdentityFailures.Refused());
        if (PendingOperationId is not { } pending || pending != operation) return Result<bool>.Ok(false);
        if (at < PendingStartedAt || !IsMonotonic(at) || Version == long.MaxValue) return Result<bool>.Fail(IdentityFailures.Refused());
        var kind = PendingOperationKind!.Value;
        if (outcome == FederationOperationOutcome.Uncertain)
        {
            if (attempt is not null) return Result<bool>.Fail(IdentityFailures.Refused());
            MarkUpdated(at, actor);
            return Result<bool>.Ok(true);
        }
        if (outcome == FederationOperationOutcome.Applied)
        {
            // Applied means the readback equalled the desired digest; anything else is drift, not success.
            if (!string.Equals(providerDigest, PendingDesiredDigest, StringComparison.Ordinal)
                || (kind == FederationOperationKind.TestStart) != attempt is not null
                || kind == FederationOperationKind.TestStart && (State != FederationConnectionState.Draft || !ValidAttempt(attempt!.Value)))
                return Result<bool>.Fail(IdentityFailures.Refused());
            var starter = PendingActor;
            // SoftDelete advances the version itself; one settled write is exactly one row version.
            if (kind == FederationOperationKind.Delete) SoftDelete(at, actor);
            else MarkUpdated(at, actor);
            LastProviderDigest = providerDigest;
            ClearPending();
            if (kind == FederationOperationKind.TestStart)
            {
                State = FederationConnectionState.TestOpen;
                TestAttemptId = attempt;
                TestActor = starter;
                TestRevision = ConfigurationRevision;
                TestExpiresAt = at + TestWindow;
                TestResult = FederationTestResult.Pending;
            }
            else if (kind == FederationOperationKind.Delete)
            {
                State = FederationConnectionState.Deleted;
                ClearTest();
            }
            return Result<bool>.Ok(true);
        }
        if (attempt is not null || providerDigest is not null && !ValidDigest(providerDigest))
            return Result<bool>.Fail(IdentityFailures.Refused());
        MarkUpdated(at, actor);
        if (providerDigest is not null) LastProviderDigest = providerDigest;
        ClearPending();
        return Result<bool>.Ok(true);
    }

    /// <summary>Resume is eligible only once the uncertain write's quiet window has elapsed.</summary>
    public bool CanResume(DateTimeOffset at) => HasPendingOperation && !IsDeleted && PendingQuietUntil is { } quiet && at >= quiet;

    /// <summary>The BFF callback records the closed result of the current window's attempt only.</summary>
    public Result<None> RecordTestResult(FederationTestAttemptId attempt, bool passed, DateTimeOffset at, UserId actor)
    {
        if (State != FederationConnectionState.TestOpen || TestAttemptId != attempt || HasPendingOperation
            || TestRevision != ConfigurationRevision || !ValidActor(actor) || !ValidInstant(at) || !IsMonotonic(at)
            || at >= TestExpiresAt)
            return IdentityFailures.BusinessRule();
        MarkUpdated(at, actor);
        if (!passed)
        {
            State = FederationConnectionState.Draft;
            ClearTest();
            return Result<None>.Ok(None.Value);
        }
        State = FederationConnectionState.TestPassed;
        TestResult = FederationTestResult.Passed;
        TestPassedAt = at;
        TestExpiresAt = at + ActivationWindow;
        return Result<None>.Ok(None.Value);
    }

    /// <summary>An expired TestOpen or TestPassed window returns to Draft.</summary>
    public Result<None> ExpireTest(DateTimeOffset at, UserId actor)
    {
        if (State is not (FederationConnectionState.TestOpen or FederationConnectionState.TestPassed)
            || TestExpiresAt is not { } expires || at < expires || !ValidActor(actor) || !ValidInstant(at) || !IsMonotonic(at))
            return IdentityFailures.BusinessRule();
        MarkUpdated(at, actor);
        State = FederationConnectionState.Draft;
        ClearTest();
        return Result<None>.Ok(None.Value);
    }

    /// <summary>Activate performs no provider write; the caller supplies the fresh readback digest.</summary>
    public Result<None> Activate(string providerDigest, DateTimeOffset at, UserId actor)
    {
        if (State != FederationConnectionState.TestPassed || HasPendingOperation || TestResult != FederationTestResult.Passed
            || TestRevision != ConfigurationRevision || TestExpiresAt is not { } expires || at >= expires
            || !string.Equals(providerDigest, DesiredDigest, StringComparison.Ordinal) || !ValidActor(actor) || !ValidInstant(at)
            || !IsMonotonic(at))
            return IdentityFailures.BusinessRule();
        MarkUpdated(at, actor);
        LastProviderDigest = providerDigest;
        State = FederationConnectionState.Active;
        ClearTest();
        return Result<None>.Ok(None.Value);
    }

    /// <summary>Enable moves Disabled to Draft without a provider write; a new test is required.</summary>
    public Result<None> Enable(DateTimeOffset at, UserId actor)
    {
        if (State != FederationConnectionState.Disabled || HasPendingOperation || !ValidActor(actor) || !ValidInstant(at)
            || !IsMonotonic(at))
            return IdentityFailures.BusinessRule();
        MarkUpdated(at, actor);
        State = FederationConnectionState.Draft;
        return Result<None>.Ok(None.Value);
    }

    /// <summary>
    /// A readback that differs from the desired digest is drift: the connection returns to Draft from every live
    /// state, Disabled included (ADR-0099 § Provider writes), so a new test is required before it is advertised.
    /// </summary>
    public Result<None> RecordDrift(string providerDigest, DateTimeOffset at, UserId actor)
    {
        if (IsDeleted || State == FederationConnectionState.Deleted || !ValidDigest(providerDigest)
            || string.Equals(providerDigest, DesiredDigest, StringComparison.Ordinal) || !ValidActor(actor) || !ValidInstant(at)
            || !IsMonotonic(at))
            return IdentityFailures.BusinessRule();
        MarkUpdated(at, actor);
        LastProviderDigest = providerDigest;
        State = FederationConnectionState.Draft;
        ClearTest();
        return Result<None>.Ok(None.Value);
    }

    private bool CanBegin(FederationOperationId operation, DateTimeOffset at, UserId actor) =>
        !IsDeleted && State != FederationConnectionState.Deleted && !HasPendingOperation && Version < long.MaxValue
        && ValidId(operation)
        && ValidActor(actor) && ValidInstant(at) && IsMonotonic(at);

    /// <summary>The storage guard refuses an update stamped before the row's last stamp; refuse it here first.</summary>
    private bool IsMonotonic(DateTimeOffset at) => at >= (UpdatedAt ?? CreatedAt);

    private void SetPending(FederationOperationKind kind, FederationOperationId operation, string desiredDigest, DateTimeOffset at, UserId actor)
    {
        PendingOperationKind = kind;
        PendingOperationId = operation;
        PendingActor = actor;
        PendingStartedAt = at;
        PendingQuietUntil = at + QuietWindow;
        PendingDesiredDigest = desiredDigest;
    }

    private void ClearPending()
    {
        PendingOperationKind = null;
        PendingOperationId = null;
        PendingActor = null;
        PendingStartedAt = null;
        PendingQuietUntil = null;
        PendingDesiredDigest = null;
    }

    private void ClearTest()
    {
        TestAttemptId = null;
        TestActor = null;
        TestRevision = null;
        TestExpiresAt = null;
        TestResult = null;
        TestPassedAt = null;
    }

    private static bool ValidConfiguration(FederationProtocol protocol, FederationConfiguration configuration) =>
        Enum.IsDefined(configuration.SourceKind) && ValidDigest(configuration.ConfigurationDigest) && ValidDigest(configuration.DesiredDigest)
        && ValidVisibleAscii(configuration.IssuerOrEntityId, MaximumIssuerLength)
        && (protocol == FederationProtocol.Oidc
            ? ValidVisibleAscii(configuration.ClientId, MaximumClientIdLength) && ValidOidcIssuer(configuration.IssuerOrEntityId)
            : configuration.ClientId is null);

    /// <summary>
    /// An OIDC issuer has no query or fragment (OpenID Connect Discovery 1.0 § 3) and no userinfo in its authority,
    /// so it cannot carry credentials. Mirrors <c>ck_federation_connections_issuer</c>: the authority is the text
    /// between a leading <c>//</c> (the first slash followed by a second) and the next slash. A SAML entity
    /// identifier stays free-form.
    /// </summary>
    private static bool ValidOidcIssuer(string issuer)
    {
        if (issuer.AsSpan().IndexOfAny('?', '#') >= 0) return false;
        var slash = issuer.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0 || slash + 1 >= issuer.Length || issuer[slash + 1] != '/') return true;
        var authority = issuer.AsSpan(slash + 2);
        var end = authority.IndexOf('/');
        return (end < 0 ? authority : authority[..end]).IndexOf('@') < 0;
    }

    private static bool ValidVisibleAscii(string? value, int maximum) => value is { Length: > 0 } text && text.Length <= maximum
        && text.All(character => character is >= '!' and <= '~');

    private static bool ValidAttempt(FederationTestAttemptId attempt) => ValidId(attempt);

    private static bool ValidId<TId>(TId id) where TId : struct, IStronglyTypedId<Guid> =>
        StronglyTypedId.IsAssigned(id) && id.Value.Version == 7;
    private static bool ValidActor(UserId actor) => StronglyTypedId.IsAssigned(actor) && actor.Value.Version == 7 && actor != UserId.SystemActor;
    private static bool ValidInstant(DateTimeOffset at) => at != default && at.Offset == TimeSpan.Zero
        && at <= DateTimeOffset.MaxValue - TestWindow - QuietWindow;
    /// <summary>A lowercase hexadecimal SHA-256 digest, the only digest shape the registry stores.</summary>
    public static bool IsDigest(string? value) => ValidDigest(value);
    internal static bool ValidDigest(string? value) => value is { Length: DigestLength } && value.All(char.IsAsciiHexDigitLower);
}
