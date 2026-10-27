using LearnStack.SharedKernel.Tenancy;
using LearnStack.Tools.Seeder;
using Microsoft.Extensions.Logging;
using LearnStack.Infrastructure.Persistence;
using System.Text.Json;
using System.Runtime.InteropServices;
using LearnStack.Modules.Identity.Infrastructure.Bindings;
using LearnStack.SharedKernel.Identifiers;

// Read-only tooling projection: no data source, seeding side effect or credentials.
if (args is ["--demo-identities"])
{
    Console.WriteLine(JsonSerializer.Serialize(DemoIdentities.Create(), JsonSerializerOptions.Web));
    return 0;
}
if (args is ["--demo-manifest"])
{
    Console.WriteLine(JsonSerializer.Serialize(DemoManifest.Create(), JsonSerializerOptions.Web));
    return 0;
}
if (args is ["--identity-manifest"])
{
    try
    {
        Console.WriteLine(JsonSerializer.Serialize(SeedIdentityManifest.Create(), JsonSerializerOptions.Web));
        return 0;
    }
    catch (InvalidOperationException)
    {
        Console.Error.WriteLine("seed: development identity declaration is inconsistent.");
        return 2;
    }
}
var identityFoundation = args is ["--identity-foundation-stdin"];
if (args.Length != 0 && !identityFoundation)
{
    Console.Error.WriteLine("seed: unsupported arguments.");
    return 2;
}

// The seeder is a host without an HTTP surface, and it exists so the two demo tenants are
// written by the same commands a request writes them with — ADR-0042 requires that: a
// seeder inserting the tenant and its default organization itself would be a second copy
// of the one sanctioned cross-aggregate write.

// The environment only. There is no --connection-string flag: the value carries a
// database password, and an argument is visible to every local user through `ps` for as
// long as the process runs. `make seed` already passes it this way.
//
// And NOT through IConfiguration's GetConnectionString: exactly one file in the solution
// reads credentials that way, and Platform_DataSource_Resolved_Only_By_PlatformAdminScope
// keeps it that way so one file decides what is done with them.
var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Default");

if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine(
        "seed: ConnectionStrings__Default is not set. Run `make seed`, which reads it "
        + "from .env and checks the role, or export it yourself (see .env.example).");
    return 2;
}

// One data source for the whole run, shared by every per-act provider: a seeder that
// opened a pool per command would leave an idle connection behind for each one.
using var loggerFactory = LoggerFactory.Create(logging => logging.AddSimpleConsole());
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
using var terminate = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM,
    signal => { signal.Cancel = true; cancellation.Cancel(); });
using var hangup = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGHUP,
    signal => { signal.Cancel = true; cancellation.Cancel(); });

var sourceStartupQualified = false;
try
{
    if (!Guid.TryParseExact(Environment.GetEnvironmentVariable("Identity__AuthorityId"), "D", out var authorityId))
        throw new InvalidOperationException("Identity authority configuration is invalid.");
    var registration = new IdentityAuthorityRegistration(IdentityAuthorityId.From(authorityId),
        Environment.GetEnvironmentVariable("Authentication__Tenant__Issuer") ?? "");
    var auxiliary = AuthorizationSourceConfiguration.Resolve(connectionString,
        Environment.GetEnvironmentVariable("ConnectionStrings__IdentityAuthorization"),
        Environment.GetEnvironmentVariable("ConnectionStrings__AuthorizationEvidence"),
        allowDevelopmentDerivation: string.Equals(Environment.GetEnvironmentVariable("Deployment__Mode"),
            "Development", StringComparison.Ordinal));
    await using var dataSource = ApplicationDataSource.Build(connectionString);
    // The process owns one bounded evidence lane across every per-act provider.
    // Qualification only reads the installed anchor; schema-only migration must
    // install it before running this application writer.
    var qualification = new IdentityAuthorityQualification(new Lazy<Npgsql.NpgsqlDataSource>(() => dataSource), registration);
    await qualification.QualifyAsync(cancellation.Token);
    await using var evidence = new AuthorizationEvidenceSource(auxiliary.AuthorizationEvidence);
    var proof = await qualification.OpenDatabaseQualificationProofAsync(cancellation.Token);
    var qualificationDeadline = proof.DeadlineToken;
    await using (proof)
        await evidence.QualifyAsync(registration.AuthorityId, registration.Issuer, proof, qualificationDeadline);
    qualificationDeadline.ThrowIfCancellationRequested();
    if (!string.Equals(qualification.QualifiedDatabaseName, evidence.QualifiedDatabaseName, StringComparison.Ordinal))
        throw new InvalidOperationException("Authorization evidence must use the qualified application database.");
    sourceStartupQualified = true;
    if (identityFoundation)
    {
        var witness = await SeedProviderWitness.ReadAsync(Console.OpenStandardInput(), registration, cancellation.Token);
        var admission = new FixedSeedIdentityFoundationAdmission(witness);
        var identityRunner = new SeedIdentityFoundationRunner(
            context => SeedComposition.Build(dataSource, context, loggerFactory,
                identityAuthority: registration, seedAdmission: admission, authorizationEvidence: evidence), admission);
        await identityRunner.RunAsync(cancellation.Token);
        return 0;
    }
    var runner = new SeedRunner(
        context => SeedComposition.Build(dataSource, context, loggerFactory,
            identityAuthority: registration, authorizationEvidence: evidence),
        loggerFactory.CreateLogger<SeedRunner>());
    return await runner.RunAsync(cancellation.Token);
}
catch (Exception failure)
{
    if (!sourceStartupQualified)
    {
        // Provider/authentication failures can contain endpoint or role details.
        // The new startup gate owns a fixed diagnostic, including its cleanup;
        // it never passes the provider exception into ordinary command logging.
        Console.Error.WriteLine("seed: authorization source configuration or startup qualification was refused.");
        return 1;
    }
    if (identityFoundation)
    {
        Console.Error.WriteLine("seed: development identity foundation was refused.");
        return 1;
    }
    // Non-zero, and nothing else: `make seed` is a gate, and a seeder that reported
    // success after failing would hand the next step a database it cannot use. The
    // message reaches the operator through the logger, which is already on the console.
    SeedLog.Failed(loggerFactory.CreateLogger<SeedRunner>(), failure);
    return 1;
}

