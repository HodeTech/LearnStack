using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LearnStack.Tests.Integration;

/// <summary>
/// A real loopback OIDC issuer. Tokens are signed from the supplied JSON bytes,
/// so duplicate properties survive until the real bearer handler reads them.
/// </summary>
internal sealed class TenantAuthenticationIssuer : IAsyncDisposable
{
    internal static readonly Guid Actor = Guid.Parse("019b24b6-0cf0-7000-8000-000000000011");
    internal static readonly Guid Tenant = Guid.Parse("019b24b6-0cf0-7000-8000-000000000022");
    internal static readonly Guid Organization = Guid.Parse("019b24b6-0cf0-7000-8000-000000000033");
    private readonly WebApplication _app;
    private readonly List<TenantAuthenticationSigningKey> _ownedKeys = [];
    private TenantAuthenticationSigningKey[] _publishedKeys = [];
    private int _metadataRequests;
    private int _jwksRequests;
    private int _canaryRequests;

    private TenantAuthenticationIssuer(WebApplication app)
    {
        _app = app;
        CurrentKey = NewKey("initial-key");
        Publish(CurrentKey);
    }

    internal string Issuer { get; private set; } = "";
    internal string MetadataAddress => Issuer + "/.well-known/openid-configuration";
    internal string CanaryAddress => Issuer + "/canary";
    internal TenantAuthenticationSigningKey CurrentKey { get; }
    internal int MetadataRequests => Volatile.Read(ref _metadataRequests);
    internal int JwksRequests => Volatile.Read(ref _jwksRequests);
    internal int CanaryRequests => Volatile.Read(ref _canaryRequests);
    internal string? DiscoveryResponse { get; set; }
    internal string? JwksResponse { get; set; }
    internal string? DiscoveryRedirect { get; set; }
    internal string? JwksRedirect { get; set; }
    internal int DiscoveryStatusCode { get; set; } = StatusCodes.Status200OK;
    internal int JwksStatusCode { get; set; } = StatusCodes.Status200OK;
    internal TimeSpan DiscoveryDelay { get; set; }
    internal TimeSpan JwksDelay { get; set; }
    internal Task? JwksRelease { get; set; }
    internal string DiscoveryDocument => JsonSerializer.Serialize(new
    {
        issuer = Issuer,
        jwks_uri = Issuer + "/keys",
        authorization_endpoint = Issuer + "/authorize",
        token_endpoint = Issuer + "/token",
        response_types_supported = (string[])["code"],
        subject_types_supported = (string[])["public"],
        id_token_signing_alg_values_supported = (string[])["RS256"],
    });

