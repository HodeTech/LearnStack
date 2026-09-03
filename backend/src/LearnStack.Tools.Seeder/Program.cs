using LearnStack.Application.Pipeline;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using LearnStack.Tools.Seeder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

// The seeder is a second composition root, deliberately minimal: the module's handlers,
// the MediatR pipeline, the ambient unit of work and the module context. It shares those
// with the API because sharing them is the point — a seed that ran through its own write
// path would prove nothing about the request path. It shares nothing else; there is no
// HTTP surface here to configure.

// `--connection-string <value>` wins; otherwise the same environment variable the compose
// stack and `.env.example` already define, so `make seed` needs no new configuration.
// Read from the flag or the environment, and NOT through IConfiguration's
// GetConnectionString: exactly one file in the solution reads credentials that way, and
// Platform_DataSource_Resolved_Only_By_PlatformAdminScope keeps it that way so one file
// decides what is done with them. A console tool taking an explicit argument needs no
// configuration stack at all.
var connectionString = ConnectionStringFrom(args)
    ?? Environment.GetEnvironmentVariable("ConnectionStrings__Default");

if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine(
        "seed: no connection string. Pass --connection-string, or set "
        + "ConnectionStrings__Default (see .env.example).");
    return 2;
}

// One data source for the whole run, shared by every per-act provider below: a seeder
// that opened a pool per command would spend more time connecting than writing.
var dataSource = NpgsqlDataSource.Create(connectionString);

// A provider per act, each composed around the context that act runs under. The seeder
// never assigns ITenantContextAccessor.Current — writes to that member are a closed set
// of four (ADR-0036 Amendment 2), and a development tool is not a reason to widen a
// security enumeration when composition costs nothing.
ServiceProvider Compose(ITenantContext? context)
{
    var services = new ServiceCollection();

    services.AddSingleton(dataSource);
    services.AddLogging(logging => logging.AddSimpleConsole());
    services.AddSingleton<IClock, SystemClock>();
    services.AddSingleton<ITenantContextAccessor>(new StaticTenantContextAccessor(context));
    services.AddTransient<ITenantContext>(provider =>
        provider.GetRequiredService<ITenantContextAccessor>().Current
        ?? UnresolvedTenantContext.Instance);

    services.AddScoped<IUnitOfWork, NpgsqlUnitOfWork>();
    services.AddModuleDbContext<TenancyDbContext>();
    services.AddScoped<ITenantWriteStore, TenantWriteStore>();
    services.AddScoped<IOrganizationWriteStore, OrganizationWriteStore>();
    services.AddScoped<IPlatformHostMappingStore, PlatformHostMappingStore>();
    services.AddLearnStackMediatRPipeline(typeof(ITenantWriteStore).Assembly);

    return services.BuildServiceProvider();
}

using var loggerFactory = LoggerFactory.Create(logging => logging.AddSimpleConsole());
var logger = loggerFactory.CreateLogger<SeedRunner>();

try
{
    return await new SeedRunner(Compose, logger).RunAsync(CancellationToken.None);
}
catch (Exception failure)
{
    // Non-zero, and the message on stderr: `make seed` is a gate, and a seeder that
    // reported success after failing would hand the next step a database it cannot use.
    SeedLog.Failed(logger, failure);
    Console.Error.WriteLine($"seed: {failure.Message}");
    return 1;
}

static string? ConnectionStringFrom(string[] args)
{
    var flag = Array.IndexOf(args, "--connection-string");
    return flag >= 0 && flag + 1 < args.Length ? args[flag + 1] : null;
}
