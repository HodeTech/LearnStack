using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;

namespace LearnStack.Api.Authentication;

/// <summary>Qualified native-purpose keys; neither public hop trust nor browser input can replace them.</summary>
internal sealed class NativeSessionSourcePolicy
{
    internal const string CurrentSecretKey = "Identity:NativeSession:CurrentSecret";
    internal const string PreviousSecretKey = "Identity:NativeSession:PreviousSecret";
    internal const string RetirementKey = "Identity:NativeSession:PreviousRetiresAt";
    internal const string SecretHeader = "X-LearnStack-Native-Session-Secret";
    internal const string TenantHeader = "X-LearnStack-Native-Tenant-Id";
    internal const string OrganizationHeader = "X-LearnStack-Native-Organization-Id";

    private readonly byte[]? _current;
    private readonly byte[]? _previous;
    private readonly DateTimeOffset? _previousRetiresAt;

    private NativeSessionSourcePolicy(byte[]? current, byte[]? previous, DateTimeOffset? retirement)
    {
        _current = current;
        _previous = previous;
        _previousRetiresAt = retirement;
    }

    internal bool IsConfigured => _current is not null;

    internal static NativeSessionSourcePolicy Create(string? current, string? previous,
        string? retirement, IReadOnlyList<string> publicHopSecrets, DateTimeOffset qualifiedAt)
    {
        ArgumentNullException.ThrowIfNull(publicHopSecrets);
        if (string.IsNullOrEmpty(current) && string.IsNullOrEmpty(previous) && string.IsNullOrEmpty(retirement))
            return new(null, null, null);

        if (!TryDecode(current, out var currentBytes)) throw Refused();
        if (publicHopSecrets.Any(value => EqualWireValue(current!, value))) throw Refused();

        if (previous is null && retirement is null) return new(currentBytes, null, null);
        if (!TryDecode(previous, out var previousBytes)
            || CryptographicOperations.FixedTimeEquals(currentBytes, previousBytes)
            || publicHopSecrets.Any(value => EqualWireValue(previous!, value))
            || retirement is null
            || !(retirement.EndsWith('Z') || retirement.EndsWith("+00:00", StringComparison.Ordinal))
            || !DateTimeOffset.TryParse(retirement, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var retiresAt)
            || retiresAt.Offset != TimeSpan.Zero || retiresAt > qualifiedAt.AddMinutes(15))
            throw Refused();

        // An absolute instant in the past stays expired after restart. It never
        // becomes a newly qualified overlap window.
        return new(currentBytes, previousBytes, retiresAt);
    }

    internal bool Matches(StringValues presented, DateTimeOffset now)
    {
        if (_current is null || presented.Count != 1 || !TryDecode(presented[0], out var candidate)) return false;
        try
        {
            var currentMatches = CryptographicOperations.FixedTimeEquals(candidate, _current);
            var previousMatches = _previous is not null
                && CryptographicOperations.FixedTimeEquals(candidate, _previous);
            return currentMatches | (previousMatches && _previousRetiresAt is { } retirement && now < retirement);
        }
        finally { CryptographicOperations.ZeroMemory(candidate); }
    }

    internal static bool IsNativeHeader(string name) =>
        name.StartsWith("X-LearnStack-Native-", StringComparison.OrdinalIgnoreCase);

    internal static bool IsKnownHeader(string name) =>
        string.Equals(name, SecretHeader, StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, TenantHeader, StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, OrganizationHeader, StringComparison.OrdinalIgnoreCase);

    internal static bool HasNativeHeaders(IHeaderDictionary headers)
    {
        foreach (var header in headers)
            if (IsNativeHeader(header.Key)) return true;
        return false;
    }

    internal static bool HasOnlyKnownNativeHeaders(IHeaderDictionary headers)
    {
        foreach (var header in headers)
            if (IsNativeHeader(header.Key) && !IsKnownHeader(header.Key)) return false;
        return true;
    }

    public override string ToString() => nameof(NativeSessionSourcePolicy);

    private static bool TryDecode(string? value, out byte[] bytes)
    {
        bytes = [];
        if (value is null || value.Length != 43
            || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
            return false;
        byte[] decoded;
        try { decoded = WebEncoders.Base64UrlDecode(value); }
        catch (FormatException) { return false; }
        if (decoded.Length != 32 || WebEncoders.Base64UrlEncode(decoded) != value
            || decoded.All(item => item == decoded[0]))
        {
            CryptographicOperations.ZeroMemory(decoded);
            return false;
        }
        bytes = decoded;
        return true;
    }

    private static bool EqualWireValue(string candidate, string other) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(candidate), Encoding.UTF8.GetBytes(other));

    private static InvalidOperationException Refused() =>
        new("Native session source configuration is invalid.");
}
