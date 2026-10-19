using System.Text.Json;
using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.Api.Authentication;

internal enum TenantTokenFailure
{
    None,
    Header,
    Claims,
    Purpose,
    Identity,
    Lifetime,
    Scope,
}

internal static class TenantTokenProfile
{
    private const long MaximumLifetimeSeconds = 60 * 60;
    private const long MinimumUnixSeconds = -62135596800;
    private const long MaximumUnixSeconds = 253402300799;

    public static bool HasClosedJoseHeader(string token)
    {
        if (!TryGetCompactSegments(token, out var headerEnd, out _))
        {
            return false;
        }

        try
        {
            using var document = ParseSegment(token.AsSpan(0, headerEnd));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var fields = new Dictionary<string, string>(3, StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (
                    property.Name is not ("alg" or "typ" or "kid")
                    || property.Value.ValueKind != JsonValueKind.String
                    || property.Value.GetString() is not { Length: > 0 } value
                    || !fields.TryAdd(property.Name, value)
                )
                {
                    return false;
                }
            }

            return fields.Count == 3
                && fields["alg"] == "RS256"
                && fields["typ"] == "JWT"
                && !string.IsNullOrWhiteSpace(fields["kid"])
                && fields["kid"].Length <= 128;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            // JsonDocument defers decoding escaped Unicode until GetString or
            // Name is read; an unpaired surrogate fails there, after parsing.
            return false;
        }
    }

