using Npgsql;

namespace LearnStack.Infrastructure.Persistence;

/// <summary>The coupled auxiliary endpoints; their values never enter diagnostic text.</summary>
public sealed class AuthorizationSourceConfiguration
{
    private AuthorizationSourceConfiguration(string authorization, string evidence)
    {
        IdentityAuthorization = authorization;
        AuthorizationEvidence = evidence;
    }

    public string IdentityAuthorization { get; }
    public string AuthorizationEvidence { get; }

    /// <summary>Selects an explicit pair, or derives the owned direct-PostgreSQL Development topology.</summary>
    /// <remarks>
    /// The composition root alone supplies the derivation permission. Direct PostgreSQL is its
    /// operational precondition, not something an arbitrary endpoint string can prove. PgBouncer
    /// always needs the explicit pair and separately qualified backend reservations.
    /// </remarks>
    public static AuthorizationSourceConfiguration Resolve(string? business, string? authorization, string? evidence,
        bool allowDevelopmentDerivation)
    {
        ValidateApplicationConnection(business, "ConnectionStrings:Default");
        var hasAuthorization = !string.IsNullOrWhiteSpace(authorization);
        var hasEvidence = !string.IsNullOrWhiteSpace(evidence);
        if (hasAuthorization != hasEvidence)
            throw new InvalidOperationException("IdentityAuthorization and AuthorizationEvidence connection strings must be supplied together.");
        if (!hasAuthorization)
        {
            var parsed = new NpgsqlConnectionStringBuilder(business!);
            if (!allowDevelopmentDerivation || parsed.Database is "learnstack_identity_authorization" or "learnstack_authorization_evidence")
                throw new InvalidOperationException("Explicit authorization and evidence connection strings are required for this topology.");
            authorization = business;
            evidence = business;
        }
        ValidateApplicationConnection(authorization, "ConnectionStrings:IdentityAuthorization");
        ValidateApplicationConnection(evidence, "ConnectionStrings:AuthorizationEvidence");
        return new AuthorizationSourceConfiguration(authorization!, evidence!);
    }

    internal static void ValidateApplicationConnection(string? connectionString, string key)
    {
        try { ApplicationDataSource.Validate(connectionString); }
        catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidOperationException)
        {
            throw new InvalidOperationException($"{key} must configure a valid guarded application-role connection string.");
        }
    }

    public override string ToString() => nameof(AuthorizationSourceConfiguration);
}
