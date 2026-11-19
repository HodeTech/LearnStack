using LearnStack.SharedKernel.Platform;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace LearnStack.Infrastructure.PlatformOperator;

/// <summary>Why a cryptographically valid operator token failed the closed profile.</summary>
public enum PlatformOperatorTokenCause
{
    Subject = 0,
    Issuer,
    AuthorizedParty,
    AuthenticationContext,
    IssuedAt,
    AuthenticationTime,
    Lifetime,
    AuthenticationAge,
}

/// <summary>The bounded label set a counter carries; nothing a caller wrote reaches it.</summary>
public static class PlatformOperatorTokenCauseLabels
{
    public static string Label(this PlatformOperatorTokenCause cause) => cause switch
    {
        PlatformOperatorTokenCause.Subject => "profile_subject",
        PlatformOperatorTokenCause.Issuer => "profile_issuer",
        PlatformOperatorTokenCause.AuthorizedParty => "profile_azp",
        PlatformOperatorTokenCause.AuthenticationContext => "profile_acr",
        PlatformOperatorTokenCause.IssuedAt => "profile_iat",
        PlatformOperatorTokenCause.AuthenticationTime => "profile_auth_time",
        PlatformOperatorTokenCause.Lifetime => "profile_lifetime",
        PlatformOperatorTokenCause.AuthenticationAge => "profile_auth_age",
        _ => "profile_invalid",
    };
}

/// <summary>Carries a closed profile label from <c>TokenValidated</c> to the challenge.</summary>
public sealed class PlatformOperatorProfileException(string label) : Exception("Operator credential refused.")
{
    public string Label { get; } = label;
}

/// <summary>Maps an authentication failure to a closed label; the response never varies.</summary>
public static class PlatformOperatorFailure
{
    public static string Classify(Exception? failure) => failure switch
    {
        null => "token_missing",
        PlatformOperatorProfileException profile => profile.Label,
        AggregateException { InnerExceptions.Count: > 0 } aggregate => Classify(aggregate.InnerExceptions[0]),
        SecurityTokenExpiredException => "token_expired",
        SecurityTokenNotYetValidException or SecurityTokenNoExpirationException or SecurityTokenInvalidLifetimeException
            => "token_lifetime",
        SecurityTokenInvalidSignatureException or SecurityTokenSignatureKeyNotFoundException => "token_signature",
        SecurityTokenInvalidIssuerException => "token_issuer",
        SecurityTokenInvalidAudienceException => "token_audience",
        SecurityTokenInvalidAlgorithmException => "token_algorithm",
        SecurityTokenInvalidTypeException => "token_type",
        _ when IsMetadataFailure(failure) => "metadata_unavailable",
        _ => "token_invalid",
    };

    // The stock manager wraps every retrieval failure in an InvalidOperationException (IDX20803)
    // whose cause is the transport or the retriever's own refusal.
    private static bool IsMetadataFailure(Exception failure)
    {
        for (var current = failure; current is not null; current = current.InnerException)
        {
            if (current is IOException or HttpRequestException or TaskCanceledException)
            {
                return true;
            }
        }

        return failure is InvalidOperationException && failure.Message.Contains("IDX20803", StringComparison.Ordinal);
    }
}