    // This projection is called only after the stock handler has validated the
    // signature against the configured issuer's keys. Parsing never authenticates.
    public static bool TryReadValidated(
        string token,
        TenantAuthenticationOptions settings,
        DateTimeOffset now,
        out ValidatedTenantIdentity? identity,
        out TenantTokenFailure failure
    )
    {
        identity = null;
        failure = TenantTokenFailure.Header;
        if (!HasClosedJoseHeader(token))
        {
            return false;
        }

        _ = TryGetCompactSegments(token, out var headerEnd, out var payloadEnd);
        failure = TenantTokenFailure.Claims;
        try
        {
            using var document = ParseSegment(
                token.AsSpan(headerEnd + 1, payloadEnd - headerEnd - 1)
            );
            if (!TryReadSecurityProperties(document.RootElement, out var claims))
            {
                return false;
            }

            if (
                !TryReadNonemptyString(claims, "iss", out var issuer)
                || !string.Equals(issuer, settings.Issuer, StringComparison.Ordinal)
                || !HasRequiredAudience(claims)
            )
            {
                return false;
            }

            failure = TenantTokenFailure.Purpose;
            if (
                !TryReadNonemptyString(claims, "typ", out var type)
                || type != "Bearer"
                || !TryReadNonemptyString(claims, "azp", out var client)
                || client != TenantAuthenticationOptions.ClientId
            )
            {
                return false;
            }

            failure = TenantTokenFailure.Lifetime;
            if (!HasValidLifetime(claims, now))
            {
                return false;
            }

            failure = TenantTokenFailure.Scope;
            if (!HasAllowedScope(claims))
            {
                return false;
            }

            failure = TenantTokenFailure.Identity;
            if (
                !TryReadNonemptyString(claims, "sub", out var subject)
                || subject.Length > 255
                || !TryReadUuid(claims, "user_id", out var actor)
                || actor == UserId.SystemActor.Value
                || !TryReadTenantScope(claims, out var tenantId, out var organizationId)
            )
            {
                return false;
            }

            identity = new ValidatedTenantIdentity(
                issuer,
                subject,
                UserId.From(actor),
                tenantId,
                organizationId
            );
            failure = TenantTokenFailure.None;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TryGetCompactSegments(
        string token,
        out int headerEnd,
        out int payloadEnd
    )
    {
        headerEnd = 0;
        payloadEnd = 0;
        if (string.IsNullOrEmpty(token) || token.Length > TenantAuthenticationOptions.MaxTokenBytes)
        {
            return false;
        }

        headerEnd = token.IndexOf('.');
        if (headerEnd <= 0)
        {
            return false;
        }

        payloadEnd = token.IndexOf('.', headerEnd + 1);
        return payloadEnd > headerEnd + 1
            && payloadEnd < token.Length - 1
            && IsBase64Url(token.AsSpan(0, headerEnd))
            && IsBase64Url(token.AsSpan(headerEnd + 1, payloadEnd - headerEnd - 1))
            && IsBase64Url(token.AsSpan(payloadEnd + 1));
    }

    private static bool IsBase64Url(ReadOnlySpan<char> value)
    {
        var remainder = value.Length % 4;
        if (remainder == 1)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (Base64UrlValue(character) < 0)
            {
                return false;
            }
        }

        // Unused low bits must be zero; otherwise multiple compact spellings
        // decode to the same bytes even without padding.
        var last = Base64UrlValue(value[^1]);
        return remainder switch
        {
            2 => (last & 15) == 0,
            3 => (last & 3) == 0,
            _ => true,
        };
    }

    private static int Base64UrlValue(char character) =>
        character switch
        {
            >= 'A' and <= 'Z' => character - 'A',
            >= 'a' and <= 'z' => character - 'a' + 26,
            >= '0' and <= '9' => character - '0' + 52,
            '-' => 62,
            '_' => 63,
            _ => -1,
        };

    private static JsonDocument ParseSegment(ReadOnlySpan<char> segment)
    {
        var encoded = segment.ToString().Replace('-', '+').Replace('_', '/');
        var padding = (4 - encoded.Length % 4) % 4;
        var bytes = Convert.FromBase64String(encoded + new string('=', padding));
        return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
    }

    private static bool TryReadSecurityProperties(
        JsonElement payload,
        out Dictionary<string, JsonElement> claims
    )
    {
        claims = new Dictionary<string, JsonElement>(12, StringComparer.Ordinal);
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var property in payload.EnumerateObject())
        {
            if (
                property.Name
                    is "iss"
                        or "sub"
                        or "aud"
                        or "exp"
                        or "iat"
                        or "nbf"
                        or "azp"
                        or "typ"
                        or "user_id"
                        or "tenant_id"
                        or "organization_id"
                        or "scope"
                && !claims.TryAdd(property.Name, property.Value)
            )
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryReadNonemptyString(
        Dictionary<string, JsonElement> claims,
        string name,
        out string value
    )
    {
        value = string.Empty;
        if (
            !claims.TryGetValue(name, out var property)
            || property.ValueKind != JsonValueKind.String
            || property.GetString() is not { Length: > 0 } text
            || string.IsNullOrWhiteSpace(text)
        )
        {
            return false;
        }

        value = text;
        return true;
    }

    private static bool HasRequiredAudience(Dictionary<string, JsonElement> claims)
    {
        if (!claims.TryGetValue("aud", out var audience))
        {
            return false;
        }

        if (audience.ValueKind == JsonValueKind.String)
        {
            return audience.GetString() == TenantAuthenticationOptions.Audience;
        }

        if (audience.ValueKind != JsonValueKind.Array || audience.GetArrayLength() == 0)
        {
            return false;
        }

        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in audience.EnumerateArray())
        {
            if (
                item.ValueKind != JsonValueKind.String
                || item.GetString() is not { Length: > 0 } text
                || !values.Add(text)
            )
            {
                return false;
            }
        }

        return values.Contains(TenantAuthenticationOptions.Audience);
    }

    private static bool HasValidLifetime(
        Dictionary<string, JsonElement> claims,
        DateTimeOffset now
    )
    {
        if (
            !TryReadUnixSeconds(claims, "iat", out var issuedAt)
            || !TryReadUnixSeconds(claims, "exp", out var expiresAt)
            || expiresAt <= issuedAt
            || expiresAt - issuedAt > MaximumLifetimeSeconds
        )
        {
            return false;
        }

        var skewTicks = TenantAuthenticationOptions.ClockSkew.Ticks;
        if (
            DateTimeOffset.FromUnixTimeSeconds(issuedAt).UtcTicks > now.UtcTicks + skewTicks
            || DateTimeOffset.FromUnixTimeSeconds(expiresAt).UtcTicks < now.UtcTicks - skewTicks
        )
        {
            return false;
        }

        return !claims.ContainsKey("nbf")
            || (
                TryReadUnixSeconds(claims, "nbf", out var notBefore)
                && notBefore <= expiresAt
                && DateTimeOffset.FromUnixTimeSeconds(notBefore).UtcTicks <= now.UtcTicks + skewTicks
            );
    }

    private static bool TryReadUnixSeconds(
        Dictionary<string, JsonElement> claims,
        string name,
        out long value
    )
    {
        value = 0;
        return claims.TryGetValue(name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt64(out value)
            && value is >= MinimumUnixSeconds and <= MaximumUnixSeconds;
    }

    private static bool HasAllowedScope(Dictionary<string, JsonElement> claims)
    {
        if (!claims.ContainsKey("scope"))
        {
            return true;
        }

        if (!TryReadNonemptyString(claims, "scope", out var scope))
        {
            return false;
        }

        var tokenStart = 0;
        for (var index = 0; index <= scope.Length; index++)
        {
            if (index == scope.Length || scope[index] == ' ')
            {
                if (
                    index == tokenStart
                    || scope.AsSpan(tokenStart, index - tokenStart).SequenceEqual("platform")
                )
                {
                    return false;
                }

                tokenStart = index + 1;
            }
            else if (scope[index] is < '!' or > '~' or '"' or '\\')
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryReadUuid(
        Dictionary<string, JsonElement> claims,
        string name,
        out Guid value
    )
    {
        value = Guid.Empty;
        return TryReadNonemptyString(claims, name, out var text)
            && text.Length == 36
            && Guid.TryParseExact(text, "D", out value)
            && value != Guid.Empty;
    }

    private static bool TryReadTenantScope(
        Dictionary<string, JsonElement> claims,
        out TenantId? tenantId,
        out OrganizationId? organizationId
    )
    {
        tenantId = null;
        organizationId = null;
        if (claims.ContainsKey("tenant_id"))
        {
            if (
                !TryReadUuid(claims, "tenant_id", out var tenant)
                || tenant == TenantId.PlatformSentinel.Value
            )
            {
                return false;
            }

            tenantId = TenantId.From(tenant);
        }

        if (claims.ContainsKey("organization_id"))
        {
            if (!tenantId.HasValue || !TryReadUuid(claims, "organization_id", out var organization))
            {
                return false;
            }

            organizationId = OrganizationId.From(organization);
        }

        return true;
    }
}
