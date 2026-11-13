using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.SharedKernel.Identity;

/// <summary>The two brokered protocols ADR-0099 admits; fixed for a connection's life.</summary>
public enum FederationBrokerProtocol { Oidc = 1, Saml = 2 }

/// <summary>The closed OIDC client-authentication choice of the fixed broker profile (ADR-0099).</summary>
public enum FederationClientAuthentication { ClientSecretPost = 1, ClientSecretBasic = 2 }

/// <summary>
/// The two-sided ownership proof: tenant, connection and the alias LearnStack derived from the connection
/// identifier. The adapter refuses every read or write whose provider owner marker differs (ADR-0099).
/// </summary>
public sealed record FederationBrokerOwner
{
    public const string AliasPrefix = "lsf-";

    public FederationBrokerOwner(TenantId tenantId, Guid connectionId, string alias)
    {
        if (!tenantId.IsInitialized() || tenantId.Value == Guid.Empty || tenantId == TenantId.PlatformSentinel
            || connectionId == Guid.Empty || connectionId.Version != 7
            || !string.Equals(alias, AliasPrefix + connectionId.ToString("N"), StringComparison.Ordinal))
            throw new ArgumentException("Federation broker owner is invalid.");
        TenantId = tenantId;
        ConnectionId = connectionId;
        Alias = alias;
    }

    public TenantId TenantId { get; }
    public Guid ConnectionId { get; }
    public string Alias { get; }
    public override string ToString() => nameof(FederationBrokerOwner);
}

/// <summary>Nonsecret provider-side configuration; the closed allow-list the adapter builds from (ADR-0099).</summary>
public abstract record FederationBrokerConfiguration
{
    private protected FederationBrokerConfiguration() { }
    public abstract FederationBrokerProtocol Protocol { get; }

    /// <summary>Every endpoint the provider later contacts; the egress guard checks each one.</summary>
    public abstract IReadOnlyList<Uri> Endpoints { get; }
}

public sealed record FederationOidcConfiguration(string Issuer, string ClientId, FederationClientAuthentication ClientAuthentication,
    Uri AuthorizationEndpoint, Uri TokenEndpoint, Uri JwksEndpoint, Uri? UserInfoEndpoint) : FederationBrokerConfiguration
{
    public override FederationBrokerProtocol Protocol => FederationBrokerProtocol.Oidc;
    public override IReadOnlyList<Uri> Endpoints => UserInfoEndpoint is null
        ? [AuthorizationEndpoint, TokenEndpoint, JwksEndpoint]
        : [AuthorizationEndpoint, TokenEndpoint, JwksEndpoint, UserInfoEndpoint];
    public override string ToString() => nameof(FederationOidcConfiguration);
}

/// <summary>Signing certificates are public PEM material, never a private key; at most two (ADR-0099 § User experience).</summary>
public sealed record FederationSamlConfiguration(string EntityId, Uri SingleSignOnEndpoint, ImmutableArray<string> SigningCertificates)
    : FederationBrokerConfiguration
{
    public override FederationBrokerProtocol Protocol => FederationBrokerProtocol.Saml;
    public override IReadOnlyList<Uri> Endpoints => [SingleSignOnEndpoint];
    public override string ToString() => nameof(FederationSamlConfiguration);
}

/// <summary>A broker's complete nonsecret desired state: every provider write is this, never a partial patch.</summary>
public sealed record FederationBrokerState(FederationBrokerOwner Owner, string DisplayName, bool Enabled,
    FederationBrokerConfiguration Configuration)
{
    public FederationBrokerProtocol Protocol => Configuration.Protocol;
    public override string ToString() => nameof(FederationBrokerState);
}

/// <summary>
/// A client secret held only in memory for one provider call. It renders, logs and serializes as nothing;
/// it is never persisted, audited or returned (ADR-0099 § Intent-first operations).
/// </summary>
[JsonConverter(typeof(FederationClientSecretRefusingConverter))]
public sealed class FederationClientSecret
{
    /// <summary>Keycloak's masked value, which it reads as "keep the stored secret".</summary>
    public const string MaskLiteral = "**********";
    public const int MaximumBytes = 1024;
    private readonly string _value;

    private FederationClientSecret(string value) => _value = value;

    /// <summary>Refuses an empty value, more than 1,024 UTF-8 bytes, the mask literal and a <c>${</c> vault reference.</summary>
    public static bool TryCreate(string? value, out FederationClientSecret? secret)
    {
        secret = null;
        if (string.IsNullOrEmpty(value) || System.Text.Encoding.UTF8.GetByteCount(value) > MaximumBytes
            || string.Equals(value, MaskLiteral, StringComparison.Ordinal) || value.StartsWith("${", StringComparison.Ordinal))
            return false;
        secret = new FederationClientSecret(value);
        return true;
    }

    /// <summary>The value, for the one adapter call that sends it. Never store, log or return the result.</summary>
    public string Reveal() => _value;
    public override string ToString() => "[redacted]";
}

