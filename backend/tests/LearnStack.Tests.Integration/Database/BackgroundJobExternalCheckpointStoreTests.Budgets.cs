using System.Reflection;
using System.Text;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobExternalCheckpointStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Production_prepare_and_outcome_parameters_preserve_the_inclusive_typed_byte_boundary(bool oversizedOutcome)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var budget = BudgetFixture.Shared;
        var registry = budget.Registry(oversizedOutcome);
        var definition = registry.Resolve(BudgetFixture.Key).Definition!;
        var artifact = Guid.NewGuid();
        var expectedPlan = BudgetFixture.Json(artifact, oversized: false);
        var expectedOutcome = BudgetFixture.Json(Guid.NewGuid(), oversizedOutcome);
        Encoding.UTF8.GetByteCount(expectedPlan).Should().Be(65_536);
        Encoding.UTF8.GetByteCount(expectedOutcome).Should().Be(oversizedOutcome ? 65_537 : 65_536);
        var diagnostics = new CheckpointDiagnostics();
        await using var host = Fixture.Host(database, new BackgroundJobFixtureProbe(), configure: services =>
        {
            services.AddSingleton(diagnostics);
            services.Insert(0, ServiceDescriptor.Transient(typeof(IPipelineBehavior<,>), typeof(FixtureDiagnosticsBehavior<,>)));
            services.RemoveAll<BackgroundJobDefinitionRegistry>();
            services.AddSingleton(registry);
            BackgroundJobFixtureOperations.AddJobs(services, definition.ProfileRegistration.RootRequestType, definition.ProfileRegistration.FinalizeRequestType!);
            services.AddSingleton<IAuditCatalogSource>(budget.AuditSource(oversizedOutcome));
            services.AddSingleton(new PreparedArtifact(artifact));
            services.AddTransient(typeof(IRequestHandler<,>).MakeGenericType(definition.ProfileRegistration.RootRequestType,
                typeof(Result<>).MakeGenericType(definition.ProfileRegistration.PlanType!)),
                typeof(FixturePrepareHandler<,>).MakeGenericType(definition.ProfileRegistration.RootRequestType,
                    definition.ProfileRegistration.PlanType!));
        });
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString, BudgetFixture.Key,
            capacityGroup: BudgetFixture.Group, profile: "external");
        var receipt = await AdmitFixtureAsync(host.Services, database, await FixtureClaim.ClaimAsync(database, job));
        var prepared = await host.Services.GetRequiredService<JobExecutor>().ExecuteBusinessAsync(receipt,
            receipt.Run.OpenBoundary(BackgroundJobCheckpointKind.Prepare));
        prepared.CommitAcknowledged.Should().BeTrue("a typed compact 65,536-byte plan is allowed without JSONB expansion: {0}/{1}/{2}",
            diagnostics.FailureType, diagnostics.SqlState, diagnostics.ConstraintName);
        prepared.CleanupFailed.Should().BeFalse();
        var fingerprint = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(expectedPlan)));
        var operation = BackgroundJobExternalIdentity.OperationId(job, fingerprint);
        var armGate = receipt.Run.OpenBoundary(BackgroundJobCheckpointKind.Arm);
        (await SendCheckpointAsync(host.Services, receipt,
            new RecordBackgroundJobArmCommand(receipt, armGate, fingerprint, operation), armGate)).IsSuccess.Should().BeTrue();
        var gate = receipt.Run.OpenBoundary(BackgroundJobCheckpointKind.Outcome);
        var checkpoint = new RecordBackgroundJobOutcomeCommand(receipt, gate, fingerprint, operation,
            expectedOutcome, BackgroundJobExternalState.Succeeded, null, Guid.NewGuid(), BackgroundJobQuiescenceKind.ProviderFenced);
        var persist = () => SendCheckpointAsync(host.Services, receipt, checkpoint, gate);
        if (oversizedOutcome)
        {
            await persist.Should().ThrowAsync<ArgumentException>();
            gate.CommitAcknowledged.Should().BeFalse();
            gate.KnownRollback.Should().BeTrue();
        }
        else
        {
            (await persist()).IsSuccess.Should().BeTrue();
            gate.CommitAcknowledged.Should().BeTrue();
        }
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
        await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
        await using var read = new NpgsqlCommand("""
            SELECT j.plan,octet_length(j.plan),a.outcome,octet_length(a.outcome),r.released_at IS NOT NULL
            FROM background_jobs j JOIN background_job_attempts a ON a.id=j.current_attempt_id
            JOIN background_job_reservations r ON r.attempt_id=a.id WHERE j.id=@job
            """, (NpgsqlConnection)app, (NpgsqlTransaction)transaction);
        read.Parameters.AddWithValue("job", job.Value);
        await using var row = await read.ExecuteReaderAsync();
        (await row.ReadAsync()).Should().BeTrue();
        row.GetString(0).Should().Be(expectedPlan);
        row.GetInt32(1).Should().Be(65_536);
        if (oversizedOutcome)
        {
            row.IsDBNull(2).Should().BeTrue();
            row.IsDBNull(3).Should().BeTrue();
            row.GetBoolean(4).Should().BeFalse("refusing an oversized outcome cannot release its occupancy");
        }
        else
        {
            row.GetString(2).Should().Be(expectedOutcome);
            row.GetInt32(3).Should().Be(65_536);
            row.GetBoolean(4).Should().BeTrue();
        }
    }

    // Compiled identifier-only DTOs exercise the production frozen serializer,
    // registry and parameter writer. No string payload, callback, executable
    // adapter or special budget exception is admitted to production contracts.
    private sealed class BudgetFixture
    {
        internal const string Key = "jobscheckpointbudgetfixture.run.v1";
        internal const string Group = "jobscheckpointbudgetfixture.resource";
        private const string Prefix = "LearnStack.Modules.JobsCheckpointBudgetFixture";
        private static readonly Lazy<BudgetFixture> Instance = new(() => new());
        internal static BudgetFixture Shared => Instance.Value;
        private readonly Assembly _contracts;
        private readonly Assembly _application;
        private readonly Assembly _infrastructure;
        private readonly BackgroundJobModuleRegistration _module;

        private BudgetFixture()
        {
            var contractsSource = "using System;using LearnStack.SharedKernel.Jobs;namespace " + Prefix + ".Application.Contracts;"
                + "public sealed record Parameters(Guid ResourceId):IBackgroundJobParameters;"
                + Payload("Plan", "IBackgroundJobPlan", false)
                + Payload("Outcome", "IBackgroundJobOutcome", false)
                + Payload("OversizedOutcome", "IBackgroundJobOutcome", true)
                + "public interface IOperation:IBackgroundJobExternalOperation<Plan,Outcome>{}"
                + "public interface IOversizedOperation:IBackgroundJobExternalOperation<Plan,OversizedOutcome>{}";
            var contracts = BackgroundJobRuntimeFixture.Compile(Prefix + ".Application.Contracts", contractsSource);
            _contracts = contracts.Assembly;
            var application = BackgroundJobRuntimeFixture.Compile(Prefix + ".Application",
                "using LearnStack.SharedKernel.Jobs;using LearnStack.SharedKernel.Results;using MediatR;using " + Prefix + ".Application.Contracts;"
                + "namespace " + Prefix + ".Application;public sealed class Declarations{}"
                + "public sealed record Prepare(Parameters Parameters):IRequest<Result<Plan>>;"
                + "public sealed record Finalize(Parameters Parameters,BackgroundJobFinalization<Plan,Outcome> Finalization):IRequest<Result<None>>;"
                + "public sealed record OversizedFinalize(Parameters Parameters,BackgroundJobFinalization<Plan,OversizedOutcome> Finalization):IRequest<Result<None>>;",
                contracts.Image);
            _application = application.Assembly;
            var infrastructure = BackgroundJobRuntimeFixture.Compile(Prefix + ".Infrastructure",
                "using System;using System.Threading;using System.Threading.Tasks;using LearnStack.SharedKernel.Jobs;using "
                + Prefix + ".Application.Contracts;namespace " + Prefix + ".Infrastructure;"
                + Adapter("Operation", "IOperation", "Outcome")
                + Adapter("OversizedOperation", "IOversizedOperation", "OversizedOutcome"), contracts.Image);
            _infrastructure = infrastructure.Assembly;
            _module = new(_contracts, _application, _infrastructure);
        }

        internal BackgroundJobDefinitionRegistry Registry(bool oversized)
        {
            var prepare = Application("Prepare");
            var finalize = Application(oversized ? "OversizedFinalize" : "Finalize");
            var port = Contracts(oversized ? "IOversizedOperation" : "IOperation");
            var topology = BackgroundJobInfrastructureRegistry.Create(BackgroundJobInfrastructureRegistry.CreateDefault().Pools.Values,
                [new(Group, Application("Declarations"))], [new(port,
                    _infrastructure.GetType(Prefix + ".Infrastructure." + (oversized ? "OversizedOperation" : "Operation"), true)!)], [_module]);
            return BackgroundJobDefinitionRegistry.Create([new(Key, Application("Declarations"), Contracts("Parameters"),
                BackgroundJobScope.TenantWide, BackgroundJobDefinitionReadiness.Active, "external",
                BackgroundJobProfileRegistration.External(prepare, finalize, Contracts("Plan"),
                    Contracts(oversized ? "OversizedOutcome" : "Outcome"), port), capacityGroup: Group)],
                BackgroundJobFrameworkRequests.All.Concat(new[]
                {
                    new BackgroundJobRequestDescriptor(prepare,BackgroundJobRequestClassification.Ordinary,BackgroundJobScope.TenantWide),
                    new BackgroundJobRequestDescriptor(finalize,BackgroundJobRequestClassification.Ordinary,BackgroundJobScope.TenantWide),
                }), [_module], infrastructure: topology);
        }

        internal IAuditCatalogSource AuditSource(bool oversized) => new BudgetAuditSource(Application("Prepare"),
            Application(oversized ? "OversizedFinalize" : "Finalize"));
        private Type Application(string name) => _application.GetType(Prefix + ".Application." + name, true)!;
        private Type Contracts(string name) => _contracts.GetType(Prefix + ".Application.Contracts." + name, true)!;

        internal static string Json(Guid id, bool oversized) => "{" + string.Join(',', Names(oversized)
            .Select(name => "\"" + name + "\":\"" + id.ToString("D") + "\"")) + "}";
        private static IEnumerable<string> Names(bool oversized) => Enumerable.Range(0, 1365)
            .Select(index => "Id" + index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture)
                + (index == 1364 ? new string('a', oversized ? 16 : 15) : string.Empty));
        private static string Payload(string name, string marker, bool oversized) => "public sealed record " + name + ":" + marker
            + "{" + string.Concat(Names(oversized).Select(property => "public Guid " + property + "{get;init;}")) + "}";
        private static string Adapter(string name, string port, string outcome) =>
            "public sealed class " + name + ":" + port + "{public " + name
            + "()=>throw new InvalidOperationException(\"A checkpoint budget proof cannot construct a provider.\");"
            + "public Task<BackgroundJobExternalResult<" + outcome + ">> ExecuteAsync(BackgroundJobExternalContext context,Plan plan,CancellationToken cancellationToken=default)"
            + "=>Task.FromResult(BackgroundJobExternalResult.Unknown<" + outcome + ">());"
            + "public Task<BackgroundJobExternalResult<" + outcome + ">> ReconcileAsync(BackgroundJobExternalContext context,Plan plan,CancellationToken cancellationToken=default)"
            + "=>Task.FromResult(BackgroundJobExternalResult.Unknown<" + outcome + ">());}";

        private sealed class BudgetAuditSource(Type prepare, Type finalize) : IAuditCatalogSource
        {
            public string ModuleName => "jobscheckpointbudgetfixture";
            public void Describe(IAuditCatalogBuilder builder)
            {
                typeof(IAuditCatalogBuilder).GetMethod(nameof(IAuditCatalogBuilder.Off))!.MakeGenericMethod(prepare).Invoke(builder, null);
                typeof(IAuditCatalogBuilder).GetMethod(nameof(IAuditCatalogBuilder.Off))!.MakeGenericMethod(finalize).Invoke(builder, null);
            }
        }
    }
}