    internal static async Task<TenantAuthenticationIssuer> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        var issuer = new TenantAuthenticationIssuer(app);
        app.MapGet("/realms/learnstack/.well-known/openid-configuration", issuer.Discovery);
        app.MapGet("/realms/learnstack/keys", issuer.Jwks);
        app.MapGet("/realms/learnstack-hub/keys", issuer.Jwks);
        app.MapGet("/realms/learnstack/canary", issuer.Canary);
        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses;
        issuer.Issuer = addresses.Single() + "/realms/learnstack";
        return issuer;
    }

    internal TenantAuthenticationSigningKey NewKey(string? kid = null)
    {
        var key = new TenantAuthenticationSigningKey(kid ?? Guid.NewGuid().ToString("N"));
        _ownedKeys.Add(key);
        return key;
    }

    internal void Publish(params TenantAuthenticationSigningKey[] keys) =>
        Volatile.Write(ref _publishedKeys, keys.ToArray());

    internal string Token(string? payload = null, string? header = null,
        TenantAuthenticationSigningKey? key = null)
    {
        key ??= CurrentKey;
        header ??= Header(key.Kid);
        payload ??= Payload();
        var input = Base64Url(Encoding.UTF8.GetBytes(header)) + "." + Base64Url(Encoding.UTF8.GetBytes(payload));
        var signature = key.Rsa.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return input + "." + Base64Url(signature);
    }

    internal string Payload(params (string Name, object? Value)[] replacements)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var claims = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["iss"] = Issuer,
            ["sub"] = "opaque-keycloak-subject",
            ["aud"] = "learnstack-api",
            ["iat"] = now - 5,
            ["exp"] = now + 600,
            ["azp"] = "learnstack-web",
            ["typ"] = "Bearer",
            ["user_id"] = Actor.ToString("D"),
            ["tenant_id"] = Tenant.ToString("D"),
            ["organization_id"] = Organization.ToString("D"),
            ["scope"] = "openid profile",
        };
        foreach (var (name, value) in replacements) claims[name] = value;
        return JsonSerializer.Serialize(claims);
    }

    internal string Without(params string[] names)
    {
        using var baseline = JsonDocument.Parse(Payload());
        return "{" + string.Join(",", baseline.RootElement.EnumerateObject()
            .Where(property => !names.Contains(property.Name, StringComparer.Ordinal))
            .Select(property => JsonSerializer.Serialize(property.Name) + ":" + property.Value.GetRawText())) + "}";
    }

    internal string PayloadWithRaw(string name, string rawJson)
    {
        using var baseline = JsonDocument.Parse(Payload());
        return "{" + string.Join(",", baseline.RootElement.EnumerateObject().Select(property =>
            JsonSerializer.Serialize(property.Name) + ":" +
            (property.NameEquals(name) ? rawJson : property.Value.GetRawText()))) + "}";
    }

    internal static string Header(string kid) => JsonSerializer.Serialize(new { alg = "RS256", typ = "JWT", kid });

    internal static string AddProperty(string json, string name, object? value) =>
        json[..^1] + "," + JsonSerializer.Serialize(name) + ":" + JsonSerializer.Serialize(value) + "}";

    private async Task Discovery(HttpContext context)
    {
        Interlocked.Increment(ref _metadataRequests);
        if (DiscoveryDelay > TimeSpan.Zero) await Task.Delay(DiscoveryDelay, context.RequestAborted);
        context.Response.StatusCode = DiscoveryStatusCode;
        context.Response.ContentType = "application/json";
        if (DiscoveryRedirect is not null) context.Response.Headers.Location = DiscoveryRedirect;
        await context.Response.WriteAsync(DiscoveryResponse ?? DiscoveryDocument, context.RequestAborted);
    }

    private async Task Jwks(HttpContext context)
    {
        Interlocked.Increment(ref _jwksRequests);
        if (JwksRelease is { } release) await release.WaitAsync(context.RequestAborted);
        if (JwksDelay > TimeSpan.Zero) await Task.Delay(JwksDelay, context.RequestAborted);
        context.Response.StatusCode = JwksStatusCode;
        context.Response.ContentType = "application/json";
        if (JwksRedirect is not null) context.Response.Headers.Location = JwksRedirect;
        await context.Response.WriteAsync(JwksResponse ?? JsonSerializer.Serialize(new
        {
            keys = Volatile.Read(ref _publishedKeys).Select(key => key.PublicJwk),
        }), context.RequestAborted);
    }

    private Task Canary(HttpContext context)
    {
        Interlocked.Increment(ref _canaryRequests);
        return context.Response.WriteAsync("token-supplied URL was dereferenced", context.RequestAborted);
    }

    internal static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        foreach (var key in _ownedKeys) key.Dispose();
    }
}

internal sealed class TenantAuthenticationSigningKey : IDisposable
{
    internal TenantAuthenticationSigningKey(string kid)
    {
        Kid = kid;
        Rsa = RSA.Create(2048);
        var parameters = Rsa.ExportParameters(false);
        PublicJwk = new
        {
            kty = "RSA",
            use = "sig",
            alg = "RS256",
            kid,
            n = TenantAuthenticationIssuer.Base64Url(parameters.Modulus!),
            e = TenantAuthenticationIssuer.Base64Url(parameters.Exponent!),
        };
    }

    internal string Kid { get; }
    internal RSA Rsa { get; }
    internal object PublicJwk { get; }
    public void Dispose() => Rsa.Dispose();
}