/// <summary>
/// The closed profile a cryptographically valid operator token must also meet (ADR-0101 § 2, § 3).
/// </summary>
/// <remarks>
/// <para>
/// A pure function of the validated token, the settings and the clock, so the scheme, the real-realm
/// proof and the offline rebind tool judge one definition. It admits the pinned <c>iss</c>, an
/// allow-listed <c>azp</c>, an accepted <c>acr</c>, a lifetime of at most 15 minutes, and an
/// <c>auth_time</c> that is neither in the future nor older than the configured age. Nothing else
/// in the token carries authority.
/// </para>
/// <para>
/// <b>It owns the witness minter.</b> The minter is nested and private, so no other type in the
/// assembly can mint a witness, and a source census pins who may call <see cref="TryRead"/>.
/// </para>
/// </remarks>
public static class PlatformOperatorTokenProfile
{
    public static bool TryRead(
        JsonWebToken token,
        PlatformOperatorSettings settings,
        DateTimeOffset now,
        string executor,
        string? correlationId,
        out PlatformOperatorWitness? witness,
        out PlatformOperatorTokenCause cause)
    {
        witness = null;
        cause = default;

        // Two executors exist and each is a constant: a caller cannot name another in an audit row's attribution.
        if (executor is not (PlatformOperatorSettings.Executor or PlatformOperatorSettings.RebindToolExecutor))
        {
            throw new ArgumentException("The executor is not a registered operator executor.", nameof(executor));
        }

        // Stock validation already pinned the issuer; this restates it so the profile is
        // correct for any caller, including one that validated with other parameters.
        if (!string.Equals(token.Issuer, settings.Issuer, StringComparison.Ordinal))
        {
            cause = PlatformOperatorTokenCause.Issuer;
            return false;
        }

        if (string.IsNullOrWhiteSpace(token.Subject))
        {
            cause = PlatformOperatorTokenCause.Subject;
            return false;
        }

        if (!token.TryGetPayloadValue<string>("azp", out var azp) || !settings.AuthorizedParties.Contains(azp))
        {
            cause = PlatformOperatorTokenCause.AuthorizedParty;
            return false;
        }

        if (!token.TryGetPayloadValue<string>("acr", out var acr) || !settings.AcceptedAcr.Contains(acr))
        {
            cause = PlatformOperatorTokenCause.AuthenticationContext;
            return false;
        }

        if (!token.TryGetPayloadValue<long>("iat", out var iat) || !IsUnixSeconds(iat))
        {
            cause = PlatformOperatorTokenCause.IssuedAt;
            return false;
        }

        if (token.ValidTo == DateTime.MinValue)
        {
            cause = PlatformOperatorTokenCause.Lifetime;
            return false;
        }

        if (!token.TryGetPayloadValue<long>("auth_time", out var authTime) || !IsUnixSeconds(authTime))
        {
            cause = PlatformOperatorTokenCause.AuthenticationTime;
            return false;
        }

        var issuedAt = DateTimeOffset.FromUnixTimeSeconds(iat);
        var authenticatedAt = DateTimeOffset.FromUnixTimeSeconds(authTime);
        var lifetime = new DateTimeOffset(token.ValidTo, TimeSpan.Zero) - issuedAt;
        if (lifetime <= TimeSpan.Zero || lifetime > PlatformOperatorSettings.MaxTokenLifetime)
        {
            cause = PlatformOperatorTokenCause.Lifetime;
            return false;
        }

        if (authenticatedAt > now + PlatformOperatorSettings.ClockSkew || now - authenticatedAt > settings.MaxAuthenticationAge)
        {
            cause = PlatformOperatorTokenCause.AuthenticationAge;
            return false;
        }

        witness = WitnessMinter.Create(
            settings.Issuer,
            PlatformOperatorSubject.Digest(settings.Issuer, token.Subject),
            acr,
            authenticatedAt,
            executor,
            correlationId);
        return true;
    }

    // DateTimeOffset.FromUnixTimeSeconds throws outside year 1..9999; a claim an issuer signed
    // is still not trusted to be in range.
    private static bool IsUnixSeconds(long value) => value is >= -62135596800L and <= 253402300799L;

    /// <summary>The one minter the operator authentication boundary owns (ADR-0101 § 7).</summary>
    private sealed class WitnessMinter : PlatformOperatorWitness.Minter
    {
        internal static PlatformOperatorWitness Create(
            string issuer, string subjectDigest, string acr, DateTimeOffset authenticatedAt, string executor,
            string? correlationId) =>
            Mint(issuer, subjectDigest, acr, authenticatedAt, executor, correlationId);
    }
}
