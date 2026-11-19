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
    private const int MaximumTokenCharacters = 16 * 1024;

    /// <summary>Reads and validates the input; false means nothing was usable, and no value is reported.</summary>
    internal static bool TryRead(
        Func<string, string?> environment, Func<string?> standardInput, IConfiguration configuration, out RebindToolInput? input)
    {
        input = null;
        var connection = environment(ConnectionStringKey);
        var token = environment(TokenKey);
        if (string.IsNullOrWhiteSpace(token))
        {
            token = standardInput();
        }

        var clientId = environment(RealmClientIdKey);
        var clientSecret = environment(RealmClientSecretKey);
        var ticket = environment(TicketKey);
        if (string.IsNullOrWhiteSpace(connection) || string.IsNullOrWhiteSpace(token) || token.Length > MaximumTokenCharacters
            || string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret)
            || !Guid.TryParseExact(environment("Identity__AuthorityId"), "D", out var authority) || authority == Guid.Empty
            || string.IsNullOrEmpty(environment("Authentication__Tenant__Issuer")))
        {
            return false;
        }

        // The mode is explicit, as it is for the API: it decides whether a loopback operator issuer and the demo
        // operator's grant are admitted, so a tool that guessed it could admit what the API refuses.
        var modeText = environment("Deployment__Mode");
        if (string.IsNullOrEmpty(modeText) || !Enum.GetNames<DeploymentMode>().Contains(modeText, StringComparer.Ordinal)
            || !Enum.TryParse<DeploymentMode>(modeText, ignoreCase: false, out var mode))
        {
            // Declared names only: an ordinal ("0") or a list would parse to a mode the API itself refuses.
            return false;
        }

        try
        {
            if (new NpgsqlConnectionStringBuilder(connection).Username != IdentityIssuerRebind.RoleName)
            {
                return false;
            }

            var newIssuer = environment("Authentication__Tenant__Issuer")!;
            var target = new IdentityAuthorityRegistration(IdentityAuthorityId.From(authority), newIssuer);
            if (mode != DeploymentMode.Development && new Uri(newIssuer).Scheme != Uri.UriSchemeHttps)
            {
                // Loopback HTTP is a Development transport only: a stored issuer no other mode's API accepts would be
                // an outage, and the realm credential would travel in clear.
                return false;
            }

            input = new RebindToolInput(
                connection, target, token.Trim(), string.IsNullOrWhiteSpace(ticket) ? null : ticket,
                clientId, clientSecret, mode, configuration, environment("OTEL_EXPORTER_OTLP_ENDPOINT"));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
