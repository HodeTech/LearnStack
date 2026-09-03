using LearnStack.SharedKernel.Tenancy;
using LearnStack.Tools.Seeder;
using Microsoft.Extensions.Logging;
using Npgsql;

// The seeder is a host without an HTTP surface, and it exists so the two demo tenants are
// written by the same commands a request writes them with — ADR-0042 requires that: a
// seeder inserting the tenant and its default organization itself would be a second copy
// of the one sanctioned cross-aggregate write.

// Read from the flag or the environment, and NOT through IConfiguration's
// GetConnectionString: exactly one file in the solution reads credentials that way, and
// Platform_DataSource_Resolved_Only_By_PlatformAdminScope keeps it that way so one file
// decides what is done with them. `make seed` passes it in the environment, because an
// argument carrying a database password is visible to any local user through `ps`.
var connectionString = ConnectionStringFrom(args)
    ?? Environment.GetEnvironmentVariable("ConnectionStrings__Default");

if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine(
        "seed: no connection string. Pass --connection-string, or set "
        + "ConnectionStrings__Default (see .env.example).");
    return 2;
}

// One data source for the whole run, shared by every per-act provider: a seeder that
// opened a pool per command would leave an idle connection behind for each one.
await using var dataSource = NpgsqlDataSource.Create(connectionString);
using var loggerFactory = LoggerFactory.Create(logging => logging.AddSimpleConsole());

var runner = new SeedRunner(
    context => SeedComposition.Build(dataSource, context, loggerFactory),
    loggerFactory.CreateLogger<SeedRunner>());

try
{
    return await runner.RunAsync(CancellationToken.None);
}
catch (Exception failure)
{
    // Non-zero, and nothing else: `make seed` is a gate, and a seeder that reported
    // success after failing would hand the next step a database it cannot use. The
    // message reaches the operator through the logger, which is already on the console.
    SeedLog.Failed(loggerFactory.CreateLogger<SeedRunner>(), failure);
    return 1;
}

static string? ConnectionStringFrom(string[] args)
{
    var flag = Array.IndexOf(args, "--connection-string");
    return flag >= 0 && flag + 1 < args.Length ? args[flag + 1] : null;
}
