using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace LearnStack.Tests.Integration.TenancyIdentity;

/// <summary>In-memory credentials with safe diagnostics and an observed exchange interval.</summary>
internal sealed class KeycloakTokenExchange
{
    internal HttpStatusCode StatusCode { get; private init; }
    internal string? Error { get; private init; }
    internal string AccessToken { get; private init; } = "";
    internal string IdToken { get; private init; } = "";
    internal string RefreshToken { get; private init; } = "";
    internal KeycloakIssuanceWindow Window { get; private init; } = null!; // Every exchange records a window.

    internal static async Task<KeycloakTokenExchange> RequestAsync(HttpClient client, IReadOnlyDictionary<string, string> form,
        string realm = "learnstack")
    {
        realm = KeycloakFixture.CheckedRealm(realm);
        var started = DateTimeOffset.UtcNow;
        var timestamp = Stopwatch.GetTimestamp();
        using var response = await KeycloakHttp.SendAsync(client, HttpMethod.Post,
            "realms/" + realm + "/protocol/openid-connect/token", new FormUrlEncodedContent(form));
        var completed = DateTimeOffset.UtcNow;
        var elapsed = Stopwatch.GetElapsedTime(timestamp);
        using var json = await KeycloakHttp.ReadJsonAsync(response);
        var body = json.RootElement;
        return new KeycloakTokenExchange
        {
            StatusCode = response.StatusCode,
            Error = KeycloakHttp.OAuthError(body),
            AccessToken = Read(body, "access_token"),
            IdToken = Read(body, "id_token"),
            RefreshToken = Read(body, "refresh_token"),
            Window = new KeycloakIssuanceWindow(started, completed, elapsed),
        };
    }

    private static string Read(JsonElement body, string name) => body.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    public override string ToString() => "Keycloak token exchange (credentials omitted)";
}

/// <summary>
/// The disposable Docker provider shares the host clock. Its explicit two-second qualification allowance
/// is smaller than the API's existing skew; it is test evidence, never production acceptance policy.
/// </summary>
internal sealed record KeycloakIssuanceWindow(DateTimeOffset StartedUtc, DateTimeOffset CompletedUtc, TimeSpan Elapsed)
{
    internal static readonly TimeSpan ClockTolerance = TimeSpan.FromSeconds(2);

    internal bool AcceptsFresh(JsonElement payload, int lifespan)
    {
        var wall = CompletedUtc - StartedUtc;
        if (wall < TimeSpan.Zero || Elapsed < TimeSpan.Zero
            || Elapsed > KeycloakHttp.RequestBudget + TimeSpan.FromSeconds(1)
            || (wall - Elapsed).Duration() > ClockTolerance
            || !payload.TryGetProperty("iat", out var issued) || issued.ValueKind != JsonValueKind.Number || !issued.TryGetInt64(out var iat)
            || !payload.TryGetProperty("exp", out var expires) || expires.ValueKind != JsonValueKind.Number || !expires.TryGetInt64(out var exp)) return false;
        var tolerance = (long)ClockTolerance.TotalSeconds;
        return iat >= StartedUtc.ToUnixTimeSeconds() - tolerance
            && iat <= CompletedUtc.ToUnixTimeSeconds() + tolerance
            && exp >= StartedUtc.ToUnixTimeSeconds() + lifespan - tolerance
            && exp <= CompletedUtc.ToUnixTimeSeconds() + lifespan + tolerance;
    }
}

internal static class KeycloakTokenVerifier
{
    internal static async Task<JsonElement> ValidateAsync(KeycloakFixture fixture, string token, string audience,
        string realm = "learnstack")
    {
        try
        {
            realm = KeycloakFixture.CheckedRealm(realm);
            using var client = KeycloakHttp.Create(fixture.BaseAddress, fixture.RootCertificate);
            using var keys = await KeycloakHttp.JsonAsync(client, HttpMethod.Get,
                "realms/" + realm + "/protocol/openid-connect/certs");
            var result = await new JsonWebTokenHandler { MapInboundClaims = false }.ValidateTokenAsync(token,
                new TokenValidationParameters
                {
                    ValidIssuer = fixture.IssuerFor(realm),
                    ValidAudience = audience,
                    IssuerSigningKeys = new JsonWebKeySet(keys.RootElement.GetRawText()).GetSigningKeys(),
                    RequireSignedTokens = true,
                    RequireExpirationTime = true,
                    ValidateIssuerSigningKey = true,
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                    ClockSkew = TimeSpan.FromSeconds(30),
                });
            if (!result.IsValid) throw new InvalidOperationException("Provider signature/profile validation failed.");
            using var payload = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(token.Split('.')[1]));
            return payload.RootElement.Clone();
        }
        catch (Exception)
        {
            // Stock-handler failures and parser exceptions must never stringify a raw credential.
            throw new InvalidOperationException("Keycloak fixture token validation failed.");
        }
    }

    internal static JsonElement Header(string token)
    {
        try
        {
            using var header = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(token.Split('.')[0]));
            return header.RootElement.Clone();
        }
        catch (Exception) { throw new InvalidOperationException("Keycloak fixture token header is malformed."); }
    }
}
