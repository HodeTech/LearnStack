using LearnStack.Infrastructure.PlatformOperator;
using LearnStack.Modules.Identity.Infrastructure.Bindings;
using LearnStack.SharedKernel.Hosting;
using LearnStack.SharedKernel.Identifiers;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace LearnStack.Tools.IdentityAuthorityRebind;

/// <summary>What the operator hands the tool, read from the process environment and one line of standard input.</summary>
/// <remarks>
/// Nothing is an argument: an argument is visible to every user of the host and survives in shell history. The
/// operator token comes from <c>LEARNSTACK_OPERATOR_TOKEN</c> or, when that is unset, the first line of standard
/// input. The realm credential is a <c>view-users</c> client the operator creates for the run and deletes after it.
/// </remarks>
internal sealed record RebindToolInput(
    string ConnectionString,
    IdentityAuthorityRegistration Target,
    string OperatorToken,
    string? Ticket,
    string RealmClientId,
    string RealmClientSecret,
    DeploymentMode Mode,
    IConfiguration Configuration,
    string? OtlpEndpoint)
{
    internal const string ConnectionStringKey = "ConnectionStrings__IdentityRebind";
    internal const string TokenKey = "LEARNSTACK_OPERATOR_TOKEN";
    internal const string TicketKey = "LEARNSTACK_REBIND_TICKET";
    internal const string RealmClientIdKey = "LEARNSTACK_REBIND_REALM_CLIENT_ID";
    internal const string RealmClientSecretKey = "LEARNSTACK_REBIND_REALM_CLIENT_SECRET";
    internal const string AuthorityKey = "Identity__AuthorityId";
    internal const string IssuerKey = "Authentication__Tenant__Issuer";
    internal const string ModeKey = "Deployment__Mode";

    /// <summary>
    /// The closed names a refused input is reported under: the setting that was unusable, never its value. The
    /// connection string covers a missing value, one that does not parse and one that names another role.
    /// </summary>
    internal static class Fields
    {
        internal const string Arguments = "arguments";
        internal const string ConnectionString = "connection_string";
        internal const string OperatorToken = "operator_token";
        internal const string RealmClient = "realm_client";
        internal const string AuthorityId = "authority_id";
        internal const string Issuer = "issuer";
        internal const string DeploymentMode = "deployment_mode";

        /// <summary>Every name, so the runbook and a test can hold the set closed.</summary>
        internal static readonly string[] All =
            [Arguments, ConnectionString, OperatorToken, RealmClient, AuthorityId, Issuer, DeploymentMode];
    }

    /// <summary>
    /// Reads and validates the input. False means nothing was usable; <paramref name="field"/> then names the first
    /// setting that was not, and no value is reported.
    /// </summary>
    internal static bool TryRead(
        Func<string, string?> environment,
        Func<string?> standardInput,
        IConfiguration configuration,
        out RebindToolInput? input,
        out string field)
    {
        input = null;
        field = Fields.ConnectionString;
        var connection = environment(ConnectionStringKey);
        if (string.IsNullOrWhiteSpace(connection))
        {
            return false;
        }

        var token = environment(TokenKey);
        if (string.IsNullOrWhiteSpace(token))
        {
            token = standardInput();
        }

        field = Fields.OperatorToken;
        if (string.IsNullOrWhiteSpace(token) || token.Length > PlatformOperatorTokenValidator.MaximumTokenCharacters)
        {
            return false;
        }

        var clientId = environment(RealmClientIdKey);
        var clientSecret = environment(RealmClientSecretKey);
        field = Fields.RealmClient;
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            return false;
        }

        field = Fields.AuthorityId;
        if (!Guid.TryParseExact(environment(AuthorityKey), "D", out var authority) || authority == Guid.Empty)
        {
            return false;
        }

        field = Fields.Issuer;
        var newIssuer = environment(IssuerKey);
        if (string.IsNullOrEmpty(newIssuer))
        {
            return false;
        }

        // The mode is explicit, as it is for the API and read the way the API reads it: it decides whether a loopback
        // operator issuer and the demo operator's grant are admitted, so a tool that guessed it could admit what the
        // API refuses.
        field = Fields.DeploymentMode;
        if (!DeploymentModeParser.TryParse(environment(ModeKey), out var mode))
        {
            return false;
        }

        try
        {
            field = Fields.ConnectionString;
            if (new NpgsqlConnectionStringBuilder(connection).Username != IdentityIssuerRebind.RoleName)
            {
                return false;
            }

            field = Fields.Issuer;
            var target = new IdentityAuthorityRegistration(IdentityAuthorityId.From(authority), newIssuer);
            if (mode != DeploymentMode.Development && new Uri(newIssuer).Scheme != Uri.UriSchemeHttps)
            {
                // Loopback HTTP is a Development transport only: a stored issuer no other mode's API accepts would be
                // an outage, and the realm credential would travel in clear.
                return false;
            }

            var ticket = environment(TicketKey);
            input = new RebindToolInput(
                connection, target, token.Trim(), string.IsNullOrWhiteSpace(ticket) ? null : ticket,
                clientId, clientSecret, mode, configuration, environment("OTEL_EXPORTER_OTLP_ENDPOINT"));
            field = string.Empty;
            return true;
        }
        catch (Exception failure) when (failure is ArgumentException or UriFormatException)
        {
            return false;
        }
    }
}
