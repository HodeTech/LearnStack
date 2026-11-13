using System.Collections.Concurrent;
using System.Collections.Immutable;
using LearnStack.SharedKernel.Identity;

namespace LearnStack.Tests.Fixtures;

/// <summary>
/// Test-only ADR-0099 provider fake: owned brokers keyed by alias, full-state writes, owner-checked readbacks.
/// Hooks let a test refuse, stall, drift or observe a call; <see cref="BeforeCall"/> lets it assert that no
/// database transaction is open while the provider is called.
/// </summary>
internal sealed class InMemoryFederationProvider : IFederationProvider
{
    private readonly ConcurrentDictionary<string, Broker> _brokers = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _calls = new();

    internal sealed record Broker(FederationBrokerState State, bool HasSecret, int MapperCount = 0, bool ProfileConforms = true);

    /// <summary>Runs before every call; a test asserts here that no transaction or unit of work is active.</summary>
    internal Action<string>? BeforeCall { get; set; }

    /// <summary>Replaces the apply result; null applies the write.</summary>
    internal Func<FederationBrokerWrite, CancellationToken, Task<FederationProviderResult?>>? OnApply { get; set; }

    /// <summary>Replaces the readback; null reads the stored broker.</summary>
    internal Func<FederationBrokerOwner, CancellationToken, Task<FederationBrokerReadback?>>? OnRead { get; set; }

    internal Func<FederationBrokerOwner, CancellationToken, Task<FederationProviderResult?>>? OnDelete { get; set; }

    internal IReadOnlyCollection<string> Calls => [.. _calls];
    internal int SecretWrites { get; private set; }
    internal string? LastSecret { get; private set; }

    internal Broker? Find(string alias) => _brokers.TryGetValue(alias, out var broker) ? broker : null;
    internal void Put(string alias, Broker broker) => _brokers[alias] = broker;

    public async Task<FederationProviderResult> ApplyAsync(FederationBrokerWrite write, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        Record("apply");
        if (OnApply is { } hook && await hook(write, cancellationToken) is { } replaced) return replaced;
        cancellationToken.ThrowIfCancellationRequested();
        var alias = write.State.Owner.Alias;
        if (_brokers.TryGetValue(alias, out var existing) && existing.State.Owner != write.State.Owner) return FederationProviderResult.Foreign;
        if (write.Secret is { } secret) { SecretWrites++; LastSecret = secret.Reveal(); }
        _brokers[alias] = new Broker(write.State, write.Secret is not null || existing?.HasSecret == true);
        return FederationProviderResult.Applied;
    }

    public async Task<FederationBrokerReadback> ReadAsync(FederationBrokerOwner owner, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        Record("read");
        if (OnRead is { } hook && await hook(owner, cancellationToken) is { } replaced) return replaced;
        cancellationToken.ThrowIfCancellationRequested();
        if (!_brokers.TryGetValue(owner.Alias, out var broker)) return FederationBrokerReadback.Absent;
        return broker.State.Owner != owner ? FederationBrokerReadback.Foreign
            : FederationBrokerReadback.Present(broker.State, broker.MapperCount, broker.ProfileConforms);
    }

    public async Task<FederationProviderResult> DeleteAsync(FederationBrokerOwner owner, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        Record("delete");
        if (OnDelete is { } hook && await hook(owner, cancellationToken) is { } replaced) return replaced;
        cancellationToken.ThrowIfCancellationRequested();
        if (_brokers.TryGetValue(owner.Alias, out var broker) && broker.State.Owner != owner) return FederationProviderResult.Foreign;
        _brokers.TryRemove(owner.Alias, out _);
        return FederationProviderResult.Applied;
    }

    private void Record(string call)
    {
        _calls.Enqueue(call);
        BeforeCall?.Invoke(call);
    }
}

/// <summary>Test-only guarded fetcher: fixed public documents for the example hosts, refusal for anything else.</summary>
internal sealed class InMemoryFederationEgressFetcher : IFederationEgressFetcher
{
    internal const string Issuer = "https://idp.example.com/realms/acme";
    internal static readonly Uri Discovery = new(Issuer + "/.well-known/openid-configuration");
    internal static readonly Uri Metadata = new("https://saml.example.com/metadata");
    internal FederationEgressStatus Status { get; set; } = FederationEgressStatus.Admitted;
    internal int Calls { get; private set; }

    /// <summary>A self-signed test certificate in PEM, public material only.</summary>
    internal static string Certificate { get; } = CreateCertificate();

    public Task<FederationEgressResult<FederationOidcDiscovery>> FetchOidcDiscoveryAsync(Uri discoveryUrl, CancellationToken cancellationToken = default) =>
        Answer(discoveryUrl == Discovery, () => new FederationOidcDiscovery(Issuer, new Uri(Issuer + "/protocol/openid-connect/auth"),
            new Uri(Issuer + "/protocol/openid-connect/token"), new Uri(Issuer + "/protocol/openid-connect/certs"),
            new Uri(Issuer + "/protocol/openid-connect/userinfo")));

    public Task<FederationEgressResult<FederationJwksDocument>> FetchJwksAsync(Uri jwksUrl, CancellationToken cancellationToken = default) =>
        Answer(jwksUrl.Host.EndsWith("example.com", StringComparison.Ordinal), () => new FederationJwksDocument(1));

    public Task<FederationEgressResult<FederationSamlMetadata>> FetchSamlMetadataAsync(Uri metadataUrl, CancellationToken cancellationToken = default) =>
        Answer(metadataUrl == Metadata, () => new FederationSamlMetadata("https://saml.example.com/entity",
            new Uri("https://saml.example.com/sso"), [Certificate]));

    public Task<FederationEgressResult<FederationEndpointAdmission>> CheckEndpointAsync(Uri endpoint, CancellationToken cancellationToken = default) =>
        Answer(endpoint.Host.EndsWith("example.com", StringComparison.Ordinal), () => FederationEndpointAdmission.Instance);

    private Task<FederationEgressResult<T>> Answer<T>(bool known, Func<T> value) where T : class
    {
        Calls++;
        return Task.FromResult(Status switch
        {
            FederationEgressStatus.Admitted when known => FederationEgressResult.Admitted(value()),
            FederationEgressStatus.Unavailable => FederationEgressResult.Unavailable<T>(),
            _ => FederationEgressResult.Refused<T>(),
        });
    }

    private static string CreateCertificate()
    {
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=federation-test", key,
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        using var certificate = request.CreateSelfSigned(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2036, 1, 1, 0, 0, 0, TimeSpan.Zero));
        return certificate.ExportCertificatePem();
    }

    internal static ImmutableArray<string> Certificates => [Certificate];
}
