using LearnStack.Modules.Identity.Infrastructure.Bindings;
using LearnStack.SharedKernel.Identifiers;
using Npgsql;

namespace LearnStack.Tools.IdentityAuthorityInstaller;

/// <summary>Deployment-only installation; receives migration credentials through the process environment, never argv.</summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 0) return Refuse();
        try
        {
            var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Migration");
            var authorityText = Environment.GetEnvironmentVariable("Identity__AuthorityId");
            var issuer = Environment.GetEnvironmentVariable("Authentication__Tenant__Issuer");
            if (string.IsNullOrWhiteSpace(connectionString)
                || !Guid.TryParseExact(authorityText, "D", out var authorityId) || authorityId == Guid.Empty
                || issuer is null
                || new NpgsqlConnectionStringBuilder(connectionString).Username != "learnstack_migration") return Refuse();
            var registration = new IdentityAuthorityRegistration(IdentityAuthorityId.From(authorityId), issuer);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(deadline.Token);
            await IdentityAuthorityDeploymentInstaller.InstallAsync(connection, registration, deadline.Token);
            Console.WriteLine("identity-authority: installed or verified without changing an existing registration.");
            return 0;
        }
#pragma warning disable CA1031 // The deployment CLI emits fixed diagnostics, never provider/credential exception payloads.
        catch (Exception)
#pragma warning restore CA1031
        {
            return Refuse();
        }
    }

    private static int Refuse()
    {
        Console.Error.WriteLine("identity-authority: installation refused; verify the migration role, stable Identity__AuthorityId and exact Authentication__Tenant__Issuer. Existing registrations are never repaired or rebound.");
        return 2;
    }
}