internal sealed class FederationClientSecretRefusingConverter : JsonConverter<FederationClientSecret>
{
    public override FederationClientSecret Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new JsonException("A federation client secret is never deserialized.");
    public override void Write(Utf8JsonWriter writer, FederationClientSecret value, JsonSerializerOptions options) =>
        throw new JsonException("A federation client secret is never serialized.");
}

/// <summary>One full desired-state write; a null secret keeps the provider's stored value.</summary>
public sealed record FederationBrokerWrite(FederationBrokerState State, FederationClientSecret? Secret)
{
    public override string ToString() => nameof(FederationBrokerWrite);
}

public enum FederationProviderStatus
{
    /// <summary>The provider accepted the call.</summary>
    Applied = 1,
    /// <summary>The provider definitively refused it; nothing changed.</summary>
    Refused = 2,
    /// <summary>The outcome is unknown: unavailable, timed out, or the adapter is not configured.</summary>
    Unavailable = 3,
    /// <summary>The provider object's owner marker names another tenant or connection; it was not touched.</summary>
    Foreign = 4,
}

/// <summary>No raw response, provider message or credential crosses this port.</summary>
public sealed record FederationProviderResult(FederationProviderStatus Status)
{
    public static FederationProviderResult Applied { get; } = new(FederationProviderStatus.Applied);
    public static FederationProviderResult Refused { get; } = new(FederationProviderStatus.Refused);
    public static FederationProviderResult Unavailable { get; } = new(FederationProviderStatus.Unavailable);
    public static FederationProviderResult Foreign { get; } = new(FederationProviderStatus.Foreign);
}

public enum FederationReadbackStatus { Present = 1, Absent = 2, Unavailable = 3, Foreign = 4 }

/// <summary>
/// The nonsecret readback of one owned broker: its state as LearnStack's allow-list reads it, the number of
/// mappers on the alias (any mapper is drift) and whether every fixed-profile key equalled the profile.
/// </summary>
public sealed record FederationBrokerReadback
{
    private FederationBrokerReadback(FederationReadbackStatus status, FederationBrokerState? state, int mapperCount, bool profileConforms)
    {
        Status = status; State = state; MapperCount = mapperCount; ProfileConforms = profileConforms;
    }

    public FederationReadbackStatus Status { get; }
    public FederationBrokerState? State { get; }
    public int MapperCount { get; }
    public bool ProfileConforms { get; }

    public static FederationBrokerReadback Present(FederationBrokerState state, int mapperCount, bool profileConforms) =>
        new(FederationReadbackStatus.Present, state ?? throw new ArgumentNullException(nameof(state)),
            mapperCount >= 0 ? mapperCount : throw new ArgumentOutOfRangeException(nameof(mapperCount)), profileConforms);
    public static FederationBrokerReadback Absent { get; } = new(FederationReadbackStatus.Absent, null, 0, true);
    public static FederationBrokerReadback Unavailable { get; } = new(FederationReadbackStatus.Unavailable, null, 0, false);
    public static FederationBrokerReadback Foreign { get; } = new(FederationReadbackStatus.Foreign, null, 0, false);
    public override string ToString() => nameof(FederationBrokerReadback);
}

/// <summary>
/// ADR-0099's narrow provider port, beside <see cref="IIdentityEventProvider"/> and not ADR-0004's broader
/// identity-provider port. It admits only a full desired-state create or update, an owned readback of the
/// representation and its mappers, and a delete. It takes no URL to fetch and performs no discovery.
/// Only Identity's federation operation runner and the Infrastructure adapter hold it.
/// </summary>
public interface IFederationProvider
{
    /// <summary>Creates the broker when absent, otherwise replaces it with exactly this desired state.</summary>
    Task<FederationProviderResult> ApplyAsync(FederationBrokerWrite write, CancellationToken cancellationToken = default);

    /// <summary>Reads the owned broker's representation and mappers; the readback activation and Resume require.</summary>
    Task<FederationBrokerReadback> ReadAsync(FederationBrokerOwner owner, CancellationToken cancellationToken = default);

    /// <summary>Deletes the owned broker; an absent broker is already deleted.</summary>
    Task<FederationProviderResult> DeleteAsync(FederationBrokerOwner owner, CancellationToken cancellationToken = default);
}

/// <summary>
/// The fail-closed default while the federation admin client is not configured: every call reports
/// <see cref="FederationProviderStatus.Unavailable"/> without any network or provider effect (ADR-0099 § Authority).
/// </summary>
public sealed class UnconfiguredFederationProvider : IFederationProvider
{
    public Task<FederationProviderResult> ApplyAsync(FederationBrokerWrite write, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        return Task.FromResult(FederationProviderResult.Unavailable);
    }

    public Task<FederationBrokerReadback> ReadAsync(FederationBrokerOwner owner, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return Task.FromResult(FederationBrokerReadback.Unavailable);
    }

    public Task<FederationProviderResult> DeleteAsync(FederationBrokerOwner owner, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return Task.FromResult(FederationProviderResult.Unavailable);
    }
}
